// SysManager · BrowserCleanerViewModel — per-browser cache/cookies/history cleanup
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

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

    /// <summary>
    /// Copies the user's ticks from the previous scan onto a fresh set, matched by browser and category.
    /// </summary>
    /// <remarks>
    /// The scan pre-selects everything that is not sensitive, so a rescan was wrong in BOTH directions:
    /// cache and history the user unticked came back ticked and would then be deleted, and a cookie or
    /// session category they deliberately ticked — an explicit choice to be signed out — was silently
    /// un-ticked again. <c>RefreshOnF5</c> is <c>ScanCommand</c>, so pressing F5 was enough (#2304).
    /// <para>An empty <paramref name="previous"/> is the first scan. A previous list that is present with
    /// nothing selected is a decision and is honoured, which is why this tests the collection being empty
    /// rather than whether anything in it is selected.</para>
    /// <para>Browser plus category is the identity: a browser has at most one row per category, and both
    /// are required on the model, so the key is always present. A category that appeared since the last
    /// scan — a browser installed in between — keeps the scan's default.</para>
    /// </remarks>
    internal static void CarryForwardSelection(
        IReadOnlyCollection<BrowserCleanupItem> previous, IReadOnlyCollection<BrowserCleanupItem> fresh)
    {
        Helpers.SelectionCarry.Apply(previous, fresh, i => (i.Browser, i.Category));
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
        // Snapshot the user's ticks before the rebuild replaces every item.
        var previous = Items.ToList();

        foreach (var old in Items) old.PropertyChanged -= OnItemSelectionChanged;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var items = await _service.ScanAsync(_cts.Token).ConfigureAwait(true);

            // Applied before subscribing, so restoring a tick does not fire a selection-changed
            // notification for state the user has not just changed.
            CarryForwardSelection(previous, items);

            Items.ReplaceWith(items);
            foreach (var i in Items) i.PropertyChanged += OnItemSelectionChanged;
            HasItems = Items.Count > 0;
            UpdateSelectedTotal();
            var total = FormatHelper.FormatSize(Items.Sum(i => i.SizeBytes));
            // The reassurance about cookies is only stated when it is TRUE. Now that a deliberate tick on a
            // sensitive category survives a rescan, printing it unconditionally would tell a user who had
            // opted into being signed out the opposite of what is about to happen.
            var signInSafe = !Items.Any(i => i.IsSensitive && i.IsSelected);
            StatusMessage = Items.Count == 0
                ? "No cleanable browser data found."
                : $"Found {Items.Count} categories across your browsers — {total} total."
                  + (signInSafe
                      ? " Cookies and sessions are left unticked to keep you signed in."
                      : " You have ticked cookies or sessions, so cleaning will sign you out of those browsers.");
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
            // Counts only. Naming the categories would record which browser/profile data the user
            // chose to erase, and activity.json is plain text under %LocalAppData%.
            ActivityLogService.Instance.Log("Browser Cleaner",
                string.Create(CultureInfo.InvariantCulture,
                    $"Removed {deleted:N0} file{(deleted == 1 ? "" : "s")} from {selected.Count} categor{(selected.Count == 1 ? "y" : "ies")}"));
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
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
