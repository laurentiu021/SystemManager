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
    /// <summary>The settings this service watches, in display order.</summary>
    IReadOnlyList<WatchedSetting> Catalog { get; }

    /// <summary>Reads each watched setting's live value. A null value means "not readable".</summary>
    IReadOnlyDictionary<string, int?> ReadCurrent();

    /// <summary>
    /// Captures the current values as the new baseline and returns the snapshot just taken, so the
    /// caller does not have to read it back.
    /// </summary>
    IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt);

    /// <summary>
    /// The saved baseline, or <c>null</c> when none exists OR the stored file cannot be read or
    /// parsed. Callers deciding whether a usable baseline exists must test this for null — a file
    /// being present is not the same as a baseline being readable.
    /// </summary>
    BaselineSnapshot? LoadBaseline();

    /// <summary>
    /// The settings that have changed since the baseline. Returns an EMPTY list — never null — when
    /// there is no baseline or nothing drifted.
    /// </summary>
    IReadOnlyList<SettingDrift> DetectDrift();

    /// <summary>
    /// Writes one drifted setting back to its baseline value. Returns false when it cannot be
    /// restored: read-only, no baseline value recorded, or the write was denied.
    /// </summary>
    bool Restore(SettingDrift drift);
}
