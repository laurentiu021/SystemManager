// SysManager · RecentChange — one thing that changed on the PC, for the Recent Changes tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>Which filter on Recent Changes a change is listed under (#1507).</summary>
public enum ChangeCategory
{
    /// <summary>Something done in SysManager, from its activity log.</summary>
    SysManager,

    /// <summary>An update Windows installed, or could not install.</summary>
    Windows,

    /// <summary>A program installed or removed, by whatever ran its installer.</summary>
    Programs,

    /// <summary>A watched setting found changed from the one saved in Settings Watchdog.</summary>
    Settings,
}

/// <summary>What kind of change it is, which decides its wording, its icon and how it groups.</summary>
public enum ChangeKind
{
    /// <summary>An entry of SysManager's activity log.</summary>
    SysManagerAction,

    /// <summary>A Windows update installed: cumulative, security, .NET or anything else Windows Update delivers.</summary>
    WindowsUpdate,

    /// <summary>A Microsoft Store app updated, which Windows records with the Store id in front of its name.</summary>
    StoreAppUpdate,

    /// <summary>Microsoft Defender's virus definitions updated.</summary>
    DefenderUpdate,

    /// <summary>A driver Windows Update installed.</summary>
    DriverUpdate,

    /// <summary>An update Windows or the Microsoft Store tried to install and could not.</summary>
    UpdateFailed,

    /// <summary>A program Windows Installer installed, at the time Windows recorded.</summary>
    ProgramInstalled,

    /// <summary>A program Windows Installer removed, at the time Windows recorded.</summary>
    ProgramRemoved,

    /// <summary>A program that appeared in the list of installed programs between two looks at Recent Changes.</summary>
    ProgramAppeared,

    /// <summary>A program that left the list of installed programs between two looks at Recent Changes.</summary>
    ProgramDisappeared,

    /// <summary>
    /// A program whose name in the list changed between two looks, the version in it most often: the same program,
    /// updated, rather than one removed and another installed.
    /// </summary>
    ProgramUpdated,

    /// <summary>A program New App Alerts noticed being installed while it was watching, at that time.</summary>
    ProgramDetected,

    /// <summary>A watched setting that no longer matches the settings saved in Settings Watchdog.</summary>
    SettingChanged,
}

/// <summary>Where the button on a change leads, if it has one.</summary>
public enum ChangeLink
{
    None,

    /// <summary>The Uninstaller, which lists the program with its size and can remove it.</summary>
    Uninstaller,

    /// <summary>New App Alerts, which noticed it.</summary>
    AppAlerts,

    /// <summary>Settings Watchdog, which can put the setting back.</summary>
    SettingsWatchdog,
}

/// <summary>One thing that changed on the PC, from any of the sources Recent Changes reads.</summary>
/// <param name="When">
/// When it happened. For a program noticed between two looks, when it was noticed: the end of the window.
/// </param>
/// <param name="Since">For a program noticed between two looks, the earlier look. Null when the time is known.</param>
/// <param name="Kind">What kind of change it is.</param>
/// <param name="Subject">What changed: the program, the update, the setting, or what SysManager did.</param>
/// <param name="Who">Who made the change, in words: "You, in SysManager", "Windows Update", "An installer".</param>
/// <param name="Detail">The line under it, or empty.</param>
public sealed record ChangeEvent(DateTime When, DateTime? Since, ChangeKind Kind, string Subject, string Who, string Detail)
{
    /// <summary>The filter it is listed under, which follows from its kind.</summary>
    public ChangeCategory Category => CategoryOf(Kind);

    /// <summary>Where its button leads, which follows from its kind.</summary>
    public ChangeLink Link => Kind switch
    {
        ChangeKind.ProgramInstalled or ChangeKind.ProgramAppeared or ChangeKind.ProgramUpdated => ChangeLink.Uninstaller,
        ChangeKind.ProgramDetected => ChangeLink.AppAlerts,
        ChangeKind.SettingChanged => ChangeLink.SettingsWatchdog,
        _ => ChangeLink.None,
    };

    /// <summary>The filter a kind of change is listed under.</summary>
    public static ChangeCategory CategoryOf(ChangeKind kind) => kind switch
    {
        ChangeKind.SysManagerAction => ChangeCategory.SysManager,
        ChangeKind.WindowsUpdate or ChangeKind.StoreAppUpdate or ChangeKind.DefenderUpdate
            or ChangeKind.DriverUpdate or ChangeKind.UpdateFailed => ChangeCategory.Windows,
        ChangeKind.SettingChanged => ChangeCategory.Settings,
        _ => ChangeCategory.Programs,
    };
}

/// <summary>A problem Windows recorded, which Recent Changes only counts and leaves to System Logs.</summary>
/// <param name="When">When Windows recorded it.</param>
/// <param name="StoppedResponding">True for a program that stopped responding and was closed; false for one that crashed.</param>
public sealed record ProblemEvent(DateTime When, bool StoppedResponding);
