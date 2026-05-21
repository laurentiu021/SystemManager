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
        await Application.Current.Dispatcher.InvokeAsync(LoadHosts);
    }

    private async Task RefreshDnsAsync()
    {
        try
        {
            string dns = await _dnsService.GetCurrentDnsAsync(_cts.Token).ConfigureAwait(false);
            Application.Current.Dispatcher.Invoke(() => CurrentDns = dns);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read current DNS");
            Application.Current.Dispatcher.Invoke(() => CurrentDns = "Unable to detect");
        }
    }

    private void LoadHosts()
    {
        try
        {
            var entries = _hostsService.ReadHosts();
            HostEntries.ReplaceWith(entries);
            HostsStatus = $"Loaded {entries.Count} entries.";
        }
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

        IsDnsApplying = true;
        StatusMessage = $"Applying {SelectedPreset.Name} DNS...";
        try
        {
            await _dnsService.SetDnsAsync(SelectedPreset.Primary, SelectedPreset.Secondary, _cts.Token)
                .ConfigureAwait(false);

            await RefreshDnsAsync();
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"DNS set to {SelectedPreset.Name} ({SelectedPreset.Primary}, {SelectedPreset.Secondary}).");
            Log.Information("DNS changed to {Preset} ({Primary}, {Secondary})",
                SelectedPreset.Name, SelectedPreset.Primary, SelectedPreset.Secondary);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"Failed to set DNS: {ex.Message}");
            Log.Error(ex, "Failed to apply DNS preset {Preset}", SelectedPreset.Name);
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsDnsApplying = false);
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
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = "DNS reset to automatic (DHCP).");
            Log.Information("DNS reset to DHCP");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"Failed to reset DNS: {ex.Message}");
            Log.Error(ex, "Failed to reset DNS to DHCP");
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsDnsApplying = false);
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

        try
        {
            _hostsService.SaveHosts(HostEntries.ToList());
            HostsStatus = $"Saved {HostEntries.Count} entries. Backup created at hosts.bak.";
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
    private void RefreshHosts() => LoadHosts();

    // ── Cleanup ──────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            // Do not dispose _dnsService or _hostsService — DI container owns their lifetime
        }
        base.Dispose(disposing);
    }
}
