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
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly SystemReportService _reportService;
    private UpdateService.ReleaseInfo? _latest;

    [ObservableProperty] private IReadOnlyList<ReleaseNote> _releaseHistory = [];

    [ObservableProperty] private string _currentVersion = UpdateService.CurrentVersion.ToString(3);
    [ObservableProperty] private string _buildDate = BuildStamp();

    // Update check state
    [ObservableProperty] private string _updateStatus = "Ready.";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _latestVersionLabel = string.Empty;
    [ObservableProperty] private string _latestPublishedLabel = string.Empty;
    [ObservableProperty] private string _latestNotes = string.Empty;

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

    // Report export state
    [ObservableProperty] private bool _isGeneratingReport;

    public AboutViewModel() : this(new UpdateService(), new SystemReportService(new SystemInfoService(), new DiskHealthService())) { }

    public AboutViewModel(UpdateService updates, SystemReportService reportService)
        : this(updates, reportService, autoCheck: true) { }

    /// <summary>
    /// Core constructor. <paramref name="autoCheck"/> controls whether the
    /// startup update-check (a live network call that populates the update
    /// properties) runs. Production always passes true; tests pass false to
    /// assert the constructor's default state without racing the async fetch.
    /// </summary>
    internal AboutViewModel(UpdateService updates, SystemReportService reportService, bool autoCheck)
    {
        _updates = updates;
        _reportService = reportService;
        if (autoCheck)
            InitializeAsync(InitAsync);
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
        try
        {
            await Task.Yield();     // let the UI settle
            await CheckForUpdatesAsync();
            await LoadHistoryAsync();
        }
        catch (HttpRequestException ex) { Log.Debug("About startup check skipped (network): {Error}", ex.Message); }
        catch (TaskCanceledException ex) { Log.Debug("About startup check timed out: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateStatus = "Contacting GitHub...";
        try
        {
            var latest = await _updates.GetLatestAsync();
            if (latest is null)
            {
                var detail = string.IsNullOrWhiteSpace(_updates.LastError) ? "Unknown error." : _updates.LastError;
                UpdateStatus = $"Couldn't reach GitHub — {detail} Click Retry to try again.";
                UpdateAvailable = false;
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
        }
        catch (TaskCanceledException ex)
        {
            UpdateStatus = $"Request timed out: {ex.Message}. Click Retry to try again.";
            UpdateAvailable = false;
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
                UpdateStatus = "Environment info copied to clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log.Debug("Clipboard locked: {Error}", ex.Message);
                UpdateStatus = "Couldn't copy to clipboard: it's currently in use by another application.";
            }
        }
        catch (System.Management.ManagementException ex)
        {
            UpdateStatus = $"Couldn't collect environment info: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            UpdateStatus = $"Couldn't collect environment info: {ex.Message}";
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
                    "SELECT Name,DriverVersion,AdapterRAM FROM Win32_VideoController");
                using var gpuResults = gpuSearch.Get();
                foreach (System.Management.ManagementObject mo in gpuResults)
                    using (mo)
                    {
                        var name = mo["Name"]?.ToString()?.Trim() ?? "unknown";
                        var driver = mo["DriverVersion"]?.ToString() ?? "";
                        var vram = mo["AdapterRAM"] as uint? ?? 0;
                        sb.Append("GPU: ").Append(name);
                        if (vram > 0) sb.Append($" ({vram / 1024.0 / 1024.0 / 1024.0:F1} GB VRAM)");
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
        IsGeneratingReport = true;
        UpdateStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var fileName = $"SysManager-Report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
            var filePath = Path.Join(desktop, fileName);
            await File.WriteAllTextAsync(filePath, report, Encoding.UTF8);
            UpdateStatus = $"Report saved to Desktop: {fileName}";
        }
        catch (IOException ex)
        {
            UpdateStatus = $"Failed to save report: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            UpdateStatus = $"Failed to save report (access denied): {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            UpdateStatus = $"Failed to generate report: {ex.Message}";
        }
        finally { IsGeneratingReport = false; }
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (IsGeneratingReport) return;
        IsGeneratingReport = true;
        UpdateStatus = "Generating system report...";
        try
        {
            var report = await _reportService.GenerateReportAsync();
            try
            {
                Clipboard.SetText(report);
                UpdateStatus = "Full system report copied to clipboard.";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Log.Debug("Clipboard locked: {Error}", ex.Message);
                UpdateStatus = "Couldn't copy to clipboard: it's currently in use by another application.";
            }
        }
        catch (InvalidOperationException ex)
        {
            UpdateStatus = $"Failed to generate report: {ex.Message}";
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

        // Step 1: Verify SHA256 hash before installing.
        DownloadStatus = "Verifying file integrity...";
        var (verified, expected, actual) = await _updates.VerifyHashAsync(_latest, DownloadedPath);
        if (!verified)
        {
            DownloadStatus = expected is not null && actual is not null
                ? $"SHA256 mismatch — file may be corrupted. Expected: {expected[..12]}… Got: {actual[..12]}…"
                : "Hash verification failed — file may be corrupted. Try downloading again.";
            return;
        }

        // Step 1b: Verify Authenticode signature (detects tampered binaries).
        if (!UpdateService.VerifyAuthenticode(DownloadedPath))
        {
            DownloadStatus = "Update binary has an invalid digital signature — possible tampering. Download aborted.";
            return;
        }

        // Step 2: Determine current executable path.
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            DownloadStatus = "Cannot determine current executable path.";
            return;
        }

        // Step 3: Hand off to the in-process applier. Instead of writing a batch
        // script to disk and running it via cmd.exe — which left a writable file a
        // same-user process could tamper with between write and execution (updater
        // TOCTOU → local elevation-of-privilege) — we launch the freshly-downloaded,
        // already-verified executable itself with an "apply-update" argument. That
        // new process waits for this one to exit, swaps itself over the old exe with
        // an atomic move, and relaunches. There is no script for anything to tamper
        // with, and the new process inherits this one's elevation, so a run-as-admin
        // session stays elevated across the update.
        try
        {
            var pid = Environment.ProcessId;
            // DownloadedPath is non-null here: validated by File.Exists above.
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
