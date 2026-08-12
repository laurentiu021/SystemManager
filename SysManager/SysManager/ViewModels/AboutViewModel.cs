// SysManager · AboutViewModel — version info + update check + install
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly SystemReportService _reportService;
    private readonly UpdateCheckPreferenceService _preferences;

    /// <summary>Overrides where the retained previous build is looked for. Null = the real profile.</summary>
    private readonly string? _updatesDir;
    private UpdateService.ReleaseInfo? _latest;

    // Suppresses saving while the constructor applies the loaded value, so restoring the
    // preference does not immediately rewrite the same file.
    private bool _loadingPreference;

    [ObservableProperty] private IReadOnlyList<ReleaseNote> _releaseHistory = [];

    [ObservableProperty] private string _currentVersion = UpdateService.CurrentVersion.ToString(3);
    [ObservableProperty] private string _buildDate = BuildStamp();

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownloadButton))]
    private string? _downloadedPath;

    [ObservableProperty] private bool _autoDownloadFailed;

    public bool ShowDownloadButton => !IsDownloading && string.IsNullOrEmpty(DownloadedPath);

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

    // Report export state
    [ObservableProperty] private bool _isGeneratingReport;

    /// <summary>
    /// Whether SysManager checks GitHub for a newer version when it starts. Bound to the About
    /// tab's checkbox and persisted, so the app's one outbound call is something the user can see
    /// and switch off — the manual "Check for updates" button always works regardless.
    /// </summary>
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;

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
               new SystemReportService(new SystemInfoService(), new DiskHealthService()),
               autoCheck: true,
               preferences: configDir is null ? null : new UpdateCheckPreferenceService(configDir),
               updatesDir: configDir) { }

    public AboutViewModel(UpdateService updates, SystemReportService reportService, string? configDir = null)
        : this(updates, reportService, autoCheck: true,
               preferences: configDir is null ? null : new UpdateCheckPreferenceService(configDir),
               updatesDir: configDir) { }

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
    /// </summary>
    internal AboutViewModel(
        UpdateService updates,
        SystemReportService reportService,
        bool autoCheck,
        UpdateCheckPreferenceService? preferences = null,
        string? updatesDir = null)
    {
        _updates = updates;
        _reportService = reportService;
        _preferences = preferences ?? new UpdateCheckPreferenceService();
        _updatesDir = updatesDir;

        // Load before any check can run, and suppress the save that binding the value would
        // otherwise trigger — restoring a preference must not rewrite the file it came from.
        _loadingPreference = true;
        CheckForUpdatesOnStartup = _preferences.Load().CheckOnStartup;
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
    /// </remarks>
    private void RefreshRollbackAvailability()
    {
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
    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        if (_loadingPreference) return;
        _preferences.SetCheckOnStartup(value);
    }

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
        if (!UpdateCheckPreferenceService.ShouldCheckAtStartup(preference, DateTimeOffset.UtcNow))
        {
            Log.Debug("Startup update check skipped (enabled={Enabled}, last={Last})",
                preference.CheckOnStartup, preference.LastCheckUtc);
            // Still show the local version history — it is read from the app itself, not the
            // network, so suppressing it would hide information the user already has.
            UpdateStatus = preference.CheckOnStartup
                ? "Checked recently. Use Check for updates to look again."
                : "Startup update check is off. Use Check for updates to look now.";
            return;
        }

        try
        {
            await Task.Yield();     // let the UI settle
            await CheckForUpdatesAsync();
            await LoadHistoryAsync();
            // Record only after the calls actually went out, so a failed check does not start the
            // 24h clock and leave the user with stale information for a day.
            _preferences.RecordCheck(DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex) { Log.Debug("About startup check skipped (network): {Error}", ex.Message); }
        catch (TaskCanceledException ex) { Log.Debug("About startup check timed out: {Error}", ex.Message); }
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
                UpdateStatus = $"Couldn't reach GitHub — {detail} Click Retry to try again.";
                UpdateAvailable = false;
                UpdateCheckFailed = true;
                return;
            }

            _latest = latest;
            LatestVersionLabel = $"v{latest.Version.ToString(3)}";
            LatestPublishedLabel = latest.PublishedAt == DateTimeOffset.MinValue
                ? string.Empty
                : latest.PublishedAt.LocalDateTime.ToString("dd MMM yyyy");
            LatestNotes = latest.Body;

            if (UpdateService.IsNewer(latest.Version, UpdateService.CurrentVersion))
            {
                UpdateAvailable = true;
                UpdateStatus = $"Update available: {LatestVersionLabel} ({LatestPublishedLabel}). Click Download to get it.";
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = $"You're up to date. Running v{UpdateService.CurrentVersion.ToString(3)}.";
            }
        }
        catch (HttpRequestException ex)
        {
            UpdateStatus = $"Network error — could not reach GitHub: {ex.Message}. Click Retry to try again.";
            UpdateAvailable = false;
            UpdateCheckFailed = true;
        }
        catch (TaskCanceledException ex)
        {
            UpdateStatus = $"Request timed out: {ex.Message}. Click Retry to try again.";
            UpdateAvailable = false;
            UpdateCheckFailed = true;
        }
        finally { IsCheckingForUpdates = false; }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var list = await _updates.GetRecentAsync(10);
            var notes = list.Select(r => new ReleaseNote
            {
                Version = $"v{r.Version.ToString(3)}",
                Title = r.Name,
                PublishedAt = r.PublishedAt == DateTimeOffset.MinValue ? "" : r.PublishedAt.LocalDateTime.ToString("dd MMM yyyy"),
                Body = r.Body,
                Url = r.HtmlUrl,
                IsCurrent = r.Version == UpdateService.CurrentVersion
            }).ToList();

            ReleaseHistory = notes;
        }
        catch (HttpRequestException ex) { Log.Debug("Release history load skipped (network): {Error}", ex.Message); }
        catch (TaskCanceledException ex) { Log.Debug("Release history load timed out: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_latest is null || IsDownloading) return;
        IsDownloading = true;
        AutoDownloadFailed = false;
        DownloadPercent = 0;
        DownloadStatus = "Downloading...";
        try
        {
            var progress = new Progress<(long read, long? total)>(p =>
            {
                if (p.total is long t && t > 0)
                {
                    DownloadPercent = (int)(p.read * 100 / t);
                    DownloadStatus = $"Downloading... {p.read / 1024 / 1024} / {t / 1024 / 1024} MB";
                }
                else
                {
                    DownloadStatus = $"Downloading... {p.read / 1024 / 1024} MB";
                }
            });

            var path = await _updates.DownloadAsync(_latest, progress);
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
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private void OpenManualDownload()
    {
        var url = _latest?.HtmlUrl ?? $"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/releases/latest";
        OpenUrl(url);
    }

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
    private void OpenDiscussions() => OpenUrl($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/discussions");

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
            try
            {
                Clipboard.SetText(text);
                ReportStatus = "Environment info copied to clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log.Debug("Clipboard locked: {Error}", ex.Message);
                ReportStatus = "Couldn't copy to clipboard: it's currently in use by another application.";
            }
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
            if (!string.IsNullOrWhiteSpace(BuildDate)) sb.Append(" (build ").Append(BuildDate).Append(')');
            sb.AppendLine();
            sb.Append("Windows: ").AppendLine(DescribeWindows());
            sb.Append("Architecture: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
            sb.Append(".NET: ").AppendLine(RuntimeInformation.FrameworkDescription);
            sb.Append("Elevated: ").AppendLine(SafeIsElevated() ? "yes" : "no");

            // CPU
            try
            {
                using var cpuSearch = new System.Management.ManagementObjectSearcher(
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
                        if (mhz is uint speed) sb.Append($" @ {speed / 1000.0:F1} GHz");
                        sb.AppendLine();
                        break;
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("CPU info unavailable: {Error}", ex.Message); }

            // RAM
            try
            {
                using var memSearch = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                using var memResults = memSearch.Get();
                foreach (System.Management.ManagementObject mo in memResults)
                    using (mo)
                    {
                        var totalKb = mo["TotalVisibleMemorySize"] as ulong? ?? 0;
                        var freeKb = mo["FreePhysicalMemory"] as ulong? ?? 0;
                        if (totalKb > 0)
                            sb.AppendLine($"RAM: {totalKb / 1024.0 / 1024.0:F1} GB total, {freeKb / 1024.0 / 1024.0:F1} GB free");
                        break;
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("RAM info unavailable: {Error}", ex.Message); }

            // GPU
            try
            {
                using var gpuSearch = new System.Management.ManagementObjectSearcher(
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
                        if (vramGB is > 0) sb.Append($" ({vramGB:F1} GB VRAM)");
                        if (!string.IsNullOrEmpty(driver)) sb.Append($" driver {driver}");
                        sb.AppendLine();
                    }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("GPU info unavailable: {Error}", ex.Message); }

            // Storage
            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                    sb.AppendLine($"Disk {drive.Name.TrimEnd('\\')} {drive.TotalSize / 1024.0 / 1024.0 / 1024.0:F0} GB total, {drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:F0} GB free ({drive.DriveFormat})");
            }
            catch (IOException ex) { Log.Debug("Storage info unavailable: {Error}", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Storage info access denied: {Error}", ex.Message); }

            // Display
            try
            {
                using var dispSearch = new System.Management.ManagementObjectSearcher(
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

    private static string DescribeWindows()
    {
        try
        {
            // WMI Caption gives a friendly name like "Microsoft Windows 11 Pro"
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Caption,BuildNumber FROM Win32_OperatingSystem");
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
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-Report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        IsGeneratingReport = true;
        ReportStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            await File.WriteAllTextAsync(dlg.FileName, report, Encoding.UTF8);
            ReportStatus = $"Report saved: {Path.GetFileName(dlg.FileName)}";
            ToastService.Instance.Show("Report exported", Path.GetFileName(dlg.FileName));
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

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (IsGeneratingReport) return;
        IsGeneratingReport = true;
        ReportStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            try
            {
                Clipboard.SetText(report);
                ReportStatus = "Full system report copied to clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log.Debug("Clipboard locked: {Error}", ex.Message);
                ReportStatus = "Couldn't copy to clipboard: it's currently in use by another application.";
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
        if (string.IsNullOrWhiteSpace(DownloadedPath) || !File.Exists(DownloadedPath))
        {
            DownloadStatus = "No downloaded file to install.";
            return;
        }

        if (_latest is null)
        {
            DownloadStatus = "No release info available.";
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
                DownloadedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (IOException ex)
        {
            DownloadStatus = $"Cannot lock update file: {ex.Message}";
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            DownloadStatus = $"Cannot access update file: {ex.Message}";
            return;
        }

        try
        {
            // Step 1: Verify SHA256 hash before installing — hashes from the held stream
            // so no other process can modify the bytes between verify and launch.
            DownloadStatus = "Verifying file integrity...";
            var (verified, expected, actual) = await _updates.VerifyHashAsync(_latest, lockedStream);
            if (!verified)
            {
                DownloadStatus = expected is not null && actual is not null
                    ? $"SHA256 mismatch — file may be corrupted. Expected: {expected[..12]}… Got: {actual[..12]}…"
                    : "Hash verification failed — file may be corrupted. Try downloading again.";
                return;
            }

            // Step 1b: Authenticode check. Integrity is already guaranteed by the SHA256
            // step above; this only rejects a file whose signature data is present but
            // unreadable. Unsigned builds (SysManager ships unsigned) are allowed.
            // VerifyAuthenticode takes a path — it opens its own handle (read-shared), which
            // is fine: our deny-write handle prevents modification. The file cannot change
            // between SHA256 verification and this call because we still hold the lock.
            if (!UpdateService.VerifyAuthenticode(DownloadedPath))
            {
                DownloadStatus = "Update binary's signature could not be read. Download aborted.";
                return;
            }

            // Step 2: Determine current executable path.
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                DownloadStatus = "Cannot determine current executable path.";
                return;
            }

            // Step 3: Launch the verified binary. The deny-write handle remains open,
            // guaranteeing the file on disk is byte-for-byte what we hashed. Process.Start
            // opens its own read handle (compatible with FileShare.Read), so the launch
            // succeeds without releasing our lock. The OS loads the executable image from
            // this same file — and Windows itself holds the image section object open for
            // the lifetime of the new process, so even after we close our handle on
            // shutdown the binary remains immutable until the applier has fully loaded.
            var pid = Environment.ProcessId;
            var args = UpdateApplier.BuildArguments(currentExe, pid);

            DownloadStatus = "Installing update — SysManager will restart...";

            Process.Start(new ProcessStartInfo
            {
                FileName = DownloadedPath!,
                Arguments = args,
                UseShellExecute = true
            })?.Dispose();

            // Give the applier a moment to start before we exit.
            await Task.Delay(500);
            System.Windows.Application.Current?.Shutdown();
        }
        catch (InvalidOperationException ex)
        {
            DownloadStatus = $"Update failed: {ex.Message}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DownloadStatus = $"Update failed: {ex.Message}";
        }
        finally
        {
            lockedStream.Dispose();
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
        if (!UpdateApplier.TryOpenVerifiedPreviousBuild(_updatesDir, out var verifiedStream, out var why))
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
                    "fixed will come back, and your settings are not affected.",
                    "Go back to the previous version"))
            {
                return;
            }

            var args = UpdateApplier.BuildArguments(currentExe, Environment.ProcessId);
            RollBackStatus = "Going back — SysManager will restart…";

            Process.Start(new ProcessStartInfo
            {
                FileName = previous,
                Arguments = args,
                UseShellExecute = true
            })?.Dispose();

            ActivityLogService.Instance.Log("Update", "Went back to the previously installed version");

            // Let the applier start before this process exits.
            await Task.Delay(500);
            System.Windows.Application.Current?.Shutdown();
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
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{DownloadedPath}\"") { UseShellExecute = true })?.Dispose(); }
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

    private static string BuildStamp()
    {
        try
        {
            // Use AppContext.BaseDirectory instead of Assembly.Location which
            // returns empty string in single-file publish (IL3000).
            var dir = AppContext.BaseDirectory;
            var exe = Path.Join(dir, "SysManager.exe");
            if (File.Exists(exe))
                return File.GetLastWriteTime(exe).ToString("dd MMM yyyy");
            // Fallback: try the DLL
            var dll = Path.Join(dir, "SysManager.dll");
            if (File.Exists(dll))
                return File.GetLastWriteTime(dll).ToString("dd MMM yyyy");
        }
        catch (IOException ex) { Log.Debug(ex, "About: could not read build date from disk"); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "About: access denied reading build date"); }
        return string.Empty;
    }
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
