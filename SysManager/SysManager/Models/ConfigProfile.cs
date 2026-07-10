// SysManager · ConfigProfile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A portable export of SysManager's own configuration — a versioned bundle of the
/// app's settings files (theme, speed-test history, …) that can be carried to another
/// PC and selectively re-applied. Contains only SysManager's own JSON config, never
/// system state, so importing it is fully reversible (it overwrites app config files).
/// </summary>
public sealed record ConfigProfile(
    int SchemaVersion,
    string AppVersion,
    DateTime ExportedAt)
{
    /// <summary>
    /// The exported config files. A defaulted init property (not a positional param)
    /// because System.Text.Json does NOT enforce non-null on positional record params:
    /// a syntactically-valid JSON object lacking a "Sections" property (an empty <c>{}</c>,
    /// a truncated export, or foreign JSON picked in the Import dialog) would otherwise
    /// deserialize <c>Sections</c> to null and crash Import with an NRE. Defaulting to
    /// <c>[]</c> mirrors the codebase idiom already used by CleanupResult.Errors,
    /// HealthScoreResult.Recommendations and TuneUpResult.DiskResults.
    /// </summary>
    public IReadOnlyList<ConfigSection> Sections { get; init; } = [];
}

/// <summary>One exported config file: its logical key, file name, and raw JSON content.</summary>
public sealed record ConfigSection(
    string Key,
    string DisplayName,
    string FileName,
    string Json);
