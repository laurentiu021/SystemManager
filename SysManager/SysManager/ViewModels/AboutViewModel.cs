// SysManager · AboutViewModel — version info + update check + install
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        // Fire-and-forget auto check on app start.
        _ = CheckAtStartupAsync();
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
        catch { /* silent — manual check still works */ }
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
        catch { /* non-fatal */ }
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
        catch (Exception ex)
        {
            AutoDownloadFailed = true;
            DownloadStatus = $"Automatic download failed: {ex.Message}";
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
    /// architecture, .NET runtime, and elevation state to the clipboard.
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

            var text = sb.ToString();
            try { Clipboard.SetText(text); } catch { /* clipboard can be locked by another app */ }
            UpdateStatus = "Environment info copied to clipboard.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Couldn't collect environment info: {ex.Message}";
        }
    }

    private static string DescribeWindows()
    {
        try
        {
            var os = Environment.OSVersion;
            // Build number tells the actual Windows version better than Major.Minor on 10/11.
            return $"{os.VersionString} (build {os.Version.Build})";
        }
        catch { return "unknown"; }
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
        catch (Exception ex)
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
            catch (Exception) { /* explorer launch is best-effort */ }
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { /* best-effort */ }
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
