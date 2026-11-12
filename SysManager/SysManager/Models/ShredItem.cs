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
    /// <summary>What a screen reader calls the grid row, rather than the class's type name (#2785).</summary>
    public override string ToString() => Name;

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
    /// True when the walk stopped early because the user cancelled, so the folder still holds the files it
    /// had not reached.
    /// </summary>
    /// <remarks>
    /// Returned instead of thrown. Cancelling a folder shred used to raise
    /// <see cref="OperationCanceledException"/>, which discarded <see cref="FilesShredded"/> and let the
    /// queue mark the whole folder "Cancelled" — while every file already visited had been destroyed and
    /// removed (#2374). A count the user can act on is the point of stopping.
    /// </remarks>
    public bool WasCancelled { get; init; }

    /// <summary>
    /// Files that could not be securely overwritten and were therefore left on disk, not deleted. Only
    /// meaningful together with <see cref="WasCancelled"/>: an uncancelled run reports these by throwing,
    /// with the file names in the message.
    /// </summary>
    public int FilesLeftInPlace { get; init; }

    /// <summary>
    /// Full paths of junctions, symlinks and link files found inside the folder and deliberately left
    /// alone. Empty on the ordinary case, which is why <see cref="Notice"/> is null then.
    /// </summary>
    public IReadOnlyList<string> SkippedLinks { get; init; } = [];

    /// <summary>
    /// Files a write-time fault (a full disk, a removed drive) stopped partway through, after the
    /// overwrite had already begun. These ARE gone — counted in <see cref="FilesShredded"/> too — just
    /// with fewer passes than the method asked for, so it is worth telling the user rather than letting
    /// them believe every file got the full depth they picked.
    /// </summary>
    public int FilesPartiallyOverwritten { get; init; }

    /// <summary>
    /// Files whose contents were overwritten but whose names Windows would not remove. They are destroyed, and counted
    /// in <see cref="FilesShredded"/> too, but their names are still in the folder.
    /// </summary>
    public int FilesNotRemoved { get; init; }

    /// <summary>Folders inside it the shred could not open, so what they hold was not shredded.</summary>
    public int FoldersNotRead { get; init; }

    /// <summary>True when the folder picked is still on disk after the shred, or that could not be told.</summary>
    public bool FolderLeftInPlace { get; init; }

    /// <summary>
    /// True when the queue may call the folder shredded: it is gone, or all that is left of it is what
    /// <see cref="SkippedLinks"/> names, left alone on purpose.
    /// </summary>
    /// <remarks>
    /// A folder that stayed was called Done and taken off the queue: an emptied read-only folder Windows would not
    /// remove, a folder holding a file whose name stayed, a folder whose subfolders could not be opened.
    /// </remarks>
    public bool IsDone => !WasCancelled
        && (!FolderLeftInPlace || (SkippedLinks.Count > 0 && FilesNotRemoved == 0 && FoldersNotRead == 0));

    /// <summary>
    /// A plain-English sentence for the user, or null when there is nothing they need to know. Names the
    /// consequence (the folder is still there, these files are gone) rather than the mechanism (reparse
    /// points, passes), and says why a skip was the safe choice.
    /// </summary>
    public string? Notice
    {
        get
        {
            List<string> parts = [];

            // Cancellation first: it is the thing the user just did, and it is the sentence that corrects
            // what they would otherwise assume — that stopping meant nothing was destroyed.
            if (WasCancelled)
            {
                parts.Add(FilesShredded == 0
                    ? "you stopped it before anything inside was destroyed, so the folder is untouched."
                    : $"you stopped it after {FilesShredded} file{(FilesShredded == 1 ? "" : "s")} inside had "
                      + "already been destroyed and removed — "
                      + $"{(FilesShredded == 1 ? "that one cannot" : "those cannot")} be recovered, and the "
                      + "rest of the folder is untouched.");

                if (FilesLeftInPlace > 0)
                    parts.Add($"{FilesLeftInPlace} file{(FilesLeftInPlace == 1 ? "" : "s")} could not be "
                              + "securely overwritten and " + (FilesLeftInPlace == 1 ? "was" : "were")
                              + " left in place rather than plainly deleted.");
            }

            if (SkippedLinks.Count > 0)
                parts.Add($"{SkippedLinks.Count} shortcut{(SkippedLinks.Count == 1 ? "" : "s")} inside "
                          + $"{(SkippedLinks.Count == 1 ? "was" : "were")} left alone because "
                          + $"{(SkippedLinks.Count == 1 ? "it points" : "they point")} to files outside the folder, "
                          + "so the folder itself is still on the computer.");

            if (FilesPartiallyOverwritten > 0)
                parts.Add($"{FilesPartiallyOverwritten} file{(FilesPartiallyOverwritten == 1 ? "" : "s")} hit a "
                          + "write error partway through and "
                          + (FilesPartiallyOverwritten == 1 ? "was" : "were")
                          + " destroyed with fewer passes than the method you picked, rather than left "
                          + "half-overwritten.");

            if (FilesNotRemoved > 0)
                parts.Add($"{FilesNotRemoved} file{(FilesNotRemoved == 1 ? " was" : "s were")} overwritten, so what "
                          + $"{(FilesNotRemoved == 1 ? "it" : "they")} held is gone, but Windows would not let "
                          + $"{(FilesNotRemoved == 1 ? "it" : "them")} be removed: "
                          + $"{(FilesNotRemoved == 1 ? "its name is" : "their names are")} still in the folder.");

            if (FoldersNotRead > 0)
                parts.Add($"{FoldersNotRead} folder{(FoldersNotRead == 1 ? "" : "s")} in it could not be opened, so "
                          + $"what is in {(FoldersNotRead == 1 ? "it" : "them")} was not shredded and is still on "
                          + "the computer.");

            // The reason, when nothing above already gives it.
            if (!WasCancelled && FolderLeftInPlace && SkippedLinks.Count == 0 && FilesNotRemoved == 0
                && FoldersNotRead == 0)
                parts.Add(FilesShredded == 0
                    ? "nothing in it could be shredded, so the folder and everything in it are still on the computer."
                    : "the folder itself could not be removed, so it is still on the computer.");

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }
}
