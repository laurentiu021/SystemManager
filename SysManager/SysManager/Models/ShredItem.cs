// SysManager · ShredItem — model for a file/folder queued for secure deletion
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Represents a file or folder queued for secure shredding.
/// </summary>
public sealed partial class ShredItem : ObservableObject
{
    [ObservableProperty] private string _status = "Pending";

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public bool IsFolder { get; init; }

    public string SizeDisplay => FormatHelper.FormatSize(SizeBytes);
}

/// <summary>
/// What a folder shred did, beyond the fact that it finished.
/// </summary>
/// <remarks>
/// Exists so a deliberate skip can be reported without being reported as a failure. The shredder never
/// follows a junction or symlink inside a selected folder, because overwriting through one would destroy
/// data at the link's target outside the selection. That refusal is correct, so it must not surface as an
/// error — but it does mean the folder was not emptied, and the user asked for it to be destroyed. Saying
/// nothing leaves them believing data is gone when it is not, which is the one thing a shredder cannot
/// afford to get wrong.
/// </remarks>
public sealed record ShredFolderReport
{
    /// <summary>Files that were securely overwritten and deleted.</summary>
    public int FilesShredded { get; init; }

    /// <summary>
    /// Full paths of junctions, symlinks and link files found inside the folder and deliberately left
    /// alone. Empty on the ordinary case, which is why <see cref="Notice"/> is null then.
    /// </summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];

    /// <summary>
    /// A plain-English sentence for the user, or null when there is nothing they need to know. Names the
    /// consequence (the folder is still there) rather than the mechanism (reparse points), and says why
    /// the skip was the safe choice.
    /// </summary>
    public string? Notice => SkippedLinks.Count == 0
        ? null
        : $"{SkippedLinks.Count} shortcut{(SkippedLinks.Count == 1 ? "" : "s")} inside "
          + $"{(SkippedLinks.Count == 1 ? "was" : "were")} left alone because "
          + $"{(SkippedLinks.Count == 1 ? "it points" : "they point")} to files outside the folder, "
          + "so the folder itself is still on the computer.";
}
