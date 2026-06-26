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
    public string RealtimeDisplay => RealtimeProtection ? "On" : "Off";

    public static DefenderStatus Unavailable { get; } =
        new(false, false, false, 0, 0, 0, [], [], []);
}
