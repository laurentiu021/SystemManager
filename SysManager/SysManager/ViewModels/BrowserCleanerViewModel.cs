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

/// <summary>Which half of Browser Cleaner is on screen (#1526).</summary>
public enum BrowserCleanerSection
{
    /// <summary>The cache, history, cookies and sessions the tab cleans.</summary>
    BrowsingData = 0,

    /// <summary>The extensions each browser has, listed and never changed.</summary>
    Extensions,
}

/// <summary>
/// ViewModel for the Browser Cleaner tab. Scans installed browsers for cleanable data
/// (cache, history, cookies, sessions), shows the size of each, and removes the selected
/// items after confirmation. Cookies/sessions are flagged and unselected by default so a
/// clean never signs the user out by accident. Operates on per-user data — no admin needed.
/// <para>Its second half, <see cref="BrowserCleanerSection.Extensions"/>, lists the extensions in every browser
/// and profile the cleaner reads, says in plain words what each can do and where it came from, and hands the user
/// to the browser's own page to remove one (#1526). It only reads: nothing in a browser is changed.</para>
/// </summary>
public sealed partial class BrowserCleanerViewModel : ViewModelBase, ISearchDestination
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => IsExtensions ? ScanExtensionsCommand : ScanCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly BrowserCleanerService _service;
    private readonly IBrowserExtensionService _extensions;
    private readonly Func<string, bool> _copyText;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<BrowserCleanupItem> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string _totalSelectedDisplay = "";

    /// <summary>The extensions, one group per browser profile that has any or could not be read.</summary>
    public BulkObservableCollection<ExtensionGroupViewModel> ExtensionGroups { get; } = new();

    /// <summary>Which half of the tab is on screen; the pills set it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsingData), nameof(IsExtensions))]
    private BrowserCleanerSection _section;

    /// <summary>True once the user has looked for extensions, so "none found" is said only after looking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoExtensions), nameof(ShowExtensionsPrompt))]
    private bool _hasLookedForExtensions;

    /// <summary>
    /// True when the last look has something to show: an extension, or a profile whose list could not be read.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoExtensions))]
    private bool _hasExtensions;

    /// <summary>The toolbar's count, e.g. "12 extensions in 3 browsers · 1 was put there by another program".</summary>
    [ObservableProperty] private string _extensionsSummary = "";

    /// <summary>The "Browsing data" pill; checking it shows that half. Unchecking it leaves the section to the other pill.</summary>
    public bool IsBrowsingData
    {
        get => Section == BrowserCleanerSection.BrowsingData;
        set { if (value) Section = BrowserCleanerSection.BrowsingData; }
    }

    /// <summary>The "Extensions" pill.</summary>
    public bool IsExtensions
    {
        get => Section == BrowserCleanerSection.Extensions;
        set { if (value) Section = BrowserCleanerSection.Extensions; }
    }

    /// <summary>After a look that found nothing to list: the empty state says so.</summary>
    public bool ShowNoExtensions => HasLookedForExtensions && !HasExtensions;

    /// <summary>Before the first look: the empty state says what the button does.</summary>
    public bool ShowExtensionsPrompt => !HasLookedForExtensions;

    /// <summary>Builds the tab over the cleaner, the extension list and the clipboard, and starts the first scan.</summary>
    /// <param name="extensions">Lists the extensions and opens a browser on its extensions page.</param>
    /// <param name="copyText">Puts text on the clipboard and says whether it could; the real clipboard unless a test passes its own.</param>
    public BrowserCleanerViewModel(BrowserCleanerService service, IBrowserExtensionService extensions,
        Func<string, bool>? copyText = null)
    {
        _service = service;
        _extensions = extensions;
        _copyText = copyText ?? CopyToClipboard;
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
            ScanExtensionsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The words that, typed into the sidebar search, mean the user is after an extension rather than her browsing
    /// data — the ones the tab's search keywords carry for that reason.
    /// </summary>
    internal static readonly string[] ExtensionSearchWords = ["extension", "add-on", "addon", "search changed", "browser ads"];

    /// <inheritdoc/>
    /// <remarks>
    /// "My searches go to some other site" is an extension far more often than it is the cache, so a search in the
    /// user's own words opens the half that can show it.
    /// </remarks>
    public void ArriveFromSearch(string query)
    {
        if (ExtensionSearchWords.Any(w => query.Contains(w, StringComparison.OrdinalIgnoreCase)))
            Section = BrowserCleanerSection.Extensions;
    }

    /// <summary>
    /// Lists every extension in every browser profile. Reading only: the browsers' own files are opened for
    /// reading, shared, and nothing is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanExtensionsAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Looking for extensions in every browser…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var profiles = await _extensions.ScanAsync(_cts.Token).ConfigureAwait(true);
            // The rows decode their icons, so they are built off this thread; each image is frozen for it.
            var groups = await Task.Run(() => profiles
                .Select(p => new ExtensionGroupViewModel(p, [.. p.Extensions.Select(ExtensionPresenter.Row)], Manage))
                .ToList()).ConfigureAwait(true);

            ExtensionGroups.ReplaceWith(groups);
            // A group whose list could not be read still shows, with its note: an empty list is not "none".
            HasExtensions = groups.Count > 0;
            HasLookedForExtensions = true;
            ExtensionsSummary = ExtensionPresenter.Summary(profiles);
            StatusMessage = ExtensionsSummary.Length > 0
                ? $"Found {ExtensionsSummary}."
                : groups.Count > 0
                    ? "No extensions could be listed: a browser's list could not be read."
                    : "No extensions found in any browser on this PC.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// "Manage in …": starts the profile's browser on its extensions page and puts the page's address on the
    /// clipboard, because whether the browser opened that page cannot be seen from here.
    /// </summary>
    private void Manage(ExtensionProfile profile)
    {
        var opening = _extensions.OpenExtensionsPage(profile);
        var copied = _copyText(profile.Page);
        StatusMessage = ExtensionPresenter.ManageStatus(profile, opening, copied);
    }

    private static bool CopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
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

        // A deletion batch that is cancelled with nothing to name it when SysManager closes mid-run. Shares
        // the disk-scanning/deleting tabs' lock (#2510).
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Browser Cleaner");
        if (opLock is null)
        {
            StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk)} is already running.";
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
            var (toastTitle, toastDetail) = CleanToast(deleted);
            ToastService.Instance.Show(toastTitle, toastDetail);
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

    /// <summary>
    /// The toast after a clean. The rescan replaces the "Removed N file(s)" status at once, so the toast is often
    /// all that is left of the result — and with nothing removed it must not say the data was cleaned (#2456).
    /// </summary>
    internal static (string Title, string Detail) CleanToast(int deleted) => deleted > 0
        ? ("Browser data cleaned", $"{deleted} files removed.")
        : ("Nothing was removed", "A browser that is still open keeps its files locked. Close it and clean again.");

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
