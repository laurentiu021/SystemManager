// SysManager · PrivacyMonitorViewModel — webcam/mic/location access history
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Privacy Monitor tab. Shows which apps recently used the camera,
/// microphone, or location, read from the Windows consent store. Read-only history — the
/// "Open privacy settings" action hands off to Windows for granting/revoking permissions,
/// which SysManager never changes itself.
/// </summary>
public sealed partial class PrivacyMonitorViewModel : ViewModelBase
{
    private readonly PrivacyMonitorService _service;

    public BulkObservableCollection<PrivacyAccessEntry> Entries { get; } = new();

    [ObservableProperty] private bool _hasEntries;

    public PrivacyMonitorViewModel(PrivacyMonitorService service)
    {
        _service = service;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var entries = _service.Read();
        Entries.ReplaceWith(entries);
        HasEntries = Entries.Count > 0;
        var inUse = entries.Count(e => e.InUse);
        StatusMessage = entries.Count == 0
            ? "No camera, microphone, or location access has been recorded yet."
            : inUse > 0
                ? $"{entries.Count} access record(s) — {inUse} device(s) in use right now."
                : $"{entries.Count} access record(s) across camera, microphone, and location.";
    }

    [RelayCommand]
    private void OpenPrivacySettings()
    {
        // Hand off to Windows for actually granting/revoking — SysManager never changes
        // capability permissions itself.
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy") { UseShellExecute = true })?.Dispose();
            StatusMessage = "Opened Windows privacy settings.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Could not open privacy settings: {Error}", ex.Message);
            StatusMessage = "Couldn't open Windows privacy settings.";
        }
    }
}
