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

    /// <summary>
    /// The DNS servers in effect immediately before the last SysManager-applied
    /// change, captured so the change can be reverted to the exact previous state.
    /// A failed attempt keeps the prior successful rollback point as a fallback:
    /// the first Undo repairs a possible partial mutation, and a second Undo can
    /// still revert the last successful change.
    /// </summary>
    private sealed record DnsUndoState(
        DnsService.DnsSnapshot Snapshot,
        DnsUndoState? Fallback,
        bool IsAmbiguous = false);

    private DnsUndoState? _dnsUndo;

    [ObservableProperty] private bool _canRestorePreviousDns;

    // ── Hosts section ────────────────────────────────────────────────────

    public BulkObservableCollection<HostsEntry> HostEntries { get; } = new();

    [ObservableProperty] private string _newIp = "";
    [ObservableProperty] private string _newHostname = "";
    [ObservableProperty] private string _hostsStatus = "";

    // ── Elevation ────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isElevated;

    public DnsHostsViewModel(DnsService dnsService, HostsFileService hostsService)
        : this(dnsService, hostsService, autoInit: true) { }

    /// <summary>
    /// Core constructor. <paramref name="autoInit"/> controls whether the startup
    /// load (reads current DNS + parses the hosts file, mutating CurrentDns/HostsStatus
    /// on a background thread) runs. Production always passes true; tests pass false to
    /// exercise the command gates deterministically without racing the async init.
    /// </summary>
    internal DnsHostsViewModel(DnsService dnsService, HostsFileService hostsService, bool autoInit)
    {
        _dnsService = dnsService;
        _hostsService = hostsService;
        Presets = _dnsService.GetPresets();
        IsElevated = AdminHelper.IsElevated();

        if (autoInit)
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
        var preset = SelectedPreset;
        if (preset is null) return;

        if (!IsElevated)
        {
            StatusMessage = "Changing DNS requires administrator privileges.";
            return;
        }

        // DHCP reset path
        if (string.IsNullOrEmpty(preset.Primary))
        {
            await ResetDnsAsync();
            return;
        }

        IsDnsApplying = true;
        StatusMessage = "Reading the active adapter's DNS settings...";
        DnsUndoState? pendingUndo = null;
        try
        {
            // Snapshot BOTH families in effect now so the change is reversible to the exact
            // previous configuration, not just a generic DHCP reset. Capture before consent
            // so the confirmation names the same adapter identity that mutation will verify.
            var snapshot = await _dnsService.CaptureSnapshotAsync(_cts.Token).ConfigureAwait(true);

            var v6Note = preset.HasIpv6 ? " + IPv6" : "";
            if (!DialogService.Instance.Confirm(
                    $"Change DNS on active network interface {snapshot.IfIndex} to {preset.Name} " +
                    $"({preset.Primary}, {preset.Secondary}{v6Note})?\n\n" +
                    $"Previous setting: {DescribeDnsSnapshot(snapshot)}.\n" +
                    "You can restore the previous setting with Undo.",
                    "Confirm DNS Change"))
            {
                StatusMessage = "DNS change cancelled.";
                return;
            }

            var confirmedSnapshot = await _dnsService.CaptureSnapshotAsync(_cts.Token)
                .ConfigureAwait(true);
            if (!DnsSnapshotsMatch(snapshot, confirmedSnapshot))
            {
                await RefreshDnsAsync();
                StatusMessage =
                    "DNS settings changed while confirmation was open. Review and try again.";
                return;
            }

            // Arm Undo before the guarded mutation so an ambiguous partial failure remains
            // recoverable. A typed precondition rejection proves no mutation began and safely
            // removes only this pending entry, exposing any older rollback point again.
            pendingUndo = ArmDnsUndo(confirmedSnapshot);
            StatusMessage = $"Applying {preset.Name} DNS...";

            await _dnsService.SetDnsAsync(confirmedSnapshot, preset.Primary, preset.Secondary,
                    preset.PrimaryV6, preset.SecondaryV6, _cts.Token)
                .ConfigureAwait(false);
            CommitDnsUndo(pendingUndo);
            pendingUndo = null;

            await RefreshDnsAsync();
            SetStatusMessage($"DNS set to {preset.Name} ({preset.Primary}, {preset.Secondary}).");

            Log.Information("DNS changed to {Preset} ({Primary}, {Secondary})",
                preset.Name, preset.Primary, preset.Secondary);
            ActivityLogService.Instance.Log("DNS & Hosts", $"Set DNS to {preset.Name}");
        }
        catch (DnsService.DnsMutationPreconditionException ex)
        {
            DiscardDnsUndo(pendingUndo);
            await RefreshDnsAsync();
            SetStatusMessage("DNS settings or the adapter changed, or their current state could not be verified. Review and try again.");

            Log.Warning(ex, "DNS preset {Preset} was not applied because its captured state could not be verified", preset.Name);
        }
        catch (OperationCanceledException)
        {
            RetainAmbiguousDnsUndo(pendingUndo);
        }
        catch (Exception ex)
        {
            RetainAmbiguousDnsUndo(pendingUndo);
            if (pendingUndo is not null)
                await RefreshDnsAsync();
            SetStatusMessage($"Failed to set DNS: {ex.Message}");

            Log.Error(ex, "Failed to apply DNS preset {Preset}", preset.Name);
        }
        finally
        {
            SetDnsApplying(false);
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
        StatusMessage = "Reading the active adapter's DNS settings...";
        DnsUndoState? pendingUndo = null;
        try
        {
            var snapshot = await _dnsService.CaptureSnapshotAsync(_cts.Token).ConfigureAwait(true);

            if (!DialogService.Instance.Confirm(
                    $"Reset DNS on active network interface {snapshot.IfIndex} to automatic (DHCP)?\n\n" +
                    $"Current setting: {DescribeDnsSnapshot(snapshot)}.\n" +
                    "The current setting can be restored with Undo.",
                    "Confirm DNS Reset"))
            {
                StatusMessage = "DNS reset cancelled.";
                return;
            }

            var confirmedSnapshot = await _dnsService.CaptureSnapshotAsync(_cts.Token)
                .ConfigureAwait(true);
            if (!DnsSnapshotsMatch(snapshot, confirmedSnapshot))
            {
                await RefreshDnsAsync();
                StatusMessage =
                    "DNS settings changed while confirmation was open. Review and try again.";
                return;
            }

            pendingUndo = ArmDnsUndo(confirmedSnapshot);
            StatusMessage = "Resetting DNS to DHCP...";

            await _dnsService.ResetToDhcpAsync(confirmedSnapshot, _cts.Token).ConfigureAwait(false);
            CommitDnsUndo(pendingUndo);
            pendingUndo = null;
            await RefreshDnsAsync();
            SetStatusMessage("DNS reset to automatic (DHCP).");

            Log.Information("DNS reset to DHCP");
        }
        catch (DnsService.DnsMutationPreconditionException ex)
        {
            DiscardDnsUndo(pendingUndo);
            await RefreshDnsAsync();
            SetStatusMessage("DNS settings or the adapter changed, or their current state could not be verified. Review and try again.");

            Log.Warning(ex, "DNS reset was not applied because its captured state could not be verified");
        }
        catch (OperationCanceledException)
        {
            RetainAmbiguousDnsUndo(pendingUndo);
        }
        catch (Exception ex)
        {
            RetainAmbiguousDnsUndo(pendingUndo);
            if (pendingUndo is not null)
                await RefreshDnsAsync();
            SetStatusMessage($"Failed to reset DNS: {ex.Message}");

            Log.Error(ex, "Failed to reset DNS to DHCP");
        }
        finally
        {
            SetDnsApplying(false);
        }
    }
    [RelayCommand]
    private async Task RestorePreviousDnsAsync()
    {
        if (!IsElevated)
        {
            StatusMessage = "Restoring DNS requires administrator privileges.";
            return;
        }

        var undo = _dnsUndo;
        if (undo is null)
        {
            StatusMessage = "No previous DNS to restore.";
            return;
        }

        var previousServers = undo.Snapshot;
        var label = DescribeDnsSnapshot(previousServers);

        if (!DialogService.Instance.Confirm(
                "Restore DNS on the previously changed network adapter " +
                $"to its previous setting ({label})?",
                "Confirm DNS Restore"))
        {
            StatusMessage = "DNS restore cancelled.";
            return;
        }

        IsDnsApplying = true;
        StatusMessage = "Restoring previous DNS...";
        try
        {
            await _dnsService.RestoreSnapshotAsync(previousServers, _cts.Token).ConfigureAwait(false);

            CompleteDnsUndo(undo);
            SetStatusMessage($"DNS restored to previous setting ({label}).");

            await RefreshDnsAsync();
            Log.Information("DNS restored to previous setting ({Label})", label);
        }
        catch (OperationCanceledException) { /* expected when the view is closed mid-operation */ }
        catch (Exception ex)
        {
            await RefreshDnsAsync();
            SetStatusMessage($"Failed to restore DNS: {ex.Message}");

            Log.Error(ex, "Failed to restore previous DNS");
        }
        finally
        {
            SetDnsApplying(false);
        }
    }

    private DnsUndoState ArmDnsUndo(DnsService.DnsSnapshot snapshot)
    {
        var pending = new DnsUndoState(snapshot, _dnsUndo);
        _dnsUndo = pending;
        UpdateCanRestorePreviousDns();
        return pending;
    }

    private static string DescribeDnsSnapshot(DnsService.DnsSnapshot snapshot)
    {
        static string DescribeFamily(
            string family,
            IReadOnlyList<string> addresses,
            DnsService.DnsConfigurationSource source) =>
            source switch
            {
                DnsService.DnsConfigurationSource.Automatic =>
                    $"{family} automatic (DHCP)",
                DnsService.DnsConfigurationSource.Static when addresses.Count > 0 =>
                    $"{family} static: {string.Join(", ", addresses)}",
                _ => $"{family} unavailable",
            };

        return $"{DescribeFamily("IPv4", snapshot.V4, snapshot.V4Source)}; " +
               DescribeFamily("IPv6", snapshot.V6, snapshot.V6Source);
    }

    private static bool DnsSnapshotsMatch(
        DnsService.DnsSnapshot expected,
        DnsService.DnsSnapshot actual) =>
        expected.IfIndex == actual.IfIndex &&
        expected.InterfaceGuid == actual.InterfaceGuid &&
        expected.V4Source == actual.V4Source &&
        expected.V6Source == actual.V6Source &&
        expected.V4.SequenceEqual(actual.V4, StringComparer.OrdinalIgnoreCase) &&
        expected.V6.SequenceEqual(actual.V6, StringComparer.OrdinalIgnoreCase);

    private static bool DnsSnapshotsReferToSameAdapter(
        DnsService.DnsSnapshot left,
        DnsService.DnsSnapshot right) =>
        left.InterfaceGuid is { } capturedGuid &&
        capturedGuid != Guid.Empty && right.InterfaceGuid == capturedGuid;

    private void CommitDnsUndo(DnsUndoState pending)
    {
        if (!ReferenceEquals(_dnsUndo, pending))
            return;

        var restoreSnapshot = pending.Snapshot;
        var unresolved = new List<DnsUndoState>();
        for (var candidate = pending.Fallback;
             candidate is not null;
             candidate = candidate.Fallback)
        {
            if (!candidate.IsAmbiguous)
                continue;

            if (DnsSnapshotsReferToSameAdapter(pending.Snapshot, candidate.Snapshot))
            {
                restoreSnapshot = candidate.Snapshot;
                continue;
            }

            unresolved.Add(candidate);
        }

        DnsUndoState? fallback = null;
        for (var i = unresolved.Count - 1; i >= 0; i--)
            fallback = unresolved[i] with { Fallback = fallback };

        _dnsUndo = new DnsUndoState(restoreSnapshot, fallback);
    }

    private void RetainAmbiguousDnsUndo(DnsUndoState? pending)
    {
        if (pending is not null && ReferenceEquals(_dnsUndo, pending))
            _dnsUndo = pending with { IsAmbiguous = true };
    }

    private void DiscardDnsUndo(DnsUndoState? pending)
    {
        if (pending is not null && ReferenceEquals(_dnsUndo, pending))
            _dnsUndo = pending.Fallback;

        UpdateCanRestorePreviousDns();
    }

    private void CompleteDnsUndo(DnsUndoState restored)
    {
        if (ReferenceEquals(_dnsUndo, restored))
            _dnsUndo = restored.Fallback;

        UpdateCanRestorePreviousDns();
    }

    private void UpdateCanRestorePreviousDns()
    {
        void Update() => CanRestorePreviousDns = _dnsUndo is not null;

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(Update);
        else
            Update();
    }

    private void SetStatusMessage(string value)
    {
        void Update() => StatusMessage = value;

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(Update);
        else
            Update();
    }
    private void SetDnsApplying(bool value)
    {
        void Update() => IsDnsApplying = value;

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(Update);
        else
            Update();
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
    private async Task SaveHostsAsync()
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

        // Snapshot the entries on the UI thread, then write off-thread: SaveHosts does
        // synchronous file I/O (WriteAllLines + File.Replace on the System32 hosts file)
        // that would otherwise block the UI until the disk write completes.
        var snapshot = HostEntries.ToList();
        try
        {
            await Task.Run(() => _hostsService.SaveHosts(snapshot)).ConfigureAwait(true);
            HostsStatus = $"Saved {snapshot.Count} entries. Original preserved at hosts.bak.";
            Log.Information("Hosts file saved with {Count} entries", snapshot.Count);
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
            // RestoreBackup copies the .bak over the System32 hosts file synchronously;
            // run it off the UI thread so the window stays responsive during the copy.
            bool restored = await Task.Run(_hostsService.RestoreBackup).ConfigureAwait(true);
            if (restored)
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
            App.RequestShutdown();
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
