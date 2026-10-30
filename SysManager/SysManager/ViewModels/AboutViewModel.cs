// SysManager · AboutViewModel — version info + update check + install
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => LoadHistoryCommand;

    /// <inheritdoc/>
    /// <remarks>
    /// Only while a download runs: Escape stops that, and nothing else on this tab is cancellable — the check and
    /// the notes are short reads, and an install that has started is a new process, not work this tab holds.
    /// </remarks>
    protected internal override IRelayCommand? EscapeCancel =>
        IsDownloading ? CancelDownloadCommand : null;

    private readonly IUpdateService _updates;
    private readonly SystemReportService _reportService;
    private readonly DiagnosticsBundleService _bundle;
    private readonly UpdateCheckPreferenceService _preferences;

    /// <summary>Overrides where the retained previous build is looked for. Null = the real profile.</summary>
    private readonly string? _updatesDir;

    /// <summary>The running executable's full path, or null when Windows does not say.</summary>
    private readonly Func<string?> _processPath;

    /// <summary>Starts the build that installs or restores SysManager. A test records the call instead.</summary>
    private readonly Action<ProcessStartInfo> _launch;

    /// <summary>Closes SysManager once that build has started. A test records the call instead.</summary>
    private readonly Action _shutdown;
    private readonly IDialogService _dialog;

    private UpdateService.ReleaseInfo? _latest;

    // Suppresses saving while the constructor applies the loaded value, so restoring the
    // preference does not immediately rewrite the same file.
    // Armed and disarmed inside the constructor only, so it is readonly: re-arming it later would
    // leave the preference save permanently suppressed instead of just during the initial load.
    private readonly bool _loadingPreference;

    [ObservableProperty] private IReadOnlyList<ReleaseNote> _releaseHistory = [];

    /// <summary>
    /// True when the release-notes fetch finished without producing anything, so the section can say why
    /// instead of promising live notes and rendering nothing.
    /// </summary>
    /// <remarks>
    /// Not <c>ReleaseHistory.Count == 0</c>: that is also true before the first load, and the fetch is the
    /// slowest thing on the tab. A count binding would greet the user with "could not reach GitHub" while
    /// the request was still in flight.
    /// </remarks>
    [ObservableProperty] private bool _historyUnavailable;

    [ObservableProperty] private string _currentVersion = UpdateService.CurrentVersion.ToString(3);

    /// <summary>The commit this build came from, shortened, or empty when the build does not carry one.</summary>
    /// <remarks>
    /// BUILD showed the exe's last-write time, which is when the file reached this PC — the download, the winget
    /// install or an in-app update — not when it was built (#2611). See <see cref="BuildCommitOf"/>.
    /// </remarks>
    [ObservableProperty]
    private string _buildCommit = BuildCommitOf(
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // Update check state
    [ObservableProperty] private string _updateStatus = "Ready.";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _updateAvailable;
    // True when the last update check failed to reach GitHub — drives the status dot to an error colour
    // instead of the success green (the dot previously stayed green even on a hard 403/network error).
    [ObservableProperty] private bool _updateCheckFailed;
    [ObservableProperty] private string _latestVersionLabel = string.Empty;
    [ObservableProperty] private string _latestPublishedLabel = string.Empty;
    [ObservableProperty] private string _latestNotes = string.Empty;

    // Report / environment action state — deliberately separate from UpdateStatus.
    // Both used to write to UpdateStatus, which is displayed inside the update card, so
    // exporting a report or copying environment info replaced "Update available: v1.56.9"
    // with "Report saved" — discarding the more important message and making the update
    // card describe something unrelated to updates.
    [ObservableProperty] private string _reportStatus = string.Empty;

    // Download state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _isDownloading;

    [ObservableProperty] private int _downloadPercent;
    [ObservableProperty] private string _downloadStatus = string.Empty;

    /// <summary>
    /// The line under the download bar, counting the megabytes as they arrive. Its own property, so the progress
    /// callback never writes <see cref="DownloadStatus"/>, which the rows saying how the download ended show and a
    /// screen reader reads out (#2661).
    /// </summary>
    [ObservableProperty] private string _downloadProgressLine = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private string? _downloadedPath;

    [ObservableProperty] private bool _autoDownloadFailed;

    /// <summary>
    /// True when this running copy is the one winget's own portable installer manages. Set once at
    /// construction from <see cref="UpdateService.IsRunningFromWinGet(string?)"/> and never changes
    /// afterward in the same session.
    /// </summary>
    /// <remarks>
    /// Blocks Download, Install and the rollback for this copy rather than letting About replace the
    /// exe winget tracks separately: doing so leaves winget's own recorded version stale, and the next
    /// <c>winget upgrade</c> or <c>winget uninstall</c> of this package then refuses with "the package
    /// has been modified" until the user adds <c>--force</c> (#2889). A winget-managed copy updates
    /// through winget instead.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private bool _isManagedByWinGet;

    /// <summary>
    /// Where the last successfully saved diagnostics bundle landed, or null before one is saved this
    /// session. Drives the "Show in folder" offer next to <see cref="ReportStatus"/> (#2861): a zip
    /// named with a timestamp is otherwise found only by typing the save dialog's path from memory.
    /// </summary>
    [ObservableProperty] private string? _lastDiagnosticsBundlePath;

    /// <summary>
    /// True when Install stopped short with the downloaded file still usable: it could not be opened, the published
    /// hash could not be fetched, the running copy could not be found, or the new version would not start. The view
    /// then shows the reason in a warning row in place of the green one, with Install still offered.
    /// </summary>
    [ObservableProperty] private bool _installFailed;

    public bool ShowDownloadButton => !IsDownloading && string.IsNullOrEmpty(DownloadedPath) && !IsManagedByWinGet;

    /// <summary>
    /// True when the previous build was retained and can be restored. Drives the visibility of the
    /// rollback button, so it only appears when there is genuinely something to go back to.
    /// </summary>
    [ObservableProperty] private bool _canRollBack;

    /// <summary>
    /// Plain-language label for the rollback button. Says WHAT it does rather than naming a
    /// mechanism — the target user does not think in terms of executables or versions on disk.
    /// </summary>
    [ObservableProperty] private string _rollBackLabel = "Go back to the previous version";

    [ObservableProperty] private string _rollBackStatus = string.Empty;

    /// <summary>
    /// The line under the two startup switches: empty, or that a choice could not be saved (#2833). Its own line, for
    /// the reason <see cref="ReportStatus"/> has one, and because the tab shows no shared status line.
    /// </summary>
    [ObservableProperty] private string _startupChecksStatus = string.Empty;

    // Report export state
    [ObservableProperty] private bool _isGeneratingReport;

    /// <summary>
    /// Whether SysManager checks GitHub for a newer version when it starts. Bound to the About
    /// tab's checkbox and persisted, so this check, one of the two the app makes on its own, is
    /// something the user can see and switch off — the manual "Check for updates" button always
    /// works regardless.
    /// </summary>
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;

    /// <summary>
    /// Whether the Dashboard asks winget for app updates when SysManager starts, the other check it makes on its own
    /// (#2613). Bound to the box under the version check and kept in the same file. Scan system, Update All Apps and
    /// the App Updates tab still ask winget when they are pressed, whatever this says.
    /// </summary>
    [ObservableProperty] private bool _checkAppUpdatesOnStartup = true;

    /// <summary>
    /// Production/designer constructor. <paramref name="configDir"/> exists so these convenience
    /// overloads are not a way AROUND the seam on the core constructor below.
    /// </summary>
    /// <remarks>
    /// The core constructor documents that <c>preferences</c> is overridable "so tests exercise the
    /// real gate against a temp directory instead of the developer's own preference file" — and all 23
    /// test constructions went through THESE overloads, which did not thread it. The seam existed, was
    /// documented, and was bypassed: every one of those tests wrote the startup-check preference into
    /// the real <c>%AppData%\SysManager</c>. Fourth instance of the shape fixed in #1772, invisible to
    /// both path ratchets for the same reason — a defaulting parameter, not a static field (#1785).
    /// </remarks>
    public AboutViewModel(string? configDir = null)
        : this(new UpdateService(),
               new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
               autoCheck: true,
               preferences: configDir is null ? null : new UpdateCheckPreferenceService(configDir),
               updatesDir: configDir)
    { }

    public AboutViewModel(IUpdateService updates, SystemReportService reportService, string? configDir = null, IDialogService? dialog = null)
        : this(updates, reportService, autoCheck: true,
               preferences: configDir is null ? null : new UpdateCheckPreferenceService(configDir),
               updatesDir: configDir, dialog: dialog)
    { }

    /// <summary>
    /// Core constructor. <paramref name="autoCheck"/> controls whether the
    /// startup update-check (a live network call that populates the update
    /// properties) runs. Production always passes true; tests pass false to
    /// assert the constructor's default state without racing the async fetch.
    /// <para><paramref name="preferences"/> is overridable so tests exercise the real gate against
    /// a temp directory instead of the developer's own preference file. <paramref name="updatesDir"/>
    /// is overridable for the same reason: the rollback check looks for a retained build under
    /// <c>%LocalAppData%</c>, and a test must be able to point that at a temp folder rather than read
    /// (or come to depend on) whatever is in the developer's real profile.</para>
    /// <para><paramref name="launch"/> and <paramref name="shutdown"/> are what installing and going back end in:
    /// starting another build and closing this one. A test replaces both, so it can drive the confirmed path
    /// without starting a file or closing anything (#2499).</para>
    /// <para><paramref name="processPath"/> decides <see cref="IsManagedByWinGet"/>: the default reads
    /// <see cref="Environment.ProcessPath"/>, the real running executable, and a test must be able to
    /// answer with a path under a fake winget folder instead (#2889).</para>
    /// </summary>
    internal AboutViewModel(
        IUpdateService updates,
        SystemReportService reportService,
        bool autoCheck,
        UpdateCheckPreferenceService? preferences = null,
        string? updatesDir = null,
        DiagnosticsBundleService? bundle = null,
        Action<ProcessStartInfo>? launch = null,
        Action? shutdown = null,
        IDialogService? dialog = null,
        Func<string?>? processPath = null)
    {
        _updates = updates;
        _reportService = reportService;
        _preferences = preferences ?? new UpdateCheckPreferenceService();
        _updatesDir = updatesDir;
        _processPath = processPath ?? (static () => Environment.ProcessPath);
        _launch = launch ?? (static startInfo => Process.Start(startInfo)?.Dispose());
        _shutdown = shutdown ?? App.RequestShutdown;
        _dialog = dialog ?? DialogService.Instance;
        // Overridable for the same reason as `preferences` and `updatesDir`: the default reads the real log
        // directory under %LocalAppData%, and a test must be able to point it at a temp folder rather than
        // read whatever is in the developer's own profile.
        _bundle = bundle ?? new DiagnosticsBundleService(reportService);
        IsManagedByWinGet = UpdateService.IsRunningFromWinGet(_processPath());

        // Load before any check can run, and suppress the save that binding the value would
        // otherwise trigger — restoring a preference must not rewrite the file it came from.
        // A choice that could not be read shows the box clear, because the startup check does not
        // run then either (#2614). Ticking it saves a choice again. Both boxes, from one read of the file.
        _loadingPreference = true;
        var saved = _preferences.Load();
        CheckForUpdatesOnStartup = saved?.CheckOnStartup ?? false;
        CheckAppUpdatesOnStartup = UpdateCheckPreferenceService.ShouldCheckAppUpdatesAtStartup(saved);
        _loadingPreference = false;

        RefreshRollbackAvailability();

        if (autoCheck)
            InitializeAsync(InitAsync);
    }

    /// <summary>
    /// Re-reads whether a retained previous build exists. Called on construction and after a
    /// rollback, so the button disappears once the copy has been consumed.
    /// </summary>
    /// <remarks>
    /// Requires the recorded checksum as well as the binary: we can only offer a rollback we are able
    /// to verify, so a copy with no checksum is not offered rather than offered-then-refused. This also
    /// means a build retained by a version that predates the checksum is not offered — that is the
    /// intended outcome, since its integrity was never recorded, and the next update writes both files.
    /// <para>Never offered on a winget-managed copy, even if one happens to be sitting on disk already
    /// (from before this copy was recognised as winget-managed) — restoring it would be the same
    /// in-place replacement Download/Install are blocked from making (#2889).</para>
    /// </remarks>
    private void RefreshRollbackAvailability()
    {
        if (IsManagedByWinGet)
        {
            CanRollBack = false;
            return;
        }

        try
        {
            CanRollBack = File.Exists(UpdateApplier.PreviousBuildPath(_updatesDir))
                       && File.Exists(UpdateApplier.PreviousBuildHashPath(_updatesDir));
        }
        catch (IOException)
        {
            // An unreadable folder simply means no rollback offer — never a crash on the About tab.
            CanRollBack = false;
        }
        catch (UnauthorizedAccessException)
        {
            CanRollBack = false;
        }
    }

    // Persist on change rather than on close: the app can be closed to the tray or killed, and a
    // setting the user visibly toggled should survive either.
    // These two decide whether SysManager goes online on its own, so a choice that could not be saved says so, under
    // them (#2833).
    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        if (_loadingPreference) return;
        StartupChecksStatus = _preferences.SetCheckOnStartup(value) ? string.Empty : ChangeNotSavedStatus;
    }

    partial void OnCheckAppUpdatesOnStartupChanged(bool value)
    {
        if (_loadingPreference) return;
        StartupChecksStatus = _preferences.SetCheckAppUpdatesOnStartup(value) ? string.Empty : ChangeNotSavedStatus;
    }

    /// <summary>
    /// The outer net for the fire-and-forget startup check. The real service's two calls catch their own
    /// failures, a reply that cannot be read included (#2663), so theirs do not end here. It stays for a
    /// client that throws instead, as <see cref="LoadHistoryAsync"/>'s catches do, so that such a failure
    /// is a warning here rather than the tab's initialisation failing. The duplicate pair that used to sit
    /// inside <see cref="CheckAtStartupAsync"/> was genuinely unreachable and was removed.
    /// </summary>
    private async Task InitAsync()
    {
        try { await CheckAtStartupAsync(); }
        catch (HttpRequestException ex) { Log.Warning("About auto-check failed (network): {Error}", ex.Message); }
        catch (TaskCanceledException ex) { Log.Warning("About auto-check timed out: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("About auto-check failed: {Error}", ex.Message); }
    }

    /// <summary>Exposes the last network error for binding ("Retry" button).</summary>
    public string LastError => _updates.LastError;

    private async Task CheckAtStartupAsync()
    {
        // The gate. Two calls used to go out on EVERY launch — latest release plus the last ten —
        // with no setting and no memory of the previous check. Skipping here rather than inside
        // CheckForUpdatesAsync is deliberate: the manual button and Retry route through that same
        // command, and they must always work. This is the startup path only.
        var preference = _preferences.Load();
        if (preference is null)
        {
            // The saved choice is there but could not be read, and it may be "off". Checking anyway is what this
            // used to do (#2614). Nothing is written over it, so the next launch reads it again.
            Log.Warning("Startup update check skipped: the saved setting could not be read");
            UpdateStatus = "The saved settings for the checks at startup could not be read, so neither ran. Use Check for updates to look now.";
            return;
        }

        if (!UpdateCheckPreferenceService.ShouldCheckAtStartup(preference, DateTimeOffset.UtcNow))
        {
            Log.Debug("Startup update check skipped (enabled={Enabled}, last={Last})",
                preference.CheckOnStartup, preference.LastCheckUtc);
            // The release history is not fetched either: it comes from GitHub too, and Refresh loads
            // it when asked.
            UpdateStatus = preference.CheckOnStartup
                ? "Checked recently. Use Check for updates to look again."
                : "Startup update check is off. Use Check for updates to look now.";
            return;
        }

        await Task.Yield();     // let the UI settle
        await CheckForUpdatesAsync();
        await LoadHistoryAsync();

        // Only a check that actually got an answer starts the 24h clock. Recording unconditionally
        // meant one offline launch throttled the next day's launches too: the user saw "Checked
        // recently. Use Check for updates to look again." with no version and no way to tell that
        // nothing had been checked at all. CheckForUpdatesAsync clears this flag at the start of
        // every attempt and sets it on each of its three failure paths, so it — not an exception —
        // is what a failure looks like here.
        //
        // A history load that failed on its own deliberately does NOT block the record. The clock
        // exists to rate-limit the latest-version question, which was answered; HistoryUnavailable
        // already tells the user the notes are missing, and Refresh reloads them without waiting
        // out a day.
        if (!UpdateCheckFailed)
        {
            _preferences.RecordCheck(DateTimeOffset.UtcNow);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateCheckFailed = false;
        UpdateStatus = "Contacting GitHub...";
        try
        {
            var latest = await _updates.GetLatestAsync();
            if (latest is null)
            {
                var detail = string.IsNullOrWhiteSpace(_updates.LastError) ? "Unknown error." : _updates.LastError;
                UpdateStatus = $"Couldn't reach GitHub — {detail} {RetryLine}";
                UpdateCheckFailed = true;
                return;
            }

            _latest = latest;
            LatestVersionLabel = $"v{latest.Version.ToString(3)}";
            LatestPublishedLabel = latest.PublishedAt == DateTimeOffset.MinValue
                ? string.Empty
                : latest.PublishedAt.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            LatestNotes = latest.Body;

            if (UpdateService.IsNewer(latest.Version, UpdateService.CurrentVersion))
            {
                UpdateAvailable = true;
                // GitHub releases always reach here before winget-pkgs does, so this copy may be offered an
                // update winget cannot deliver yet. Nothing About can do changes that — only naming the
                // right next step does (#2889).
                UpdateStatus = IsManagedByWinGet
                    ? $"Update available: {LatestVersionLabel} ({LatestPublishedLabel}). This copy was installed "
                      + $"with winget — run \"winget upgrade {UpdateService.WingetPackageId}\" to update it. "
                      + "Winget sometimes takes longer than GitHub to offer a new release."
                    : $"Update available: {LatestVersionLabel} ({LatestPublishedLabel}). Click Download to get it.";
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = $"You're up to date. Running v{UpdateService.CurrentVersion.ToString(3)}.";
            }
        }
        catch (HttpRequestException ex)
        {
            UpdateStatus = $"Network error — could not reach GitHub: {ex.Message}. {RetryLine}";
            UpdateCheckFailed = true;
        }
        catch (TaskCanceledException ex)
        {
            UpdateStatus = $"Request timed out: {ex.Message}. {RetryLine}";
            UpdateCheckFailed = true;
        }
        finally { IsCheckingForUpdates = false; }
    }

    /// <summary>
    /// What a failed check tells the user to do. Says "Check for updates" rather than "Retry" when an update is
    /// already known, because the Retry button lives in the other card: the two are mutually exclusive on
    /// <see cref="UpdateAvailable"/>, and a check that failed while an update was known keeps the update card up.
    /// </summary>
    private string RetryLine => UpdateAvailable
        ? "The update found earlier is still here — press Check for updates to look again."
        : "Click Retry to try again.";

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var list = await _updates.GetRecentAsync(10);
            if (list is null)
            {
                // GitHub could not be read. Nothing is assigned, so a refresh that failed leaves the notes that
                // are on screen there (#2831).
                HistoryUnavailable = ReleaseHistory.Count == 0;
                return;
            }

            var notes = list.Select(r => new ReleaseNote
            {
                Version = $"v{r.Version.ToString(3)}",
                Title = r.Name,
                PublishedAt = r.PublishedAt == DateTimeOffset.MinValue ? "" : r.PublishedAt.LocalDateTime.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                Body = r.Body,
                Url = r.HtmlUrl,
                // Not ==: the row's version is parsed from a three-component tag and CurrentVersion carries
                // AssemblyVersion's fourth, so Version equality never matched the two (#2418).
                IsCurrent = UpdateService.IsSameRelease(r.Version, UpdateService.CurrentVersion)
            }).ToList();

            ReleaseHistory = notes;
            HistoryUnavailable = notes.Count == 0;
        }
        // Both failures leave ReleaseHistory as it was — empty, on a first load — so the flag is what
        // separates "GitHub could not be reached" from "there is nothing to show". Without it the section
        // promised release notes "pulled live from GitHub" and then rendered nothing at all, which reads as
        // the feature being broken rather than the connection being down.
        catch (HttpRequestException ex)
        {
            Log.Debug("Release history load skipped (network): {Error}", ex.Message);
            HistoryUnavailable = true;
        }
        catch (TaskCanceledException ex)
        {
            Log.Debug("Release history load timed out: {Error}", ex.Message);
            HistoryUnavailable = true;
        }
    }

    /// <summary>
    /// Cancels the download in flight. Its own source rather than the tab's, so closing the tab does not stop a
    /// download the user left running, and so the button can stop one without stopping anything else (#2831).
    /// </summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>What the tab says about a download the user stopped. Not a failure: nothing went wrong with it.</summary>
    private const string CancelledLine = "Download cancelled. Press Download to start again.";

    /// <summary>Stops the download in flight.</summary>
    [RelayCommand]
    private void CancelDownload()
    {
        // Cancelled explicitly rather than through a linked source: an await can resume inside the cancelling
        // callbacks and dispose a linked one before its own callback runs (#2843).
        try { _downloadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the download has just finished; there is nothing to stop */ }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        // The actual safety boundary, not just ShowDownloadButton hiding the button: a winget-managed copy
        // must never acquire a downloaded file to install, since the whole point is that this copy's exe
        // is winget's to replace, not About's (#2889).
        if (IsManagedByWinGet || _latest is null || IsDownloading) return;
        IsDownloading = true;
        AutoDownloadFailed = false;
        InstallFailed = false;
        DownloadPercent = 0;
        DownloadStatus = "Downloading...";
        DownloadProgressLine = DownloadStatus;

        // Declared out here, not inside the try, so the catch below can tell the user's own Cancel from a failure:
        // the download throws OperationCanceledException for both, and the only difference is whose token it was.
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var token = cts.Token;
        try
        {
            var progress = new SettlingProgress<(long read, long? total)>(p =>
            {
                if (p.total is long t && t > 0)
                {
                    DownloadPercent = (int)(p.read * 100 / t);
                    DownloadProgressLine = $"Downloading... {p.read / 1024 / 1024} / {t / 1024 / 1024} MB";
                }
                else
                {
                    DownloadProgressLine = $"Downloading... {p.read / 1024 / 1024} MB";
                }
            });

            var path = await progress.SettleAfterAsync(reporter => _updates.DownloadAsync(_latest, reporter, token));
            if (token.IsCancellationRequested)
            {
                // The service answers a cancellation with null rather than throwing, so this is the live path and
                // the catch below is for one that throws instead.
                DownloadStatus = CancelledLine;
                return;
            }

            if (path is not null && File.Exists(path))
            {
                DownloadedPath = path;
                DownloadStatus = "Download complete. Click Install to restart with the new version.";
                DownloadPercent = 100;
            }
            else
            {
                AutoDownloadFailed = true;
                DownloadStatus = "Automatic download failed — the server may be temporarily unavailable. Click 'Manual download' to get it directly from GitHub, or try again later.";
            }
        }
        // Before the three below, and before TaskCanceledException in particular: that is what a cancelled await
        // throws, so without this the user's own Cancel was reported as "Download timed out" with the failure row
        // and the Manual download button, for a download nothing had gone wrong with.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DownloadStatus = CancelledLine;
        }
        catch (HttpRequestException ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Download failed: {ex.Message}. This usually means a network issue or firewall blocking the connection. Try 'Manual download' as fallback.";
        }
        catch (IOException ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Download failed: {ex.Message}. This usually means a network issue or firewall blocking the connection. Try 'Manual download' as fallback.";
        }
        catch (TaskCanceledException ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Download timed out: {ex.Message}";
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void OpenManualDownload()
    {
        var url = _latest?.HtmlUrl ?? $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases/latest";
        OpenUrl(url);
    }

    /// <summary>
    /// Open one release-history entry on GitHub.
    /// </summary>
    /// <remarks>
    /// <c>ReleaseNote.Url</c> was populated for all ten entries and read by nothing, so the history cards
    /// were dead ends: the user could read a note and not reach the release it came from. Takes the url as a
    /// parameter rather than reading a selected item, because the list is an <c>ItemsControl</c> with no
    /// selection — each card carries its own.
    /// <para>Guarded on the value through <c>CanExecute</c> rather than an early return inside the body: an
    /// entry whose <c>HtmlUrl</c> came back empty greys its link out instead of accepting a click that does
    /// nothing, and the decision becomes observable to a test (<c>OpenUrl</c> is a no-op without a WPF
    /// <c>Application</c>, so a guard inside the body would assert nothing).</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenRelease))]
    private void OpenRelease(string? url) => OpenUrl(url!);

    private static bool CanOpenRelease(string? url) => !string.IsNullOrWhiteSpace(url);

    [RelayCommand]
    private void OpenRepo() => OpenUrl($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}");

    [RelayCommand]
    private void OpenChangelog() => OpenUrl($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/blob/main/CHANGELOG.md");

    [RelayCommand]
    private void OpenLicense() => OpenUrl($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/blob/main/LICENSE");

    /// <summary>
    /// Opens the repository's bug-report form with the version and elevation fields pre-filled. The
    /// Preview banner tells users to "report anything unexpected on GitHub" but nothing in the app
    /// gave them a way there — so a bug simply never got reported. Version and elevation are the two
    /// fields the template marks required and that users most often leave blank or get wrong, and the
    /// app already knows both.
    /// </summary>
    [RelayCommand]
    private void ReportProblem() => OpenUrl(BuildBugReportUrl(UpdateService.CurrentVersion.ToString(3), AdminHelper.IsElevated()));

    /// <summary>Opens Discussions for questions, mirroring SUPPORT.md's bug-vs-question split.</summary>
    [RelayCommand]
    private void OpenDiscussions() => OpenUrl(QuestionsUrl);

    /// <summary>
    /// The Q&amp;A category, not the Discussions root. Every release auto-posts an announcement, so the
    /// root is a wall of changelogs — a user who pressed "Ask a question" landed there and had to work
    /// out that Q&amp;A was a separate category further down the page. Deep-linking opens the right
    /// category directly.
    /// </summary>
    /// <remarks>
    /// <c>q-a</c> is GitHub's slug for the "Q&amp;A" category. The four support routes (this button,
    /// SUPPORT.md, README.md and the issue-template chooser) must all point at the same place, so the
    /// URL lives in one testable member rather than being interpolated at each call site.
    /// </remarks>
    internal static string QuestionsUrl =>
        $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/discussions/categories/q-a";

    /// <summary>
    /// The GitHub issue-form URL with the <c>version</c> and <c>elevation</c> fields pre-filled through
    /// query parameters. Pure and static so the pre-fill — which silently breaks if a template field id
    /// or a dropdown option string drifts — is unit-testable without opening a browser.
    /// </summary>
    /// <remarks>
    /// The field ids (<c>version</c>, <c>elevation</c>) and the elevation option strings
    /// ("Yes (elevated)" / "No (standard user)") must match <c>.github/ISSUE_TEMPLATE/bug_report.yml</c>
    /// exactly; GitHub silently ignores an unknown id, so a drift degrades to an empty field rather than
    /// an error. Values are URL-encoded because the option strings contain spaces and parentheses.
    /// </remarks>
    internal static string BuildBugReportUrl(string version, bool isElevated)
    {
        var elevation = isElevated ? "Yes (elevated)" : "No (standard user)";
        return $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/issues/new"
             + "?template=bug_report.yml"
             + $"&version={Uri.EscapeDataString(version)}"
             + $"&elevation={Uri.EscapeDataString(elevation)}";
    }

    /// <summary>
    /// Copy a bug-report-ready block with SysManager version, Windows version,
    /// architecture, .NET runtime, elevation state, and hardware diagnostics
    /// (CPU, RAM, GPU, storage, display) to the clipboard.
    /// Fully defensive — falls back gracefully on any WMI / registry miss.
    /// </summary>
    [RelayCommand]
    private async Task CopyEnvironmentInfoAsync()
    {
        try
        {
            var text = await Task.Run(() => CollectEnvironmentInfo()).ConfigureAwait(true);
            ReportStatus = Helpers.ClipboardFeedback.TrySetText(text)
                ? "Environment info copied to clipboard."
                : Helpers.ClipboardFeedback.Busy;
        }
        catch (System.Management.ManagementException ex)
        {
            ReportStatus = $"Couldn't collect environment info: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            ReportStatus = $"Couldn't collect environment info: {ex.Message}";
        }
    }

    private string CollectEnvironmentInfo()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("SysManager ").Append(UpdateService.CurrentVersion.ToString(3));
            if (!string.IsNullOrWhiteSpace(BuildCommit)) sb.Append(" (build ").Append(BuildCommit).Append(')');
            sb.AppendLine();
            sb.Append("Windows: ").AppendLine(DescribeWindows());
            sb.Append("Architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
            sb.Append(".NET: ").AppendLine(RuntimeInformation.FrameworkDescription);
            sb.Append("Elevated: ").AppendLine(SafeIsElevated() ? "yes" : "no");
            // Culture (the regional format) and display language (the UI language) are independent
            // Windows settings, and several log/report parsers match English text (#2861).
            sb.Append("Language: ").Append(CultureInfo.CurrentCulture.Name)
              .Append(" (display ").Append(CultureInfo.CurrentUICulture.Name).AppendLine(")");

            // CPU
            try
            {
                using var cpuSearch = Wmi.Searcher(
                    "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor");
                using var cpuResults = cpuSearch.Get();
                foreach (System.Management.ManagementObject mo in cpuResults)
                    using (mo)
                    {
                        var name = mo["Name"]?.ToString()?.Trim() ?? "unknown";
                        var cores = mo["NumberOfCores"];
                        var threads = mo["NumberOfLogicalProcessors"];
                        var mhz = mo["MaxClockSpeed"];
                        sb.Append("CPU: ").Append(name);
                        if (cores is not null) sb.Append($" ({cores}c/{threads}t)");
                        if (mhz is uint speed) sb.Append(string.Create(CultureInfo.InvariantCulture, $" @ {speed / 1000.0:F1} GHz"));
                        sb.AppendLine();
                        break;
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("CPU info unavailable: {Error}", ex.Message); }

            // RAM
            try
            {
                using var memSearch = Wmi.Searcher(
                    "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                using var memResults = memSearch.Get();
                foreach (System.Management.ManagementObject mo in memResults)
                    using (mo)
                    {
                        var totalKb = mo["TotalVisibleMemorySize"] as ulong? ?? 0;
                        var freeKb = mo["FreePhysicalMemory"] as ulong? ?? 0;
                        if (totalKb > 0)
                            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"RAM: {totalKb / 1024.0 / 1024.0:F1} GB total, {freeKb / 1024.0 / 1024.0:F1} GB free"));
                        break;
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("RAM info unavailable: {Error}", ex.Message); }

            // GPU
            try
            {
                using var gpuSearch = Wmi.Searcher(
                    "SELECT Name,DriverVersion,AdapterRAM,PNPDeviceID FROM Win32_VideoController");
                using var gpuResults = gpuSearch.Get();
                foreach (System.Management.ManagementObject mo in gpuResults)
                    using (mo)
                    {
                        var name = mo["Name"]?.ToString()?.Trim() ?? "unknown";
                        var driver = mo["DriverVersion"]?.ToString() ?? "";
                        // AdapterRAM is a uint32 that caps near 4 GiB; the shared helper prefers the
                        // driver's 64-bit qwMemorySize so >4 GB cards report their true VRAM.
                        var pnpId = mo["PNPDeviceID"]?.ToString();
                        ulong? adapterRam = mo["AdapterRAM"] is { } ram ? Convert.ToUInt64(ram) : null;
                        var vramGB = SysManager.Helpers.GpuVramHelper.ResolveVramGB(pnpId, adapterRam);
                        sb.Append("GPU: ").Append(name);
                        if (vramGB is > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $" ({vramGB:F1} GB VRAM)"));
                        if (!string.IsNullOrEmpty(driver)) sb.Append($" driver {driver}");
                        sb.AppendLine();
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("GPU info unavailable: {Error}", ex.Message); }

            // Storage
            try
            {
                foreach (var line in StorageLines(DriveInfo.GetDrives().Select(DriveProbe.Of)))
                    sb.AppendLine(line);
            }
            catch (IOException ex) { Log.Debug("Storage info unavailable: {Error}", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Storage info access denied: {Error}", ex.Message); }

            // Display
            try
            {
                using var dispSearch = Wmi.Searcher(
                    "SELECT CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate FROM Win32_VideoController");
                using var dispResults = dispSearch.Get();
                foreach (System.Management.ManagementObject mo in dispResults)
                    using (mo)
                    {
                        var w = mo["CurrentHorizontalResolution"];
                        var h = mo["CurrentVerticalResolution"];
                        var hz = mo["CurrentRefreshRate"];
                        if (w is not null && h is not null)
                        {
                            sb.Append($"Display: {w}×{h}");
                            if (hz is not null) sb.Append($" @ {hz} Hz");
                            sb.AppendLine();
                            break;
                        }
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("Display info unavailable: {Error}", ex.Message); }

            var text = sb.ToString();
            return text;
        }
        catch (System.Management.ManagementException ex)
        {
            return $"Couldn't collect environment info: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Couldn't collect environment info: {ex.Message}";
        }
    }

    /// <summary>
    /// The environment info's line for each fixed drive that is ready: its size, its free space and its format. One
    /// that cannot be read is left out and the rest are listed; the first that threw used to end the list (#2802).
    /// </summary>
    internal static List<string> StorageLines(IEnumerable<DriveProbe> drives) =>
        DriveProbe.ReadFixed(drives, drive =>
            $"Disk {drive.Name.TrimEnd('\\')} {drive.TotalSize() / 1024.0 / 1024.0 / 1024.0:F0} GB total, "
            + $"{drive.FreeSpace() / 1024.0 / 1024.0 / 1024.0:F0} GB free ({drive.Format()})");

    private static string DescribeWindows()
    {
        try
        {
            // WMI Caption gives a friendly name like "Microsoft Windows 11 Pro"
            using var searcher = Wmi.Searcher("SELECT Caption,BuildNumber FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject mo in results)
                using (mo)
                {
                    var caption = mo["Caption"]?.ToString()?.Trim() ?? "";
                    var build = mo["BuildNumber"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(caption))
                        return $"{caption} (build {build})";
                }
        }
        catch (System.Management.ManagementException ex) { Log.Debug("WMI OS info unavailable: {Error}", ex.Message); }

        // Fallback to Environment.OSVersion
        try
        {
            var os = Environment.OSVersion;
            return $"{os.VersionString} (build {os.Version.Build})";
        }
        catch (InvalidOperationException) { return "unknown"; }
    }

    private static bool SafeIsElevated()
    {
        try { return AdminHelper.IsElevated(); }
        catch (InvalidOperationException) { return false; }
    }

    [RelayCommand]
    private async Task ExportToFileAsync()
    {
        if (IsGeneratingReport) return;

        // Ask where to save, matching every other export in the app (System Report, Logs,
        // Resource History, Profile). This previously wrote straight to the Desktop, which
        // both differed from those tabs and failed outright when the Desktop is redirected
        // to OneDrive or locked down by policy — with no way for the user to choose another
        // location. The dialog is shown before any work starts so cancelling costs nothing.
        var fileName = $"SysManager-Report-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.txt";
        var path = _dialog.PickSaveFile(fileName, "Text file (*.txt)|*.txt|All files (*.*)|*.*");
        if (path is null) return;

        IsGeneratingReport = true;
        ReportStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);
            ReportStatus = $"Report saved: {Path.GetFileName(path)}";
            ToastService.Instance.Show("Report exported", Path.GetFileName(path));
        }
        catch (IOException ex)
        {
            ReportStatus = $"Failed to save report: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportStatus = $"Failed to save report (access denied): {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            ReportStatus = $"Failed to generate report: {ex.Message}";
        }
        finally { IsGeneratingReport = false; }
    }

    /// <summary>
    /// Writes one zip holding the report, the environment block and the newest log files.
    /// </summary>
    /// <remarks>
    /// The four steps this replaces were: copy the environment block, export the full report, then leave the
    /// app, open <c>%LOCALAPPDATA%\SysManager\logs\</c> in Explorer and work out which of up to fourteen
    /// daily rolling files covers the moment the bug happened. The target user cannot do the last two, so
    /// their bug was unreportable — not badly reported, unreportable (#1650).
    /// <para>Uses the SHARABLE report, not the full one. The bundle exists to be attached to a public issue,
    /// and the full report carries the adapter's MAC address, which is permanent and cannot be withdrawn once
    /// posted. <see cref="DiagnosticsBundleService"/> holds that decision and the README inside the zip says
    /// what was left out and why.</para>
    /// <para>Nothing is uploaded. The dialog comes first, so cancelling costs nothing, and the file goes
    /// exactly where the user pointed it.</para>
    /// </remarks>
    [RelayCommand]
    private async Task SaveDiagnosticsBundleAsync()
    {
        if (IsGeneratingReport) return;

        var fileName = $"SysManager-diagnostics-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.zip";
        var path = _dialog.PickSaveFile(fileName, "Zip archive (*.zip)|*.zip|All files (*.*)|*.*");
        if (path is null) return;

        IsGeneratingReport = true;
        ReportStatus = "Collecting diagnostics…";
        try
        {
            var environment = await Task.Run(CollectEnvironmentInfo).ConfigureAwait(true);
            var contents = await _bundle.WriteAsync(path, environment).ConfigureAwait(true);

            var logs = contents.LogFilesIncluded switch
            {
                0 => "no log files were found",
                1 => "1 log file",
                var n => string.Create(CultureInfo.InvariantCulture, $"{n} log files"),
            };
            ReportStatus = string.Create(CultureInfo.InvariantCulture,
                $"Saved {Path.GetFileName(path)} — report, environment info and {logs}. Nothing was sent anywhere.");
            LastDiagnosticsBundlePath = path;
            ToastService.Instance.Show("Diagnostics bundle saved", Path.GetFileName(path));
        }
        catch (IOException ex)
        {
            ReportStatus = $"Failed to save the diagnostics bundle: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportStatus = $"Failed to save the diagnostics bundle (access denied): {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            ReportStatus = $"Failed to collect diagnostics: {ex.Message}";
        }
        finally { IsGeneratingReport = false; }
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (IsGeneratingReport) return;
        IsGeneratingReport = true;
        ReportStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            if (Helpers.ClipboardFeedback.TrySetText(report))
            {
                ReportStatus = "Full system report copied to clipboard.";
            }
            else
            {
                ReportStatus = Helpers.ClipboardFeedback.Busy;
            }
        }
        catch (InvalidOperationException ex)
        {
            ReportStatus = $"Failed to generate report: {ex.Message}";
        }
        finally { IsGeneratingReport = false; }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        InstallFailed = false;

        // Defensive, since DownloadAsync already refuses a winget-managed copy a file to install — but
        // Install is independently reachable, and this is the actual boundary that must hold either way
        // (#2889).
        if (IsManagedByWinGet)
        {
            InstallStopped($"This copy was installed with winget — run \"winget upgrade {UpdateService.WingetPackageId}\" to update it.", unusable: false);
            return;
        }

        var path = DownloadedPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            InstallStopped("No downloaded file to install.", unusable: true);
            return;
        }

        if (_latest is null)
        {
            InstallStopped("No release info available.", unusable: false);
            return;
        }

        // SEC: Open the downloaded binary with deny-write sharing BEFORE verification
        // and hold the handle across Process.Start. This eliminates the TOCTOU window
        // (verify-by-path then execute-by-path on a user-writable %LOCALAPPDATA% file)
        // that previously allowed a same-user attacker to swap the verified binary for a
        // malicious one between VerifyHashAsync and Process.Start — inheriting the
        // caller's (possibly elevated) integrity level on UseShellExecute=true.
        FileStream? lockedStream = null;
        try
        {
            lockedStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (IOException ex)
        {
            InstallStopped($"Cannot lock update file: {ex.Message}", unusable: false);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            InstallStopped($"Cannot access update file: {ex.Message}", unusable: false);
            return;
        }

        // Set when the file itself is refused, so it is deleted once the handle on it is closed.
        var refused = false;
        try
        {
            // Step 1: Verify SHA256 hash before installing — hashes from the held stream
            // so no other process can modify the bytes between verify and launch.
            DownloadStatus = "Verifying file integrity...";
            var (verified, expected, actual) = await _updates.VerifyHashAsync(_latest, lockedStream);
            if (!verified && expected is not null && actual is not null)
            {
                refused = true;
                InstallStopped($"SHA256 mismatch — file may be corrupted. Expected: {expected[..12]}… Got: {actual[..12]}…",
                    unusable: true);
                return;
            }
            if (!verified || expected is null)
            {
                // The published hash could not be fetched or the file read, which says nothing against the file.
                // A verified result with no hash to show cannot happen, and if it ever did there would be nothing
                // to hand the applier, so it is refused the same way rather than installed unchecked (#2832).
                InstallStopped("Could not check the download against its published hash. Check the internet "
                    + "connection, then press Install again.", unusable: false);
                return;
            }

            // Step 1b: Authenticode check. Integrity is already guaranteed by the SHA256
            // step above; this only rejects a file whose signature data is present but
            // unreadable. Unsigned builds (SysManager ships unsigned) are allowed.
            // VerifyAuthenticode takes a path — it opens its own handle (read-shared), which
            // is fine: our deny-write handle prevents modification. The file cannot change
            // between SHA256 verification and this call because we still hold the lock.
            if (!UpdateService.VerifyAuthenticode(path))
            {
                refused = true;
                InstallStopped("Update binary's signature could not be read. Download aborted.", unusable: true);
                return;
            }

            // Step 2: Determine current executable path.
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                InstallStopped("Cannot determine current executable path.", unusable: false);
                return;
            }

            // Step 2b: Ask. Installing closes SysManager, and this was the one exit that did not say so: it
            // launched the new version and shut down half a second later, cutting off anything still running
            // (#2499). After the checks, so the user is never asked to approve something that is then refused, and
            // with the verified file still held, so it cannot change while the dialog is open.
            if (!DialogService.Instance.Confirm(
                    $"Install SysManager {LatestVersionLabel}?\n\n" +
                    "SysManager closes, installs the update and opens again. Your settings are not affected." +
                    QuitGuard.ActiveWorkWarning(),
                    "Install update"))
            {
                DownloadStatus = "The update is downloaded and checked, ready to install.";
                return;
            }

            // Step 3: Launch the verified binary. The deny-write handle remains open,
            // guaranteeing the file on disk is byte-for-byte what we hashed. Process.Start
            // opens its own read handle (compatible with FileShare.Read), so the launch
            // succeeds without releasing our lock. The OS loads the executable image from
            // this same file — and Windows itself holds the image section object open for
            // the lifetime of the new process, so even after we close our handle on
            // shutdown the image that is RUNNING cannot change.
            //
            // What that does NOT cover is the file the applier then installs: this handle is closed in the
            // finally below, and the applier used to read the path again by name, out of a folder the user can
            // write to. So the hash goes with the launch and the applier checks the bytes on a handle of its
            // own before installing them (#2832).
            var pid = Environment.ProcessId;
            // The hash travels with the launch, so the applier can check the bytes it is about to install on a
            // handle of its own rather than trusting this one, which is closed by the time it reads (#2832).
            var args = UpdateApplier.BuildArguments(currentExe, pid, expected);

            DownloadStatus = "Installing update — SysManager will restart...";

            _launch(new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = true
            });

            // Give the applier a moment to start before we exit.
            await Task.Delay(500);
            _shutdown();
        }
        catch (InvalidOperationException ex)
        {
            InstallStopped($"Update failed: {ex.Message}", unusable: false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            InstallStopped($"Update failed: {ex.Message}", unusable: false);
        }
        finally
        {
            lockedStream.Dispose();
            // The cache hands a file back while it matches the hash stored beside it, which is its own, so a refused
            // file kept would come back from the next Download at once and be refused again.
            if (refused) _updates.DiscardDownload(path);
        }
    }

    /// <summary>
    /// Shows why Install stopped short, in a row that reads as a problem rather than in the green one.
    /// </summary>
    /// <param name="message">What went wrong, for the row and for a screen reader.</param>
    /// <param name="unusable">
    /// True when the downloaded file itself was refused or is gone. It is let go, so Download and Manual download come
    /// back where an Install that cannot succeed was offered. Otherwise the file is kept, and Install stays offered.
    /// </param>
    /// <remarks>
    /// Install wrote every refusal and failure to <see cref="DownloadStatus"/>, which with a downloaded file only the
    /// green "download complete" row showed. "SHA256 mismatch" and "Update failed: …" read as success there, were cut
    /// off at the card's edge, and had Install offered beside them.
    /// </remarks>
    private void InstallStopped(string message, bool unusable)
    {
        DownloadStatus = message;
        if (unusable)
        {
            DownloadedPath = null;
            AutoDownloadFailed = true;
        }
        else
        {
            InstallFailed = true;
        }
    }

    /// <summary>
    /// Restores the retained previous build over the running executable.
    /// </summary>
    /// <remarks>
    /// Reuses the SAME applier path as an install — <c>BuildArguments</c> + launch the source binary
    /// with the sentinel — rather than copying files from here. That path already handles waiting for
    /// this process to exit, retrying while the target is locked, and staging via an atomic move; a
    /// second, parallel implementation would be the one without those properties.
    /// <para>Confirms first, naming what is about to happen: rolling back re-exposes whatever the
    /// newer build fixed, so it must be a deliberate choice rather than a stray click.</para>
    /// </remarks>
    [RelayCommand]
    private async Task RollBackAsync()
    {
        // Defensive, like InstallUpdateAsync's: RefreshRollbackAvailability already keeps CanRollBack
        // false for a winget-managed copy, but Rollback is independently reachable (#2889).
        if (IsManagedByWinGet)
        {
            RollBackStatus = "This copy is managed by winget — going back through SysManager is not offered for it.";
            return;
        }

        var previous = UpdateApplier.PreviousBuildPath(_updatesDir);
        if (!File.Exists(previous))
        {
            // Vanished since the button appeared (manual cleanup, disk tools). Reflect reality.
            RollBackStatus = "The previous version is no longer available.";
            RefreshRollbackAvailability();
            return;
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            RollBackStatus = "Could not determine where SysManager is running from.";
            return;
        }

        // SEC: verify the retained build against the checksum recorded when it was written, and hold the
        // verified handle from here until after the launch. The retained copy sits in a user-writable
        // folder and can have been replaced at any point since the update was applied; rollback
        // previously ran on File.Exists alone, while the install path directly above closes this same
        // window this same way.
        //
        // Before the confirmation, deliberately: never ask the user to approve something that is then
        // refused. It also means the deny-write handle is already held while the dialog is open, so the
        // file cannot be swapped during the seconds the prompt sits on screen.
        if (!UpdateApplier.TryOpenVerifiedPreviousBuild(_updatesDir, out var verifiedStream, out var why,
                                                        out var previousHash))
        {
            RollBackStatus = $"Cannot go back safely — {why}.";
            RefreshRollbackAvailability();
            return;
        }

        try
        {
            if (!DialogService.Instance.Confirm(
                    $"Go back to the version you had before the last update?\n\n" +
                    "SysManager will close and reopen on the older version. Anything the newer version " +
                    "fixed will come back, and your settings are not affected." +
                    QuitGuard.ActiveWorkWarning(),
                    "Go back to the previous version"))
            {
                return;
            }

            var args = UpdateApplier.BuildArguments(currentExe, Environment.ProcessId, previousHash);
            RollBackStatus = "Going back — SysManager will restart…";

            _launch(new ProcessStartInfo
            {
                FileName = previous,
                Arguments = args,
                UseShellExecute = true
            });

            ActivityLogService.Instance.Log("Update", "Went back to the previously installed version");

            // Let the applier start before this process exits.
            await Task.Delay(500);
            _shutdown();
        }
        catch (InvalidOperationException ex)
        {
            RollBackStatus = $"Could not go back: {ex.Message}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RollBackStatus = $"Could not go back: {ex.Message}";
        }
        finally
        {
            // Released only after the launch: Windows holds the image section open for the new process's
            // lifetime, so the binary stays immutable once it has loaded.
            verifiedStream?.Dispose();
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        if (string.IsNullOrWhiteSpace(DownloadedPath)) return;
        var dir = Path.GetDirectoryName(DownloadedPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"), $"/select,\"{DownloadedPath}\"") { UseShellExecute = true })?.Dispose(); }
            catch (InvalidOperationException) { /* explorer launch is best-effort */ }
            catch (System.ComponentModel.Win32Exception) { /* explorer launch is best-effort */ }
        }
    }

    /// <summary>
    /// Reveals the diagnostics bundle Explorer already selects, so finding the zip to drag into an
    /// issue form's new bundle field (#2861) does not need navigating %TEMP%-style save paths by hand.
    /// </summary>
    [RelayCommand]
    private void ShowDiagnosticsBundleInFolder()
    {
        if (string.IsNullOrWhiteSpace(LastDiagnosticsBundlePath)) return;
        var dir = Path.GetDirectoryName(LastDiagnosticsBundlePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo(SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"), $"/select,\"{LastDiagnosticsBundlePath}\"") { UseShellExecute = true })?.Dispose(); }
            catch (InvalidOperationException) { /* explorer launch is best-effort */ }
            catch (System.ComponentModel.Win32Exception) { /* explorer launch is best-effort */ }
        }
    }

    private static void OpenUrl(string url)
    {
        if (System.Windows.Application.Current == null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (InvalidOperationException) { /* best-effort */ }
        catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
    }

    /// <summary>
    /// The first seven characters of the commit an informational version names after its '+', or empty.
    /// </summary>
    /// <remarks>
    /// The build stamps that suffix itself, and the release workflow checks the version in front of it, so it is the
    /// build's own record of where it came from rather than anything about the PC it runs on.
    /// </remarks>
    internal static string BuildCommitOf(string? informationalVersion)
    {
        var plus = informationalVersion?.IndexOf('+') ?? -1;
        if (plus < 0) return string.Empty;
        var commit = informationalVersion![(plus + 1)..].Trim();
        return commit.Length > 7 ? commit[..7] : commit;
    }
    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsDownloadingChanged(bool value) => IsBusy = value;

    // And the download's progress to the taskbar button, which follows Progress (#2596).
    partial void OnDownloadPercentChanged(int value) => ShowOnTaskbar(value);
}

/// <summary>Single release entry in the "What's new" history.</summary>
public sealed class ReleaseNote
{
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string Body { get; init; } = "";
    public string Url { get; init; } = "";
    public bool IsCurrent { get; init; }
}
