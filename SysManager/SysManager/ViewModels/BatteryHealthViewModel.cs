// SysManager · BatteryHealthViewModel — battery health tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Battery Health tab — shows charge, health %, wear, cycles, runtime.
/// </summary>
public sealed partial class BatteryHealthViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly Func<Task<BatteryInfo?>> _read;

    [ObservableProperty] private BatteryInfo _battery = new();
    [ObservableProperty] private string _summary = "Click Refresh to read battery data.";

    // Distinguishes "Windows answered that there is no battery" from "the read failed". The tab said "No battery
    // detected — this device runs on AC power only." either way, because the service reported a failed read as
    // no battery (#2503).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoBatteryText))]
    private bool _readFailed;

    /// <summary>The line shown in place of the battery cards when there is no reading to show.</summary>
    public string NoBatteryText => ReadFailed
        ? "The battery could not be read, so SysManager cannot tell whether this device has one."
        : "No battery detected on this device.";

    public BatteryHealthViewModel(BatteryService service) : this(() => service.GetBatteryInfoAsync()) { }

    /// <summary>Test seam: the battery read, so a failed one can be supplied without WMI.</summary>
    internal BatteryHealthViewModel(Func<Task<BatteryInfo?>> read)
    {
        _read = read;
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(InitAsync);
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy)) RefreshCommand.NotifyCanExecuteChanged();
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (ManagementException ex) { Log.Warning("Battery auto-scan failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Battery auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading battery info…";

        try
        {
            var battery = await _read();
            if (battery is null)
            {
                ShowFailedRead();
                return;
            }

            ReadFailed = false;
            Battery = battery;

            Summary = Battery.HasBattery
                ? Battery.HealthPercent >= 0
                    ? $"{Battery.Name} · {Battery.ChargePercent}% · Health {Battery.HealthPercent}% · {Battery.Status}"
                    : $"{Battery.Name} · {Battery.ChargePercent}% · Health: requires elevation · {Battery.Status}"
                : "No battery detected — this device runs on AC power only.";

            StatusMessage = Battery.HasBattery
                ? "Battery data loaded."
                : "No battery found.";
            Log.Information("Battery scan completed: {HasBattery}", Battery.HasBattery);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Battery read failed: {Error}", ex.Message);
            ShowFailedRead();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// A failed read changes nothing on screen: an earlier reading stays, and the summary and status line say the
    /// read failed rather than that there is no battery (#2503).
    /// </summary>
    private void ShowFailedRead()
    {
        ReadFailed = true;
        if (Battery.HasBattery)
        {
            StatusMessage = "The battery could not be read this time, so the figures below are from the last reading.";
            return;
        }

        Summary = "Could not read the battery. Press Refresh to try again.";
        StatusMessage = "The battery could not be read.";
    }
}
