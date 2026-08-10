// SysManager · DuplicateFileGroup — model for duplicate file results
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// A group of files that share the same content (identical SHA-256 hash).
/// Read-only by design — the UI only offers "Show in Explorer" and "Copy path".
/// </summary>
public sealed partial class DuplicateFileGroup : ObservableObject
{
    [ObservableProperty] private string _hash = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WastedBytes))]
    private long _fileSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WastedBytes))]
    private int _count;

    public ObservableCollection<DuplicateFileEntry> Files { get; } = new();

    /// <summary>Wasted space = (count - 1) * fileSize.</summary>
    public long WastedBytes => Math.Max(Count - 1, 0) * FileSize;

    /// <summary>
    /// Marks exactly one file in the group as the suggested keeper, so five identical photos are not
    /// presented as five equal rows with no hint which one is the original.
    /// </summary>
    /// <remarks>
    /// The rule is "oldest last-modified time wins", because a copy is normally made after the file it
    /// was copied from. It is a HEURISTIC, not a fact, and it is wrong in real cases: a copy tool that
    /// preserves timestamps, or a cloud-sync client that rewrites them, both break it. That is exactly
    /// why the suggestion is shown with its reason attached and can be moved by the user
    /// (<see cref="SetKeeper"/>) instead of being applied silently.
    ///
    /// Ties are broken by the shortest path, then alphabetically — deterministic either way, so the
    /// same scan never suggests a different keeper twice.
    /// </remarks>
    public void ApplySuggestedKeeper()
    {
        var keeper = Files
            .OrderBy(f => f.LastModified)
            .ThenBy(f => f.Path.Length)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        foreach (var f in Files)
            f.IsSelected = ReferenceEquals(f, keeper);
    }

    /// <summary>
    /// Moves the keeper to <paramref name="entry"/>. Exactly one file per group is kept, so this
    /// clears the others rather than toggling — "which one do I keep" is a single choice, not a
    /// set of independent checkboxes.
    /// </summary>
    public void SetKeeper(DuplicateFileEntry entry)
    {
        if (!Files.Contains(entry)) return;

        foreach (var f in Files)
            f.IsSelected = ReferenceEquals(f, entry);
    }
}

/// <summary>A single file within a duplicate group.</summary>
public sealed partial class DuplicateFileEntry : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private DateTime _lastModified;

    /// <summary>
    /// True for the one file in the group suggested as the keeper. Drives the "Keep" badge and the
    /// de-emphasis of the other rows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepLabel))]
    private bool _isSelected;

    /// <summary>Badge text — empty for non-keepers so the badge collapses.</summary>
    public string KeepLabel => IsSelected ? "Keep" : "";
}
