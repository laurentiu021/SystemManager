// SysManager · ShortcutCleanerViewModel — find and remove broken shortcuts
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Shortcut Cleaner tab — scans for broken .lnk files and allows deletion.
/// </summary>
public partial class ShortcutCleanerViewModel : ViewModelBase
{
    private readonly ShortcutCleanerService _service = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<BrokenShortcut> BrokenShortcuts { get; } = new();

    [ObservableProperty] private string _scanStatus = "Click Scan to find broken shortcuts.";
    [ObservableProperty] private string _currentLocation = "";
    [ObservableProperty] private int _brokenCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _moveToRecycleBin = true;

    public ShortcutCleanerViewModel() { }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;

        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Shortcut Scan");
        if (opLock == null)
        {
            ScanStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
            return;
        }

        IsScanning = true;
        IsBusy = true;
        IsProgressIndeterminate = true;
        BrokenShortcuts.Clear();
        BrokenCount = 0;
        SelectedCount = 0;
        ScanStatus = "Scanning...";
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(msg => CurrentLocation = msg);
            var results = await _service.ScanAsync(progress, _cts.Token);

            foreach (var s in results)
            {
                s.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(BrokenShortcut.IsSelected))
                        SelectedCount = BrokenShortcuts.Count(x => x.IsSelected);
                };
                BrokenShortcuts.Add(s);
            }

            BrokenCount = BrokenShortcuts.Count;
            SelectedCount = BrokenShortcuts.Count(x => x.IsSelected);
            ScanStatus = BrokenCount == 0
                ? "No broken shortcuts found — your system is clean."
                : $"Found {BrokenCount} broken shortcut{(BrokenCount == 1 ? "" : "s")}.";
            CurrentLocation = "";
            Log.Information("Shortcut scan completed: {Count} broken shortcuts found", BrokenCount);
        }
        catch (OperationCanceledException) { ScanStatus = "Scan cancelled."; }
        catch (System.IO.IOException ex) { ScanStatus = $"Scan failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ScanStatus = $"Scan failed: {ex.Message}"; }
        finally
        {
            IsScanning = false;
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = BrokenShortcuts.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ScanStatus = "No shortcuts selected for deletion.";
            return;
        }

        var action = MoveToRecycleBin ? "move to Recycle Bin" : "permanently delete";
        var result = MessageBox.Show(
            $"Are you sure you want to {action} {selected.Count} broken shortcut{(selected.Count == 1 ? "" : "s")}?",
            "Delete Broken Shortcuts — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var deleted = ShortcutCleanerService.DeleteShortcuts(selected, MoveToRecycleBin);

        // Remove deleted items from the list
        foreach (var s in selected.Where(s => !System.IO.File.Exists(s.ShortcutPath)))
        {
            BrokenShortcuts.Remove(s);
        }

        BrokenCount = BrokenShortcuts.Count;
        SelectedCount = BrokenShortcuts.Count(x => x.IsSelected);
        ScanStatus = $"Deleted {deleted} shortcut{(deleted == 1 ? "" : "s")}. {BrokenCount} remaining.";
        Log.Information("Deleted {Count} broken shortcuts (recycle bin: {RecycleBin})", deleted, MoveToRecycleBin);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var s in BrokenShortcuts) s.IsSelected = true;
        SelectedCount = BrokenShortcuts.Count;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var s in BrokenShortcuts) s.IsSelected = false;
        SelectedCount = 0;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts?.Dispose();
        base.Dispose(disposing);
    }
}
