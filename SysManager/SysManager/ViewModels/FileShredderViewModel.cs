// SysManager · FileShredderViewModel — secure file deletion UI logic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the File Shredder tab — allows users to securely delete
/// files and folders with multi-pass overwrite patterns.
/// </summary>
public sealed partial class FileShredderViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsShredding ? CancelCommand : null;

    private readonly FileShredderService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<ShredItem> Items { get; } = new();

    [ObservableProperty] private ShredMethod _selectedMethod = ShredMethod.Standard;
    [ObservableProperty] private bool _isShredding;

    public FileShredderViewModel(FileShredderService service)
    {
        _service = service;
        Items.CollectionChanged += (_, _) => ShredAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select files to shred",
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var skipped = new List<string>();

        foreach (var filePath in dialog.FileNames)
        {
            if (Items.Any(i => i.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var info = new FileInfo(filePath);
                Items.Add(new ShredItem
                {
                    Path = filePath,
                    Name = info.Name,
                    SizeBytes = info.Length,
                    IsFolder = false
                });
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "Could not read file info: {Path}", filePath);
                skipped.Add(Path.GetFileName(filePath));
            }
            catch (UnauthorizedAccessException ex)
            {
                // A file the user dragged in but cannot read — skip it rather than aborting the
                // whole drop. UnauthorizedAccessException is a sibling of IOException, not a
                // subclass, so the catch above never covered it.
                Log.Warning(ex, "Access denied reading file info: {Path}", filePath);
                skipped.Add(Path.GetFileName(filePath));
            }
        }

        StatusMessage = DescribeSkipped(skipped);
    }

    /// <summary>
    /// Builds the message shown when items the user picked could not be queued. Returning an empty
    /// string clears any previous warning, so a successful second attempt does not leave a stale one
    /// on screen.
    /// <para>Named separately so the wording is assertable without a file dialog.</para>
    /// </summary>
    internal static string DescribeSkipped(IReadOnlyList<string> skipped) => skipped.Count switch
    {
        0 => string.Empty,
        1 => $"Could not add \"{skipped[0]}\" — it could not be read, so it is NOT in the list below.",
        _ => $"Could not add {skipped.Count} of the items you picked ({string.Join(", ", skipped)}) — "
             + "they could not be read, so they are NOT in the list below."
    };

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to shred"
        };

        if (dialog.ShowDialog() != true) return;

        var folderPath = dialog.FolderName;
        if (Items.Any(i => i.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            var size = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);

            Items.Add(new ShredItem
            {
                Path = folderPath,
                Name = dirInfo.Name,
                SizeBytes = size,
                IsFolder = true
            });
            StatusMessage = string.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied while reading folder: {Path}", folderPath);
            StatusMessage = DescribeSkipped([Path.GetFileName(folderPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))]);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Could not read folder info: {Path}", folderPath);
            StatusMessage = DescribeSkipped([Path.GetFileName(folderPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditQueue))]
    private void RemoveItem(ShredItem? item)
    {
        if (item is not null)
            Items.Remove(item);
    }

    [RelayCommand(CanExecute = nameof(CanShredAll))]
    private async Task ShredAllAsync()
    {
        if (Items.Count == 0) return;

        var confirmed = DialogService.Instance.Confirm(
            $"You are about to permanently shred {Items.Count} item(s) using the {SelectedMethod} method.\n\n" +
            "This action is IRREVERSIBLE. The data cannot be recovered.\n\nContinue?",
            "Confirm Secure Shred");

        if (!confirmed) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsShredding = true;
        IsBusy = true;
        StatusMessage = "Shredding...";

        var completed = 0;
        var failed = 0;

        // What was actually destroyed, tracked by IDENTITY. The finally below used to re-read item.Status
        // and remove whatever said "Done", but Status is a display string the async progress callback also
        // writes — so "was this destroyed?" depended on callback delivery order, and a report that arrived
        // late left a file that no longer exists sitting in the queue claiming it was still being shredded.
        List<ShredItem> destroyed = [];

        // Everything the user needs to be told, in the order it happened. Previously the service built a
        // careful explanation of what it could not shred and the catch below dropped it on the floor:
        // item.Status showed the word "Failed" and ex.Message went only to the log file. For an operation
        // whose entire purpose is destroying data, "it did not work" without "and these files are still
        // on your disk" is the wrong half of the sentence.
        List<string> notices = [];

        // Cancellation ends the run through the SAME exit as a completed one, rather than by throwing past
        // the reporting below. Everything already destroyed still has to be counted, announced and recorded
        // in the activity log: a cancelled shred is a shred that stopped, not one that did nothing.
        var cancelled = false;

        try
        {
            // Shred a SNAPSHOT of the queue, not the live collection: indexing Items across the
            // per-file awaits would race a concurrent Add/Remove. (Those commands are also
            // disabled while shredding via CanEditQueue; the snapshot is the belt-and-suspenders.)
            foreach (var item in Items.ToArray())
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                var totalPasses = (int)SelectedMethod;

                // Stops reporting the moment the shred ends, so none of the final statuses below can be
                // overwritten by a report that drains afterwards and leaves an erased file looking
                // unfinished forever. SettlingProgress carries why that is not theoretical (#2391).
                var itemProgress = new SettlingProgress<int>(p =>
                {
                    var currentPass = (int)Math.Ceiling(p / 100.0 * totalPasses);
                    item.Status = $"Shredding pass {currentPass}/{totalPasses}...";
                });

                try
                {
                    if (item.IsFolder)
                    {
                        item.Status = $"Shredding pass 1/{totalPasses}...";
                        // No ConfigureAwait(false): this is the UI-facing command body, so the
                        // continuation must resume on the captured Dispatcher. The post-await code
                        // mutates the bound Items collection (RemoveAt in finally) and item.Status,
                        // which throw if run off the UI thread. The service's internal awaits keep
                        // ConfigureAwait(false).
                        var report = await itemProgress.SettleAfterAsync(
                            reporter => _service.ShredFolderAsync(item.Path, SelectedMethod, reporter, ct));
                        if (report.Notice is { } notice)
                            notices.Add($"{item.Name}: {notice}");

                        // A cancelled folder is not done: the files it had not reached are still inside, so
                        // it stays in the queue with an honest status instead of being marked Done and
                        // silently removed from the list.
                        if (report.WasCancelled)
                        {
                            item.Status = report.FilesShredded == 0 ? "Cancelled" : "Partly shredded";
                            cancelled = true;
                            break;
                        }
                    }
                    else
                    {
                        item.Status = $"Shredding pass 1/{totalPasses}...";
                        var passesRun = await itemProgress.SettleAfterAsync(
                            reporter => _service.ShredFileAsync(item.Path, SelectedMethod, reporter, ct));

                        // Cancelling after the overwrite began cannot bring the file back, so the service
                        // finishes and removes it rather than leaving a corrupt one behind. It IS shredded —
                        // just with fewer passes than asked for, which the user has to be told because they
                        // pressed Cancel and the file is gone anyway (#2374).
                        if (passesRun < totalPasses)
                            notices.Add(
                                $"{item.Name}: you cancelled once the overwrite had started, so it was "
                                + $"finished with {passesRun} of {totalPasses} passes and removed — the data "
                                + "was already unrecoverable at that point.");
                    }

                    item.Status = "Done";
                    destroyed.Add(item);
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    // Reachable only BEFORE this item's first byte — the service no longer abandons an
                    // overwrite it has started, so this arm can never again be the "file destroyed but
                    // reported cancelled" case. "Cancelled" here is therefore literally true.
                    item.Status = "Cancelled";
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    item.Status = "Failed";
                    failed++;
                    Log.Warning(ex, "Failed to shred: {Path}", item.Path);

                    // The service words these for the user — which files were left in place, why a hard-
                    // linked file cannot be shredded, that a junction would have destroyed data
                    // elsewhere. Show it. The Status column is 160px and would truncate it, so it goes to
                    // the wrapping caption at the bottom of the view instead.
                    notices.Add($"{item.Name}: {ex.Message}");
                }
            }

            var headline = cancelled
                ? $"Stopped — {completed} shredded, {failed} failed."
                : $"Complete — {completed} shredded, {failed} failed.";

            StatusMessage = notices.Count == 0 ? headline : headline + " " + string.Join(" ", notices);
            ToastService.Instance.Show(
                cancelled ? "File Shredder stopped" : "File Shredder complete",
                $"{completed} shredded, {failed} failed");

            // COUNT ONLY — never a file name or path. activity.json is plain text under
            // %LocalAppData%, so recording the name of a file the user chose to destroy beyond
            // recovery would leave behind exactly the evidence the shred was meant to remove, and
            // would outlive the file itself. The pass count is safe and is the useful part.
            if (completed > 0)
            {
                ActivityLogService.Instance.Log("File Shredder",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Securely erased {completed:N0} item{(completed == 1 ? "" : "s")} ({(int)SelectedMethod}-pass overwrite)"));
            }
        }
        finally
        {
            IsShredding = false;
            IsBusy = false;

            // Remove what was actually destroyed, by identity. Keying this on Status == "Done" made the
            // queue a function of a display string that the async progress callback also writes, so a
            // report draining late left an erased file sitting in the list under a "Shredding pass 2/3..."
            // label that would never change.
            foreach (var item in destroyed)
                Items.Remove(item);

            ShredAllCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanShredAll() => Items.Count > 0 && !IsShredding;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    partial void OnIsShreddingChanged(bool value)
    {
        ShredAllCommand.NotifyCanExecuteChanged();
        // The queue must not change mid-shred (the shred iterates a snapshot), so disable the
        // add/remove commands while shredding and re-enable them when it finishes.
        AddFilesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
    }

    // Add/remove are disabled while a shred runs so the queue can't be mutated mid-operation.
    private bool CanEditQueue => !IsShredding;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
