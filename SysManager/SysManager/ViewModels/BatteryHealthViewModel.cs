// SysManager · BatteryHealthViewModel — battery health tab
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Management;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Battery Health tab — shows charge, health %, wear, cycles, runtime.
/// </summary>
public partial class BatteryHealthViewModel : ViewModelBase
{
    private readonly BatteryService _service = new();

    [ObservableProperty] private BatteryInfo _battery = new();
    [ObservableProperty] private string _summary = "Click Refresh to read battery data.";

    public BatteryHealthViewModel()
    {
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Log.Warning("Battery auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading battery info…";

        try
        {
            Battery = await _service.GetBatteryInfoAsync();

            Summary = Battery.HasBattery
                ? $"{Battery.Name} · {Battery.ChargePercent}% · Health {Battery.HealthPercent}% · {Battery.Status}"
                : "No battery detected — this device runs on AC power only.";

            StatusMessage = Battery.HasBattery
                ? "Battery data loaded."
                : "No battery found.";
            Log.Information("Battery scan completed: {HasBattery}", Battery.HasBattery);
        }
        catch (System.Management.ManagementException ex)
        {
            StatusMessage = $"WMI error: {ex.Message}";
            Summary = "Could not read battery information.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Failed: {ex.Message}";
            Summary = "Could not read battery information.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
