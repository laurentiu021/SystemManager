// SysManager · DefenderStatus
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A read-only snapshot of the Microsoft Defender settings SysManager surfaces.
/// Note that several underlying Defender properties are inverted "Disable" booleans;
/// they are normalized here to positive meaning (e.g. <see cref="RealtimeProtection"/>
/// is true when protection is ON).
/// </summary>
public sealed record DefenderStatus(
    bool Available,
    bool IsTamperProtected,
    bool RealtimeProtection,
    int PuaProtection,            // 0=Disabled, 1=Enabled, 2=AuditMode
    int MapsReporting,            // 0=Disabled, 1=Basic, 2=Advanced
    int ControlledFolderAccess,  // 0=Disabled, 1=Enabled, 2=AuditMode
    IReadOnlyList<string> ExclusionPaths,
    IReadOnlyList<string> ExclusionExtensions,
    IReadOnlyList<string> ExclusionProcesses)
{
    public static string TriStateLabel(int v) => v switch
    {
        0 => "Disabled",
        1 => "Enabled",
        2 => "Audit mode",
        _ => "Unknown",
    };

    public static string MapsLabel(int v) => v switch
    {
        0 => "Disabled",
        1 => "Basic",
        2 => "Advanced",
        _ => "Unknown",
    };

    public string PuaDisplay => TriStateLabel(PuaProtection);
    public string CfaDisplay => TriStateLabel(ControlledFolderAccess);
    public string MapsDisplay => MapsLabel(MapsReporting);
    /// <summary>
    /// Whether real-time scanning runs, as Windows reports it, or null when that was not read.
    /// </summary>
    /// <remarks>
    /// <see cref="RealtimeProtection"/> is the setting, which says only what is configured. Windows stops real-time
    /// scanning when another antivirus takes over and leaves the setting on, and the card read the setting, so it said
    /// "On" while Defender was not scanning (#2867).
    /// </remarks>
    public bool? RealtimeInEffect { get; init; }

    /// <summary>
    /// How Windows runs Defender, in Get-MpComputerStatus's words ("Normal", "Passive Mode", …), or empty.
    /// </summary>
    public string RunningMode { get; init; } = "";

    /// <summary>
    /// Defender stands aside for another antivirus: Windows runs it in passive mode, or in EDR block mode, either
    /// of which means another antivirus is the one in charge.
    /// </summary>
    public bool StandsAside =>
        RunningMode.Contains("Passive", StringComparison.OrdinalIgnoreCase)
        || RunningMode.Contains("EDR Block", StringComparison.OrdinalIgnoreCase);

    /// <summary>Real-time scanning as it runs, or as it is set when that was not read.</summary>
    public string RealtimeDisplay => (RealtimeInEffect ?? RealtimeProtection) ? "On" : "Off";

    /// <summary>Why real-time scanning is off while its setting is on, or empty when the two agree.</summary>
    public string RealtimeNote => RealtimeProtection && RealtimeInEffect == false
        ? StandsAside
            ? "Real-time protection is off: another antivirus is in charge, and Windows runs Defender in passive mode."
            : "Real-time protection is off, although its setting here is on."
        : "";

    /// <summary>
    /// False when Windows withheld the exclusion lists, which it does for a standard user. The three lists
    /// are then empty because they are unknown, not because there are no exclusions.
    /// </summary>
    public bool ExclusionsReadable { get; init; } = true;

    public static DefenderStatus Unavailable { get; } =
        new(false, false, false, 0, 0, 0, [], [], []);
}
