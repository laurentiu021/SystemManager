// SysManager · BrowserCleanerViewModel — per-browser cache/cookies/history cleanup
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Browser Cleaner tab. Scans installed browsers for cleanable data
/// (cache, history, cookies, sessions), shows the size of each, and removes the selected
/// items after confirmation. Cookies/sessions are flagged and unselected by default so a
/// clean never signs the user out by accident. Operates on per-user data — no admin needed.
/// </summary>
public sealed partial class BrowserCleanerViewModel : ViewModelBase
{
    private readonly BrowserCleanerService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<BrowserCleanupItem> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string _totalSelectedDisplay = "";

    public BrowserCleanerViewModel(BrowserCleanerService service)
    {
        _service = service;
        StatusMessage = "Scanning installed browsers…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(ScanAsync);
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy))
        {
            ScanCommand.NotifyCanExecuteChanged();
            CleanCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnItemSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserCleanupItem.IsSelected)) UpdateSelectedTotal();
    }

    private void UpdateSelectedTotal()
    {
        var bytes = Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        TotalSelectedDisplay = FormatHelper.FormatSize(bytes);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning installed browsers…";
        foreach (var old in Items) old.PropertyChanged -= OnItemSelectionChanged;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var items = await _service.ScanAsync(_cts.Token).ConfigureAwait(true);
            Items.ReplaceWith(items);
            foreach (var i in Items) i.PropertyChanged += OnItemSelectionChanged;
            HasItems = Items.Count > 0;
            UpdateSelectedTotal();
            var total = FormatHelper.FormatSize(Items.Sum(i => i.SizeBytes));
            StatusMessage = Items.Count == 0
                ? "No cleanable browser data found."
                : $"Found {Items.Count} categories across your browsers — {total} total. Cookies and sessions are left unticked to keep you signed in.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CleanAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Nothing selected. Tick the categories to clean.";
            return;
        }

        var sensitive = selected.Where(i => i.IsSensitive).ToList();
        var sensitiveNote = sensitive.Count > 0
            ? $"\n\nNote: this includes {sensitive.Count} cookie/session item(s) — clearing them will sign you out of websites and close saved sessions."
            : "";
        var sizeNote = FormatHelper.FormatSize(selected.Sum(i => i.SizeBytes));

        if (!DialogService.Instance.Confirm(
                $"Clean {selected.Count} selected categor{(selected.Count == 1 ? "y" : "ies")} (~{sizeNote})?\n\n" +
                "Close the affected browsers first so locked files can be removed. Open files are skipped, not forced." +
                sensitiveNote,
                "Clean browser data"))
        {
            StatusMessage = "Clean cancelled.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Cleaning…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var deleted = await _service.CleanAsync(selected, _cts.Token).ConfigureAwait(true);
            StatusMessage = $"Removed {deleted} file(s). Re-scanning…";
            ToastService.Instance.Show("Browser data cleaned", $"{deleted} files removed.");
            Log.Information("BrowserCleaner: cleaned {Count} categories, {Deleted} files", selected.Count, deleted);
            await ScanAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            foreach (var i in Items) i.PropertyChanged -= OnItemSelectionChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
