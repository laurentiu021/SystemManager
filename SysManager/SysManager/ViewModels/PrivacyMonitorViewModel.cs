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
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<PrivacyAccessEntry> Entries { get; } = new();

    [ObservableProperty] private bool _hasEntries;

    public PrivacyMonitorViewModel(PrivacyMonitorService service)
    {
        _service = service;
        StatusMessage = "Reading access history…";
        PropertyChanged += OnVmPropertyChanged;
        // Read off the UI thread so a registry walk (or a corrupt-hive failure) can never
        // block or crash startup — this VM is built eagerly with the main window.
        InitializeAsync(RefreshAsync);
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy)) RefreshCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading access history…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var entries = await _service.ReadAsync(_cts.Token).ConfigureAwait(true);
            Entries.ReplaceWith(entries);
            HasEntries = Entries.Count > 0;
            var inUse = entries.Count(e => e.InUse);
            StatusMessage = entries.Count == 0
                ? "No camera, microphone, or location access has been recorded yet."
                : inUse > 0
                    ? $"{entries.Count} access record(s) — {inUse} device(s) in use right now."
                    : $"{entries.Count} access record(s) across camera, microphone, and location.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally { IsBusy = false; }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
