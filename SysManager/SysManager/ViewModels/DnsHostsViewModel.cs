// SysManager · DnsHostsViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Combined ViewModel for DNS preset switching and hosts file editing.
/// Both operations require administrator privileges.
/// </summary>
public sealed partial class DnsHostsViewModel : ViewModelBase
{
    private readonly DnsService _dnsService;
    private readonly HostsFileService _hostsService;
    private readonly CancellationTokenSource _cts = new();

    // ── DNS section ──────────────────────────────────────────────────────

    public List<DnsPreset> Presets { get; }

    [ObservableProperty] private DnsPreset? _selectedPreset;
    [ObservableProperty] private string _currentDns = "Loading...";
    [ObservableProperty] private bool _isDnsApplying;

    // ── Hosts section ────────────────────────────────────────────────────

    public BulkObservableCollection<HostsEntry> HostEntries { get; } = new();

    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string _newHostname = "";
    [ObservableProperty] private string _hostsStatus = "";

    // ── Elevation ────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isElevated;

    public DnsHostsViewModel(DnsService dnsService, HostsFileService hostsService)
    {
        _dnsService = dnsService;
        _hostsService = hostsService;
        Presets = _dnsService.GetPresets();
        IsElevated = AdminHelper.IsElevated();

        InitializeAsync(LoadInitialDataAsync);
    }

    private async Task LoadInitialDataAsync()
    {
        await RefreshDnsAsync();
        await LoadHostsAsync();
    }

    private async Task RefreshDnsAsync()
    {
        try
        {
            string dns = await _dnsService.GetCurrentDnsAsync(_cts.Token).ConfigureAwait(false);
            if (Application.Current?.Dispatcher is { } dispatcher)
                dispatcher.Invoke(() => CurrentDns = dns);
            else
                CurrentDns = dns;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read current DNS");
            if (Application.Current?.Dispatcher is { } d)
                d.Invoke(() => CurrentDns = "Unable to detect");
            else
                CurrentDns = "Unable to detect";
        }
    }

    private async Task LoadHostsAsync()
    {
        try
        {
            var entries = await _hostsService.ReadHostsAsync(_cts.Token).ConfigureAwait(true);
            HostEntries.ReplaceWith(entries);
            HostsStatus = $"Loaded {entries.Count} entries.";
        }
        catch (OperationCanceledException) { /* expected on view teardown — nothing to report */ }
        catch (UnauthorizedAccessException)
        {
            HostsStatus = "Access denied — run as administrator to read hosts file.";
        }
        catch (IOException ex)
        {
            HostsStatus = $"Error reading hosts file: {ex.Message}";
            Log.Warning(ex, "Failed to read hosts file");
        }
    }

    // ── DNS Commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        if (SelectedPreset is null) return;

        if (!IsElevated)
        {
            StatusMessage = "Changing DNS requires administrator privileges.";
            return;
        }

        // DHCP reset path
        if (string.IsNullOrEmpty(SelectedPreset.Primary))
        {
            await ResetDnsAsync();
            return;
        }

        if (!DialogService.Instance.Confirm(
                $"Change this PC's DNS servers to {SelectedPreset.Name} " +
                $"({SelectedPreset.Primary}, {SelectedPreset.Secondary})?\n\n" +
                "You can revert any time with \"Reset to automatic (DHCP)\".",
                "Confirm DNS Change"))
        {
            StatusMessage = "DNS change cancelled.";
            return;
        }

        IsDnsApplying = true;
        StatusMessage = $"Applying {SelectedPreset.Name} DNS...";
        try
        {
            await _dnsService.SetDnsAsync(SelectedPreset.Primary, SelectedPreset.Secondary, _cts.Token)
                .ConfigureAwait(false);

            await RefreshDnsAsync();
            Application.Current?.Dispatcher?.Invoke(() =>
                StatusMessage = $"DNS set to {SelectedPreset.Name} ({SelectedPreset.Primary}, {SelectedPreset.Secondary}).");
            Log.Information("DNS changed to {Preset} ({Primary}, {Secondary})",
                SelectedPreset.Name, SelectedPreset.Primary, SelectedPreset.Secondary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
                StatusMessage = $"Failed to set DNS: {ex.Message}");
            Log.Error(ex, "Failed to apply DNS preset {Preset}", SelectedPreset.Name);
        }
        finally
        {
            Application.Current?.Dispatcher?.Invoke(() => IsDnsApplying = false);
        }
    }

    [RelayCommand]
    private async Task ResetDnsAsync()
    {
        if (!IsElevated)
        {
            StatusMessage = "Resetting DNS requires administrator privileges.";
            return;
        }

        IsDnsApplying = true;
        StatusMessage = "Resetting DNS to DHCP...";
        try
        {
            await _dnsService.ResetToDhcpAsync(_cts.Token).ConfigureAwait(false);
            await RefreshDnsAsync();
            Application.Current?.Dispatcher?.Invoke(() =>
                StatusMessage = "DNS reset to automatic (DHCP).");
            Log.Information("DNS reset to DHCP");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
                StatusMessage = $"Failed to reset DNS: {ex.Message}");
            Log.Error(ex, "Failed to reset DNS to DHCP");
        }
        finally
        {
            Application.Current?.Dispatcher?.Invoke(() => IsDnsApplying = false);
        }
    }

    // ── Hosts Commands ───────────────────────────────────────────────────

    [RelayCommand]
    private void AddEntry()
    {
        try
        {
            var entry = _hostsService.AddEntry(NewIp, NewHostname);
            HostEntries.Add(entry);
            NewIp = "";
            NewHostname = "";
            HostsStatus = $"Added {entry.Hostname} ({entry.IpAddress}).";
        }
        catch (ArgumentException ex)
        {
            HostsStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveEntry(HostsEntry? entry)
    {
        if (entry is null) return;
        HostEntries.Remove(entry);
        HostsStatus = $"Removed {entry.Hostname}.";
    }

    [RelayCommand]
    private void SaveHosts()
    {
        if (!IsElevated)
        {
            HostsStatus = "Saving hosts file requires administrator privileges.";
            return;
        }

        if (!DialogService.Instance.Confirm(
                $"Overwrite the system hosts file with these {HostEntries.Count} entries?\n\n" +
                "The original hosts file is preserved as hosts.bak (only the first time) " +
                "and can be restored with \"Restore original\".",
                "Confirm Hosts File Change"))
        {
            HostsStatus = "Save cancelled.";
            return;
        }

        try
        {
            _hostsService.SaveHosts(HostEntries.ToList());
            HostsStatus = $"Saved {HostEntries.Count} entries. Original preserved at hosts.bak.";
            Log.Information("Hosts file saved with {Count} entries", HostEntries.Count);
        }
        catch (UnauthorizedAccessException)
        {
            HostsStatus = "Access denied — run as administrator to save hosts file.";
        }
        catch (IOException ex)
        {
            HostsStatus = $"Error saving hosts file: {ex.Message}";
            Log.Error(ex, "Failed to save hosts file");
        }
    }

    [RelayCommand]
    private async Task RestoreHostsAsync()
    {
        if (!IsElevated)
        {
            HostsStatus = "Restoring the hosts file requires administrator privileges.";
            return;
        }

        if (!_hostsService.HasBackup)
        {
            HostsStatus = "No backup found — nothing to restore.";
            return;
        }

        if (!DialogService.Instance.Confirm(
                "Restore the original hosts file from backup? Your current SysManager " +
                "changes to the hosts file will be discarded.",
                "Confirm Restore Hosts File"))
        {
            HostsStatus = "Restore cancelled.";
            return;
        }

        try
        {
            if (_hostsService.RestoreBackup())
            {
                await LoadHostsAsync();
                HostsStatus = "Original hosts file restored from backup.";
                Log.Information("Hosts file restored from backup");
            }
            else
            {
                HostsStatus = "No backup found — nothing to restore.";
            }
        }
        catch (UnauthorizedAccessException)
        {
            HostsStatus = "Access denied — run as administrator to restore hosts file.";
        }
        catch (IOException ex)
        {
            HostsStatus = $"Error restoring hosts file: {ex.Message}";
            Log.Error(ex, "Failed to restore hosts file");
        }
    }

    [RelayCommand]
    private Task RefreshHostsAsync() => LoadHostsAsync();

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    // ── Cleanup ──────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
