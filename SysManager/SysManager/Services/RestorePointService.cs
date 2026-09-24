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

    /// <summary>Printed by <see cref="BuildCreateScript"/> only after Windows confirmed a new restore point.</summary>
    internal const string CreateOkSentinel = "__SM_RP_CREATED__";

    /// <summary>Printed by <see cref="BuildRestoreScript"/> only after Windows accepted the restore request.</summary>
    internal const string RestoreStartedSentinel = "__SM_RP_RESTORE_STARTED__";

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
    /// <exception cref="ArgumentNullException"><paramref name="objects"/> is null.</exception>
    public static IReadOnlyList<RestorePoint> ParseRestorePoints(IEnumerable<PSObject> objects)
    {
        // The individual nulls below are handled and the collection itself was not, which is the asymmetry
        // worth closing on a method that is public and documented for direct use (#2259). Not reachable from
        // production — PowerShellRunner.RunAsync always returns a collection — so this is about the contract
        // a caller can rely on, and about failing at the boundary instead of somewhere inside the loop.
        ArgumentNullException.ThrowIfNull(objects);

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
    /// Creates a restore point. Requires admin. Returns true only when the script confirms Windows made one.
    /// </summary>
    public async Task<bool> CreateAsync(string description, CancellationToken ct = default)
    {
        try
        {
            var results = await _ps.RunAsync(BuildCreateScript(description), cancellationToken: ct).ConfigureAwait(false);
            var ok = Confirms(results, CreateOkSentinel);
            if (!ok)
                Log.Warning("RestorePoint: Checkpoint-Computer did not confirm success (it may be rate-limited to one per 24h).");
            return ok;
        }
        catch (RuntimeException ex)
        {
            Log.Warning("RestorePoint: creation failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The script <see cref="CreateAsync"/> runs: success is the sentinel it prints last, never the absence of
    /// an exception.
    /// </summary>
    /// <remarks>
    /// Windows allows one restore point per 24 hours, and Windows PowerShell 5.1 — the engine an elevated run
    /// uses — reports the refusal as a WARNING (its resource is <c>CannotCreateRestorePointWarning</c>), not as
    /// an error. <c>-ErrorAction Stop</c> does not govern warnings, so without <c>-WarningAction Stop</c> the
    /// script went on to print the sentinel and every caller reported "Restore point created" for a point
    /// Windows had declined to make (#2436). An earlier comment here called the refusal a non-terminating
    /// error, which is what the design was built around.
    /// <para>Internal so the integration suite can run this exact text in a real Windows PowerShell 5.1 with the
    /// restore-point cmdlets shadowed by functions, which is the only way to test the semantics it depends
    /// on. The single user-supplied value, the description, is single-quote-escaped.</para>
    /// </remarks>
    internal static string BuildCreateScript(string? description)
    {
        var safeDesc = (string.IsNullOrWhiteSpace(description) ? "SysManager Restore Point" : description)
            .Replace("'", "''");
        return "try { " +
               "Enable-ComputerRestore -Drive $env:SystemDrive -ErrorAction SilentlyContinue; " +
               $"Checkpoint-Computer -Description '{safeDesc}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop -WarningAction Stop; " +
               $"'{CreateOkSentinel}' " +
               "} catch { Write-Error $_; exit 1 }";
    }

    /// <summary>
    /// Restores the system to the given restore point. Requires admin and TRIGGERS A REBOOT.
    /// The caller MUST confirm with the user first. Returns true only when the script confirms Windows accepted
    /// the request (the machine then restarts, so a true return is rarely observed).
    /// </summary>
    public async Task<bool> RestoreAsync(int sequenceNumber, CancellationToken ct = default)
    {
        try
        {
            var results = await _ps.RunAsync(BuildRestoreScript(sequenceNumber), cancellationToken: ct).ConfigureAwait(false);
            var started = Confirms(results, RestoreStartedSentinel);
            if (!started)
                Log.Warning("RestorePoint: Restore-Computer did not confirm the restore to #{Seq}.", sequenceNumber);
            return started;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Warning("RestorePoint: restore to #{Seq} failed: {Error}", sequenceNumber, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The script <see cref="RestoreAsync"/> runs, confirmed the same way as <see cref="BuildCreateScript"/>.
    /// </summary>
    /// <remarks>
    /// It used to report success whenever <c>RunAsync</c> did not throw. The <c>catch</c> ends in
    /// <c>Write-Error $_; exit 1</c>, and <c>Write-Error</c> is non-terminating; <c>RunAsync</c> returns normally
    /// when only the error stream has records. So a failed <c>Restore-Computer</c> read as "Restore initiated
    /// — the system will restart." with no restart coming (#2436). No <c>-WarningAction Stop</c> here: a warning
    /// from a restore Windows has already accepted must not turn into a report that it failed while the
    /// machine restarts. The sequence number is an int, so embedding it is injection-safe.
    /// </remarks>
    internal static string BuildRestoreScript(int sequenceNumber) =>
        $"try {{ Restore-Computer -RestorePoint {sequenceNumber} -Confirm:$false -ErrorAction Stop; '{RestoreStartedSentinel}' }} " +
        "catch { Write-Error $_; exit 1 }";

    /// <summary>Whether a script's output carries its success sentinel — the one signal both scripts trust.</summary>
    private static bool Confirms(IEnumerable<PSObject?> results, string sentinel) =>
        results.Any(o => string.Equals(o?.BaseObject?.ToString(), sentinel, StringComparison.Ordinal));
}
