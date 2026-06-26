// SysManager · DefenderService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and tweaks Microsoft Defender settings through the Defender PowerShell module
/// (Get-MpPreference / Set-MpPreference / Add-MpPreference / Remove-MpPreference) via the
/// shared <see cref="IPowerShellRunner"/> seam.
///
/// Two correctness rules baked in:
/// 1. Several Defender properties are inverted "Disable" booleans; they are normalized
///    to positive meaning when read.
/// 2. Tamper Protection can SILENTLY reject a Set (it "appears to succeed but is
///    ignored"), and the runner forwards PowerShell errors to its line stream rather
///    than throwing — so every change is verified by reading the value back and
///    comparing, never by trusting the Set call.
///
/// Changing Defender requires administrator; without it the Set is rejected and the
/// read-back simply shows no change, surfaced cleanly to the user.
/// </summary>
public sealed class DefenderService
{
    private readonly IPowerShellRunner _ps;

    public DefenderService(IPowerShellRunner ps) => _ps = ps;

    /// <summary>Read the current Defender status, or <see cref="DefenderStatus.Unavailable"/>.</summary>
    public async Task<DefenderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            const string script = """
                $p = Get-MpPreference
                $s = Get-MpComputerStatus
                [PSCustomObject]@{
                    DisableRealtimeMonitoring   = $p.DisableRealtimeMonitoring
                    PUAProtection               = [int]$p.PUAProtection
                    MAPSReporting               = [int]$p.MAPSReporting
                    EnableControlledFolderAccess = [int]$p.EnableControlledFolderAccess
                    ExclusionPath               = @($p.ExclusionPath)
                    ExclusionExtension          = @($p.ExclusionExtension)
                    ExclusionProcess            = @($p.ExclusionProcess)
                    IsTamperProtected           = [bool]$s.IsTamperProtected
                }
                """;
            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            if (results.Count == 0) return DefenderStatus.Unavailable;
            return ParseStatus(results[0]);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Defender status read failed: {Error}", ex.Message);
            return DefenderStatus.Unavailable;
        }
    }

    /// <summary>Parse a Get-MpPreference/Get-MpComputerStatus projection into a status.</summary>
    public static DefenderStatus ParseStatus(PSObject obj)
    {
        bool disableRtp = ToBool(Prop(obj, "DisableRealtimeMonitoring"));
        return new DefenderStatus(
            Available: true,
            IsTamperProtected: ToBool(Prop(obj, "IsTamperProtected")),
            RealtimeProtection: !disableRtp, // normalize the inverted "Disable" boolean
            PuaProtection: ToInt(Prop(obj, "PUAProtection")),
            MapsReporting: ToInt(Prop(obj, "MAPSReporting")),
            ControlledFolderAccess: ToInt(Prop(obj, "EnableControlledFolderAccess")),
            ExclusionPaths: ToStringList(Prop(obj, "ExclusionPath")),
            ExclusionExtensions: ToStringList(Prop(obj, "ExclusionExtension")),
            ExclusionProcesses: ToStringList(Prop(obj, "ExclusionProcess")));
    }

    /// <summary>
    /// Set PUA protection (0/1/2) and verify the change took effect. Returns the
    /// re-read status; compare its PuaProtection to confirm success.
    /// </summary>
    public Task<DefenderStatus> SetPuaProtectionAsync(int value, CancellationToken ct = default)
        => ApplyAndVerifyAsync("Set-MpPreference -PUAProtection $Value", new() { ["Value"] = ClampTri(value) }, ct);

    /// <summary>Set Controlled Folder Access (0/1/2) and verify.</summary>
    public Task<DefenderStatus> SetControlledFolderAccessAsync(int value, CancellationToken ct = default)
        => ApplyAndVerifyAsync("Set-MpPreference -EnableControlledFolderAccess $Value", new() { ["Value"] = ClampTri(value) }, ct);

    /// <summary>Add a folder exclusion (additive — never replaces the array). Verifies.</summary>
    public Task<DefenderStatus> AddExclusionPathAsync(string path, CancellationToken ct = default)
        => ApplyAndVerifyAsync("Add-MpPreference -ExclusionPath $Path", new() { ["Path"] = path }, ct);

    /// <summary>Remove a folder exclusion. Verifies.</summary>
    public Task<DefenderStatus> RemoveExclusionPathAsync(string path, CancellationToken ct = default)
        => ApplyAndVerifyAsync("Remove-MpPreference -ExclusionPath $Path", new() { ["Path"] = path }, ct);

    /// <summary>
    /// Run a hard-coded Set/Add/Remove script with bound parameters (never interpolated),
    /// then return a fresh status read so the caller can verify the change actually applied
    /// (Tamper Protection / missing admin can silently reject it).
    /// </summary>
    private async Task<DefenderStatus> ApplyAndVerifyAsync(string script, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        try
        {
            await _ps.RunAsync(script, parameters, ct).ConfigureAwait(false);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Defender set failed: {Error}", ex.Message);
        }
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    // ── Pure parse helpers (testable) ──────────────────────────────────
    private static object? Prop(PSObject obj, string name) => obj.Properties[name]?.Value;

    internal static bool ToBool(object? v) => v switch
    {
        bool b => b,
        not null when bool.TryParse(v.ToString(), out bool r) => r,
        _ => false,
    };

    internal static int ToInt(object? v)
    {
        if (v is null) return 0;
        try { return Convert.ToInt32(v); }
        catch (FormatException) { return 0; }
        catch (InvalidCastException) { return 0; }
        catch (OverflowException) { return 0; }
    }

    internal static IReadOnlyList<string> ToStringList(object? v)
    {
        if (v is null) return [];
        if (v is object[] arr)
            return arr.Where(x => x is not null).Select(x => x.ToString() ?? "").Where(s => s.Length > 0).ToList();
        string single = v.ToString() ?? "";
        return single.Length > 0 ? [single] : [];
    }

    internal static int ClampTri(int value) => value is >= 0 and <= 2 ? value : 0;
}
