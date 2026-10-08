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
    /// <exception cref="System.IO.IOException">The baseline file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The baseline folder or file is not writable.</exception>
    IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt);

    /// <summary>
    /// The saved baseline, or <c>null</c> when none exists OR the stored file cannot be read or
    /// parsed. Callers deciding whether a usable baseline exists must test this for null — a file
    /// being present is not the same as a baseline being readable.
    /// </summary>
    BaselineSnapshot? LoadBaseline();

    /// <summary>
    /// True when a baseline file is on disk, whether or not it could be read. With <see cref="LoadBaseline"/>
    /// returning null it tells a baseline that could not be read or used from none at all, which is the
    /// difference between asking before replacing it and not (#2521).
    /// </summary>
    bool BaselineFileExists { get; }

    /// <summary>
    /// The settings that have changed since the baseline. Returns an EMPTY list — never null — when
    /// there is no baseline or nothing drifted.
    /// <para>Reads the baseline and the live values itself. A caller that is going to display those
    /// live values as well must use the overload below and pass its own read in, or the two will come
    /// from different moments.</para>
    /// </summary>
    IReadOnlyList<SettingDrift> DetectDrift();

    /// <summary>
    /// The settings that have changed, computed against an ALREADY-READ snapshot of the live values.
    /// <para>Exists so a caller that shows both the drift list and the live values derives them from
    /// ONE read. With two reads a setting that changes in between appears with its new value but no
    /// drift verdict — which is precisely the state this tab exists to make visible.</para>
    /// </summary>
    IReadOnlyList<SettingDrift> DetectDrift(BaselineSnapshot baseline, IReadOnlyDictionary<string, int?> current);

    /// <summary>
    /// Writes one drifted setting back to its baseline value. Returns false when it cannot be
    /// restored: read-only, no baseline value recorded, or the write was denied.
    /// </summary>
    bool Restore(SettingDrift drift);

    /// <summary>
    /// Records when each drift in <paramref name="drifts"/> was first seen, and when one seen before is seen gone, and
    /// returns every sighting kept (#1507).
    /// </summary>
    /// <remarks>
    /// The drift list is worked out again on every look, so on its own it can never say when a setting changed. A
    /// sighting is when SysManager first noticed it, which is the earliest anyone can know without a watcher running.
    /// Saving a new baseline forgets them, since what they were measured against is gone.
    /// </remarks>
    DriftSightings RecordDrift(IReadOnlyList<SettingDrift> drifts, DateTime now);
}
