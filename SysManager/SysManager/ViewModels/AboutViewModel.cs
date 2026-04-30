// SysManager · AboutViewModel — version info + update check + install
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
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

public partial class AboutViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private UpdateService.ReleaseInfo? _latest;

    public ObservableCollection<ReleaseNote> ReleaseHistory { get; } = new();

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
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private int _downloadPercent;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private string? _downloadedPath;
    [ObservableProperty] private bool _autoDownloadFailed;

    public AboutViewModel() : this(new UpdateService()) { }

    public AboutViewModel(UpdateService updates)
    {
        _updates = updates;
        _ = InitAsync();
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
            await Task.Delay(1000);     // let the UI settle
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
            if (latest == null)
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
                UpdateStatus = $"Update available: {LatestVersionLabel} ({LatestPublishedLabel}).";

                // Auto-download when a new version is found — falls back
                // gracefully if blocked.
                if (!IsDownloading && DownloadedPath == null)
                    _ = DownloadAsync();
            }
            else
            {
                UpdateAvailable = false;
                UpdateStatus = $"You're up to date. Running v{UpdateService.CurrentVersion.ToString(3)}.";
            }
        }
        finally { IsCheckingForUpdates = false; }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var list = await _updates.GetRecentAsync(10);
            ReleaseHistory.Clear();
            foreach (var r in list)
            {
                ReleaseHistory.Add(new ReleaseNote
                {
                    Version = $"v{r.Version.ToString(3)}",
                    Title = r.Name,
                    PublishedAt = r.PublishedAt == DateTimeOffset.MinValue ? "" : r.PublishedAt.LocalDateTime.ToString("dd MMM yyyy"),
                    Body = r.Body,
                    Url = r.HtmlUrl,
                    IsCurrent = r.Version == UpdateService.CurrentVersion
                });
            }
        }
        catch (HttpRequestException ex) { Log.Debug("Release history load skipped (network): {Error}", ex.Message); }
        catch (TaskCanceledException ex) { Log.Debug("Release history load timed out: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_latest == null || IsDownloading) return;
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
            if (path != null && File.Exists(path))
            {
                DownloadedPath = path;
                DownloadStatus = "Download complete. Click Install to restart with the new version.";
                DownloadPercent = 100;
            }
            else
            {
                AutoDownloadFailed = true;
                DownloadStatus = "Automatic download failed — use Manual download to get it from GitHub.";
            }
        }
        catch (HttpRequestException ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Automatic download failed: {ex.Message}";
        }
        catch (IOException ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Automatic download failed: {ex.Message}";
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
    private void OpenLicense() => OpenUrl($"https://github.com/{UpdateService.Owner}/{UpdateService.Repo}/blob/main/LICENSE");

    /// <summary>
    /// Copy a bug-report-ready block with SysManager version, Windows version,
    /// architecture, .NET runtime, elevation state, and hardware diagnostics
    /// (CPU, RAM, GPU, storage, display) to the clipboard.
    /// Fully defensive — falls back gracefully on any WMI / registry miss.
    /// </summary>
    [RelayCommand]
    private void CopyEnvironmentInfo()
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
                foreach (System.Management.ManagementObject mo in cpuSearch.Get())
                {
                    var name = mo["Name"]?.ToString()?.Trim() ?? "unknown";
                    var cores = mo["NumberOfCores"];
                    var threads = mo["NumberOfLogicalProcessors"];
                    var mhz = mo["MaxClockSpeed"];
                    sb.Append("CPU: ").Append(name);
                    if (cores != null) sb.Append($" ({cores}c/{threads}t)");
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
                foreach (System.Management.ManagementObject mo in memSearch.Get())
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
                foreach (System.Management.ManagementObject mo in gpuSearch.Get())
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
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    sb.AppendLine($"Disk {drive.Name.TrimEnd('\\')} {drive.TotalSize / 1024.0 / 1024.0 / 1024.0:F0} GB total, {drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:F0} GB free ({drive.DriveFormat})");
                }
            }
            catch (IOException ex) { Log.Debug("Storage info unavailable: {Error}", ex.Message); }
            catch (UnauthorizedAccessException ex) { Log.Debug("Storage info access denied: {Error}", ex.Message); }

            // Display
            try
            {
                using var dispSearch = new System.Management.ManagementObjectSearcher(
                    "SELECT CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate FROM Win32_VideoController");
                foreach (System.Management.ManagementObject mo in dispSearch.Get())
                {
                    var w = mo["CurrentHorizontalResolution"];
                    var h = mo["CurrentVerticalResolution"];
                    var hz = mo["CurrentRefreshRate"];
                    if (w != null && h != null)
                    {
                        sb.Append($"Display: {w}×{h}");
                        if (hz != null) sb.Append($" @ {hz} Hz");
                        sb.AppendLine();
                        break;
                    }
                }
            }
            catch (System.Management.ManagementException ex) { Log.Debug("Display info unavailable: {Error}", ex.Message); }

            var text = sb.ToString();
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException ex) { Log.Debug("Clipboard locked: {Error}", ex.Message); }
            UpdateStatus = "Environment info copied to clipboard.";
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

    private static string DescribeWindows()
    {
        try
        {
            // WMI Caption gives a friendly name like "Microsoft Windows 11 Pro"
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Caption,BuildNumber FROM Win32_OperatingSystem");
            foreach (System.Management.ManagementObject mo in searcher.Get())
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
    private void InstallUpdate()
    {
        if (string.IsNullOrWhiteSpace(DownloadedPath) || !File.Exists(DownloadedPath))
        {
            DownloadStatus = "No downloaded file to install.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DownloadedPath,
                UseShellExecute = true  // lets UAC prompt if needed
            });
            // Close the current instance so the new one takes over.
            System.Windows.Application.Current?.Shutdown();
        }
        catch (InvalidOperationException ex)
        {
            DownloadStatus = $"Couldn't launch installer: {ex.Message}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DownloadStatus = $"Couldn't launch installer: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        if (string.IsNullOrWhiteSpace(DownloadedPath)) return;
        var dir = Path.GetDirectoryName(DownloadedPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{DownloadedPath}\"") { UseShellExecute = true }); }
            catch (InvalidOperationException) { /* explorer launch is best-effort */ }
            catch (System.ComponentModel.Win32Exception) { /* explorer launch is best-effort */ }
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (InvalidOperationException) { /* best-effort */ }
        catch (System.ComponentModel.Win32Exception) { /* best-effort */ }
    }

    private static string BuildStamp()
    {
        try
        {
            var path = typeof(AboutViewModel).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return File.GetLastWriteTime(path).ToString("dd MMM yyyy");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
