// SysManager · RestorePointService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Lists, creates, and restores Windows System Restore points via PowerShell.
///
/// All PowerShell goes through the <see cref="IPowerShellRunner"/> seam so the parsing
/// and orchestration can be unit-tested with a substituted runner (Gate-ARCH). Creating
/// and restoring require administrator rights; restoring triggers a reboot and is gated
/// behind an explicit confirmation in the ViewModel.
///
/// SECURITY: only hard-coded scripts are passed to the runner. The single user-supplied
/// value (a restore-point description) is single-quote-escaped before being embedded,
/// matching <see cref="PerformanceService.CreateRestorePointAsync"/>.
/// </summary>
public sealed class RestorePointService
{
    private readonly IPowerShellRunner _ps;

    private const string CreateOkSentinel = "__SM_RP_CREATED__";

    public RestorePointService(IPowerShellRunner ps) => _ps = ps;

    /// <summary>
    /// Lists existing restore points, newest first. Returns an empty list if System
    /// Restore is disabled or the query is denied (logged at Debug).
    /// </summary>
    public async Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken ct = default)
    {
        // Get-ComputerRestorePoint surfaces SequenceNumber, Description, CreationTime
        // (a WMI CIM_DATETIME string), RestorePointType, and EventType.
        const string script =
            "Get-ComputerRestorePoint | Select-Object SequenceNumber, Description, " +
            "@{N='CreationTimeIso';E={ $_.ConvertToDateTime($_.CreationTime).ToString('o') }}, " +
            "RestorePointType, EventType";
        try
        {
            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            return ParseRestorePoints(results);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("RestorePoint: list failed (System Restore may be disabled): {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Parses <c>Get-ComputerRestorePoint</c> output into <see cref="RestorePoint"/> records,
    /// sorted newest-first. Pure and runner-agnostic so it can be unit-tested directly.
    /// </summary>
    public static IReadOnlyList<RestorePoint> ParseRestorePoints(IEnumerable<PSObject> objects)
    {
        List<RestorePoint> points = [];
        foreach (var obj in objects)
        {
            if (obj is null) continue;
            var seq = ToInt(obj.Properties["SequenceNumber"]?.Value);
            if (seq is null) continue;

            var description = obj.Properties["Description"]?.Value?.ToString()?.Trim() ?? "";
            var type = obj.Properties["RestorePointType"]?.Value?.ToString()?.Trim() ?? "";
            var eventType = obj.Properties["EventType"]?.Value?.ToString()?.Trim() ?? "";

            DateTime created = default;
            var iso = obj.Properties["CreationTimeIso"]?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(iso))
                DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created);

            points.Add(new RestorePoint(seq.Value, description, created, type, eventType));
        }
        return [.. points.OrderByDescending(p => p.SequenceNumber)];
    }

    private static int? ToInt(object? value) => value switch
    {
        null => null,
        int i => i,
        uint u => (int)u,
        long l => (int)l,
        _ => int.TryParse(value.ToString(), out var n) ? n : null
    };

    /// <summary>
    /// Creates a restore point. Requires admin. Returns true only on confirmed success;
    /// Windows rate-limits creation to one per 24h and reports that as a non-terminating
    /// error, so a success sentinel is required rather than the absence of an exception.
    /// </summary>
    public async Task<bool> CreateAsync(string description, CancellationToken ct = default)
    {
        var safeDesc = (string.IsNullOrWhiteSpace(description) ? "SysManager Restore Point" : description)
            .Replace("'", "''");
        var script =
            "try { " +
            "Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue; " +
            $"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; " +
            $"'{CreateOkSentinel}' " +
            "} catch { Write-Error $_; exit 1 }";
        var results = await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
        var ok = results.Any(o => string.Equals(o?.BaseObject?.ToString(), CreateOkSentinel, StringComparison.Ordinal));
        if (!ok)
            Log.Warning("RestorePoint: Checkpoint-Computer did not confirm success (it may be rate-limited to one per 24h).");
        return ok;
    }

    /// <summary>
    /// Restores the system to the given restore point. Requires admin and TRIGGERS A REBOOT.
    /// The caller MUST confirm with the user first. Returns false if the request could not
    /// be issued (the machine reboots on success, so a true return is rarely observed).
    /// </summary>
    public async Task<bool> RestoreAsync(int sequenceNumber, CancellationToken ct = default)
    {
        // SequenceNumber is an int we validate ourselves, so embedding it is injection-safe.
        var script =
            $"try {{ Restore-Computer -RestorePoint {sequenceNumber} -Confirm:$false -ErrorAction Stop }} " +
            "catch { Write-Error $_; exit 1 }";
        try
        {
            await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Warning("RestorePoint: restore to #{Seq} failed: {Error}", sequenceNumber, ex.Message);
            return false;
        }
    }
}
