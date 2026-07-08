// SysManager · FileShredderViewModel — secure file deletion UI logic
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
            }
        }
    }

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
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied while reading folder: {Path}", folderPath);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Could not read folder info: {Path}", folderPath);
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

        try
        {
            // Shred a SNAPSHOT of the queue, not the live collection: indexing Items across the
            // per-file awaits would race a concurrent Add/Remove. (Those commands are also
            // disabled while shredding via CanEditQueue; the snapshot is the belt-and-suspenders.)
            foreach (var item in Items.ToArray())
            {
                ct.ThrowIfCancellationRequested();

                var totalPasses = (int)SelectedMethod;

                var itemProgress = new Progress<int>(p =>
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
                        await _service.ShredFolderAsync(item.Path, SelectedMethod, itemProgress, ct);
                    }
                    else
                    {
                        item.Status = $"Shredding pass 1/{totalPasses}...";
                        await _service.ShredFileAsync(item.Path, SelectedMethod, itemProgress, ct);
                    }

                    item.Status = "Done";
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelled";
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    item.Status = "Failed";
                    failed++;
                    Log.Warning(ex, "Failed to shred: {Path}", item.Path);
                }
            }

            StatusMessage = $"Complete — {completed} shredded, {failed} failed.";
            ToastService.Instance.Show("File Shredder complete", $"{completed} shredded, {failed} failed");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Shredding cancelled.";
        }
        finally
        {
            IsShredding = false;
            IsBusy = false;

            // Remove successfully shredded items
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].Status == "Done")
                    Items.RemoveAt(i);
            }

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
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
