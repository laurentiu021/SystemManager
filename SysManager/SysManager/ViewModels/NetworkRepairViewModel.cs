// SysManager · NetworkRepairViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>DNS flush, Winsock reset, TCP/IP reset.</summary>
public sealed partial class NetworkRepairViewModel : ElevatedViewModelBase
{
    public NetworkSharedState Shared { get; }

    private readonly ISessionRestorePoint _restorePoint;

    [ObservableProperty] private bool _isRepairing;
    [ObservableProperty] private string _repairStatus = "";
    [ObservableProperty] private bool _repairNeedsReboot;

    /// <summary>Builds the tab over the network services its sibling tabs share.</summary>
    /// <param name="shared">The network services the network tabs share.</param>
    /// <param name="restorePoint">
    /// The session's restore point, taken before the first Winsock or TCP/IP reset, as each tab that changes Windows
    /// takes it before its first change.
    /// </param>
    public NetworkRepairViewModel(NetworkSharedState shared, ISessionRestorePoint restorePoint)
    {
        Shared = shared;
        _restorePoint = restorePoint;
    }


    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        if (!DialogService.Instance.Confirm(
            "Flush the DNS resolver cache?\n\nThis clears cached DNS lookups "
            + "and forces fresh resolution. Safe and instant — no reboot needed.",
            "DNS Flush — Confirm")) return;
        await RunRepairAsync(() => Shared.Repair.FlushDnsAsync());
    }

    [RelayCommand]
    private async Task ResetWinsockAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            RepairStatus = "⚠ Winsock reset requires administrator privileges.";
            return;
        }
        if (!DialogService.Instance.Confirm(
            "Reset the Winsock catalog?\n\nThis repairs corrupted network "
            + "socket settings. A reboot is required for changes to take effect."
            + _restorePoint.ConfirmationNotice,
            "Winsock Reset — Confirm")) return;
        await RunRepairAsync(() => Shared.Repair.ResetWinsockAsync(), takesRestorePoint: true);
    }

    [RelayCommand]
    private async Task ResetTcpIpAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            RepairStatus = "⚠ TCP/IP reset requires administrator privileges.";
            return;
        }
        // What the card says the reset removes, said where the reset is confirmed too: the prompt said only that the
        // settings go back to their defaults, which a fixed address does not sound like (#2843).
        if (!DialogService.Instance.Confirm(
            "Reset the TCP/IP stack?\n\nThis puts every TCP/IP setting back to Windows' defaults. If you set "
            + "a fixed IP address, added routes or changed adapter settings yourself, they are removed, and you "
            + "set them again after the restart. A reboot is required for changes to take effect."
            + _restorePoint.ConfirmationNotice,
            "TCP/IP Reset — Confirm")) return;
        await RunRepairAsync(() => Shared.Repair.ResetTcpIpAsync(), takesRestorePoint: true);
    }

    private async Task RunRepairAsync(
        Func<Task<Models.NetworkRepairResult>> operation, bool takesRestorePoint = false)
    {
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Network Repair");
        if (opLock is null)
        {
            RepairStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }

        // A reset changes Windows' settings, and the restore point before it snapshots the system drive, so neither may
        // overlap a repair, a feature change or an update install SysManager is running, which hold this lock.
        using var systemLock = takesRestorePoint
            ? OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Network Repair")
            : null;
        if (takesRestorePoint && systemLock is null)
        {
            var running = OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification);
            RepairStatus = $"Cannot start — {running} is already running.";
            return;
        }

        IsRepairing = true;
        RepairStatus = "Running…";
        try
        {
            // Before the reset, never after: a point taken afterwards would record the settings the reset removed.
            // The two resets were the changes to Windows that took none, and TCP/IP's has no way back in SysManager
            // (#2843).
            var snapshotTaken = takesRestorePoint
                && await _restorePoint.EnsureAsync("SysManager Network Repair").ConfigureAwait(true);
            var r = await operation();
            var rp = snapshotTaken ? " Restore point created." : "";
            RepairStatus = r.Success
                ? $"✓ {r.ToolName} completed successfully.{rp}"
                : $"✗ {r.ToolName} failed: {r.Output}";
            if (r.Success)
            {
                ToastService.Instance.Show("Network repair complete", $"{r.ToolName} completed successfully");
                Log.Information("Network repair completed: {Tool}", r.ToolName);
            }
            else
                Log.Warning("Network repair failed: {Tool}", r.ToolName);
            if (r.NeedsReboot && r.Success)
            {
                RepairNeedsReboot = true;
                RepairStatus += " Reboot required.";
            }
        }
        catch (OperationCanceledException) { RepairStatus = "Cancelled."; }
        catch (System.ComponentModel.Win32Exception ex)
        { RepairStatus = $"✗ Error: {ex.Message}"; }
        catch (InvalidOperationException ex)
        { RepairStatus = $"✗ Error: {ex.Message}"; }
        finally { IsRepairing = false; }
    }

    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsRepairingChanged(bool value) => IsBusy = value;
}
