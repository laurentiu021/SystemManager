// SysManager · ISettingsWatchdogService — testable seam for the Settings Watchdog
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="SettingsWatchdogService"/> so the ViewModel can be unit-tested with a
/// substituted implementation (no real registry / baseline file). Mirrors the established
/// interface-seam pattern (<see cref="IAppBlockerService"/>, <see cref="IPowerShellRunner"/>).
/// </summary>
public interface ISettingsWatchdogService
{
    IReadOnlyList<WatchedSetting> Catalog { get; }
    bool HasBaseline { get; }
    IReadOnlyDictionary<string, int?> ReadCurrent();
    IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt);
    BaselineSnapshot? LoadBaseline();
    IReadOnlyList<SettingDrift> DetectDrift();
    bool Restore(SettingDrift drift);
}
