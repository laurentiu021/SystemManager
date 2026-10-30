// SysManager · DefenderService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections;
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

    /// <summary>
    /// The status read. Both reads stop on failure.
    /// </summary>
    /// <remarks>
    /// Without <c>-ErrorAction Stop</c>, a failed <c>Get-MpPreference</c> left <c>$p</c> null and the object
    /// below was still emitted, with every field null. <see cref="ParseStatus"/> turns a null "Disable" flag
    /// into protection ON, so a status that was never read showed real-time protection "On" (#2476). A failed
    /// read now throws, and <see cref="GetStatusAsync"/> reports it as <see cref="DefenderStatus.Unavailable"/>.
    /// <para><c>internal</c> so a test can run this exact text against shadowed Defender cmdlets.</para>
    /// </remarks>
    internal const string StatusScript = """
        $p = Get-MpPreference -ErrorAction Stop
        $s = Get-MpComputerStatus -ErrorAction Stop
        [PSCustomObject]@{
            DisableRealtimeMonitoring   = $p.DisableRealtimeMonitoring
            PUAProtection               = [int]$p.PUAProtection
            MAPSReporting               = [int]$p.MAPSReporting
            EnableControlledFolderAccess = [int]$p.EnableControlledFolderAccess
            ExclusionPath               = @($p.ExclusionPath)
            ExclusionExtension          = @($p.ExclusionExtension)
            ExclusionProcess            = @($p.ExclusionProcess)
            IsTamperProtected           = [bool]$s.IsTamperProtected
            RealTimeProtectionEnabled   = $s.RealTimeProtectionEnabled
            AMRunningMode               = [string]$s.AMRunningMode
        }
        """;

    /// <summary>Read the current Defender status, or <see cref="DefenderStatus.Unavailable"/>.</summary>
    public async Task<DefenderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            Collection<PSObject> results = await _ps.RunAsync(StatusScript, cancellationToken: ct).ConfigureAwait(false);
            if (results.Count == 0) return DefenderStatus.Unavailable;
            return HideWithheldExclusions(ParseStatus(results[0]), Helpers.AdminHelper.IsElevated());
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Defender status read failed: {Error}", ex.Message);
            return DefenderStatus.Unavailable;
        }
    }

    /// <summary>
    /// Empties the exclusion lists and marks them unreadable when the session is not elevated.
    /// </summary>
    /// <remarks>
    /// Windows shows the exclusion lists only to an administrator. For a standard user it puts a sentence where
    /// each list should be — "N/A: Must be an administrator to view exclusions" on an English system — and that
    /// sentence would otherwise be listed as an excluded folder. The decision rests on the session's elevation,
    /// not on matching the sentence, so it does not depend on the sentence's wording or language; the
    /// PowerShell child that ran the read has the same token as the app.
    /// </remarks>
    internal static DefenderStatus HideWithheldExclusions(DefenderStatus status, bool elevated) =>
        elevated
            ? status
            : status with { ExclusionPaths = [], ExclusionExtensions = [], ExclusionProcesses = [], ExclusionsReadable = false };

    /// <summary>Parse a Get-MpPreference/Get-MpComputerStatus projection into a status.</summary>
    /// <remarks>
    /// Real-time scanning is read both ways: as set, from the preference, and as it runs, from the computer status,
    /// which is what the card shows (#2867). A projection without the second leaves it unknown, not off.
    /// </remarks>
    public static DefenderStatus ParseStatus(PSObject obj)
    {
        bool disableRtp = ToBool(Prop(obj, "DisableRealtimeMonitoring"));
        var inEffect = Prop(obj, "RealTimeProtectionEnabled");
        return new DefenderStatus(
            Available: true,
            IsTamperProtected: ToBool(Prop(obj, "IsTamperProtected")),
            RealtimeProtection: !disableRtp, // normalize the inverted "Disable" boolean
            PuaProtection: ToInt(Prop(obj, "PUAProtection")),
            MapsReporting: ToInt(Prop(obj, "MAPSReporting")),
            ControlledFolderAccess: ToInt(Prop(obj, "EnableControlledFolderAccess")),
            ExclusionPaths: ToStringList(Prop(obj, "ExclusionPath")),
            ExclusionExtensions: ToStringList(Prop(obj, "ExclusionExtension")),
            ExclusionProcesses: ToStringList(Prop(obj, "ExclusionProcess")))
        {
            RealtimeInEffect = inEffect is null ? null : ToBool(inEffect),
            RunningMode = Prop(obj, "AMRunningMode")?.ToString()?.Trim() ?? "",
        };
    }

    /// <summary>
    /// Set PUA protection (0/1/2) and verify the change took effect. Returns the
    /// re-read status; compare its PuaProtection to confirm success.
    /// </summary>
    public Task<DefenderStatus> SetPuaProtectionAsync(int value, CancellationToken ct = default)
        => ApplyAndVerifyAsync("param([int]$Value) Set-MpPreference -PUAProtection $Value", new() { ["Value"] = ClampTri(value) }, ct);

    /// <summary>Set Controlled Folder Access (0/1/2) and verify.</summary>
    public Task<DefenderStatus> SetControlledFolderAccessAsync(int value, CancellationToken ct = default)
        => ApplyAndVerifyAsync("param([int]$Value) Set-MpPreference -EnableControlledFolderAccess $Value", new() { ["Value"] = ClampTri(value) }, ct);

    /// <summary>Add a folder exclusion (additive — never replaces the array). Verifies.</summary>
    public Task<DefenderStatus> AddExclusionPathAsync(string path, CancellationToken ct = default)
    {
        // Trust-boundary validation at the service (the UI validates too, but the service
        // is public), so an over-broad exclusion can never weaken Defender, even via a non-UI caller.
        if (ExclusionRefusal(path) is { } why)
            throw new ArgumentException($"\"{path}\" cannot be excluded from scanning: it {why}.", nameof(path));
        return ApplyAndVerifyAsync("param([string]$Path) Add-MpPreference -ExclusionPath $Path", new() { ["Path"] = path }, ct);
    }

    /// <summary>True when <paramref name="path"/> may be excluded from scanning: see <see cref="ExclusionRefusal(string)"/>.</summary>
    internal static bool IsValidExclusionPath(string path) => ExclusionRefusal(path) is null;

    /// <summary>
    /// Why <paramref name="path"/> may not be excluded from scanning, as the end of a sentence, or null when it may.
    /// Existence is intentionally not required: the path may not exist yet at the service boundary; the tab checks it.
    /// </summary>
    /// <remarks>
    /// The check used to be only "rooted, no wildcards, at most 260 characters", and <c>C:\</c> passes all of it. The
    /// folder picker offers a drive, so one confirmation turned off scanning for the whole system drive, and the Windows
    /// folder, Program Files, Users, the profile or Downloads were as easy (#2773). Refused now: a path that is not
    /// fully qualified (<c>C:</c> and <c>\Windows</c> are rooted but mean "wherever the current directory is"), a whole
    /// drive or share, the Windows folder and anything in it, and any folder that is or holds Program Files, ProgramData,
    /// the folder every account's files are in, the user's own folder, Documents, Desktop, Downloads, Pictures, Music,
    /// Videos or either temp folder. A folder inside those last ones, such as a game's own folder in Program Files, is
    /// still allowed: that is what exclusions are for.
    /// </remarks>
    internal static string? ExclusionRefusal(string path) => ExclusionRefusal(path, ProtectedFromExclusion());

    /// <summary><see cref="ExclusionRefusal(string)"/> over a given list of folders, so a test can name its own.</summary>
    internal static string? ExclusionRefusal(
        string path, IReadOnlyList<(string Name, string? Path, bool ContentsToo)> protectedFolders)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260 || path.Contains('*') || path.Contains('?')
            || !System.IO.Path.IsPathFullyQualified(path))
            return "is not a full folder path without wildcards";

        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return "is not a full folder path without wildcards";
        }

        if (string.Equals(System.IO.Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            return "is a whole drive or network share";

        foreach (var (name, folder, contentsToo) in protectedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (Helpers.SystemPaths.IsInsideSubtree(folder, full)) return $"is or holds {name}";
            if (contentsToo && Helpers.SystemPaths.IsInsideSubtree(full, folder)) return $"is inside {name}";
        }
        return null;
    }

    /// <summary>The folders <see cref="ExclusionRefusal(string)"/> protects, read from this machine on each call.</summary>
    private static IReadOnlyList<(string Name, string? Path, bool ContentsToo)> ProtectedFromExclusion()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<(string, string?, bool)> folders =
        [
            ("the Windows folder", windows, true),
            ("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), false),
            ("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), false),
            ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), false),
            ("the folder every account's files are in",
                string.IsNullOrEmpty(profile) ? null : System.IO.Path.GetDirectoryName(profile), false),
            ("your account's folder", profile, false),
            ("your temp folder", Helpers.SystemPaths.UserTempRoot, false),
        ];
        folders.AddRange(Helpers.KnownFolders.UserFolders().Select(f => (f.Name, (string?)f.Path, false)));
        return folders;
    }

    /// <summary>Remove a folder exclusion. Verifies.</summary>
    public Task<DefenderStatus> RemoveExclusionPathAsync(string path, CancellationToken ct = default)
        => ApplyAndVerifyAsync("param([string]$Path) Remove-MpPreference -ExclusionPath $Path", new() { ["Path"] = path }, ct);

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

    internal static IReadOnlyList<string> ToStringList(object? value)
    {
        value = UnwrapPsObject(value);
        if (value is null) return [];
        if (value is string text)
            return text.Length > 0 ? [text] : [];
        if (value is IEnumerable values)
        {
            return values.Cast<object?>()
                .Select(UnwrapPsObject)
                .Where(item => item is not null)
                .Select(item => item!.ToString() ?? "")
                .Where(item => item.Length > 0)
                .ToList();
        }

        string single = value.ToString() ?? "";
        return single.Length > 0 ? [single] : [];
    }

    private static object? UnwrapPsObject(object? value)
        => value is PSObject psObject ? psObject.BaseObject : value;

    internal static int ClampTri(int value) => value is >= 0 and <= 2 ? value : 0;
}
