// SysManager · NetworkRepairViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;

namespace SysManager.ViewModels;

/// <summary>DNS flush, Winsock reset, TCP/IP reset.</summary>
public partial class NetworkRepairViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }

    [ObservableProperty] private bool _isRepairing;
    [ObservableProperty] private string _repairStatus = "";
    [ObservableProperty] private bool _repairNeedsReboot;

    public NetworkRepairViewModel(NetworkSharedState shared)
    {
        Shared = shared;
    }

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        var result = MessageBox.Show(
            "Flush the DNS resolver cache?\n\nThis clears cached DNS lookups "
            + "and forces fresh resolution. Safe and instant — no reboot needed.",
            "DNS Flush — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
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
        var result = MessageBox.Show(
            "Reset the Winsock catalog?\n\nThis repairs corrupted network "
            + "socket settings. A reboot is required for changes to take effect.",
            "Winsock Reset — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        await RunRepairAsync(() => Shared.Repair.ResetWinsockAsync());
    }

    [RelayCommand]
    private async Task ResetTcpIpAsync()
    {
        if (!AdminHelper.IsElevated())
        {
            RepairStatus = "⚠ TCP/IP reset requires administrator privileges.";
            return;
        }
        var result = MessageBox.Show(
            "Reset the TCP/IP stack?\n\nThis restores all TCP/IP settings "
            + "to their defaults. A reboot is required for changes to take effect.",
            "TCP/IP Reset — Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        await RunRepairAsync(() => Shared.Repair.ResetTcpIpAsync());
    }

    private async Task RunRepairAsync(
        Func<Task<Models.NetworkRepairResult>> operation)
    {
        IsRepairing = true;
        RepairStatus = "Running…";
        try
        {
            var r = await operation();
            RepairStatus = r.Success
                ? $"✓ {r.ToolName} completed successfully."
                : $"✗ {r.ToolName} failed: {r.Output}";
            if (r.Success)
                Log.Information("Network repair completed: {Tool}", r.ToolName);
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
}
