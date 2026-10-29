// SysManager · AppDataFolders — the one source for %LocalAppData%\SysManager and %AppData%\SysManager
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Helpers;

/// <summary>
/// The two data-folder roots SysManager writes under, and the sub-folders built from them, declared
/// once instead of composed independently at roughly thirty call sites — among them pairs that must
/// agree: the crash marker's writer in <c>App</c> and its reader in <c>CrashMarkerService</c>, the
/// update folder in <c>UpdateService</c> and <c>UpdateApplier</c>, and the two entries
/// <c>ILeftoverEnvironment.OwnFolders</c> uses to keep the Uninstaller from offering SysManager's own
/// data back to the user as a leftover (#2860).
/// </summary>
/// <remarks>
/// <see cref="Local"/> and <see cref="Roaming"/> are settable, not <c>static readonly</c>, so a test
/// run can redirect both away from the real profile in one place rather than relying on every
/// constructor's own <c>configDir</c> seam being passed correctly — <c>TestAssemblyInit</c> in both
/// test projects does exactly that, once, before any test runs, the same way it already redirects
/// <c>ActivityLogService.Instance</c> (#1772). A service's own <c>configDir</c> parameter, where one
/// exists, still takes precedence: this is the floor underneath that seam, not a replacement for it.
/// </remarks>
internal static class AppDataFolders
{
    internal static string Local { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");

    internal static string Roaming { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysManager");

    /// <summary>Daily-rolled log files. <see cref="Services.LogService"/>'s own default.</summary>
    internal static string Logs => Path.Combine(Local, "logs");

    /// <summary>Downloaded release builds awaiting install, and the one retained previous build.</summary>
    internal static string Updates => Path.Combine(Local, "updates");

    /// <summary>The Ookla Speedtest CLI SysManager downloads on first use.</summary>
    internal static string Tools => Path.Combine(Local, "tools");

    /// <summary>Cached per-app icons, keyed by app ID.</summary>
    internal static string IconCache => Path.Combine(Local, "IconCache");

    /// <summary>
    /// Root for every feature's own restore point, e.g. <c>Path.Combine(AppDataFolders.Backups,
    /// "ContextMenu")</c>. Not a single folder on its own — each caller names its own sub-folder under it.
    /// </summary>
    internal static string Backups => Path.Combine(Local, "Backups");
}
