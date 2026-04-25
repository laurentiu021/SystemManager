// SysManager · DashboardViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly SystemInfoService _sys;

    [ObservableProperty] private SystemSnapshot? _snapshot;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _osLine = "";
    [ObservableProperty] private string _cpuLine = "";
    [ObservableProperty] private string _memLine = "";
    [ObservableProperty] private string _diskLine = "";
    [ObservableProperty] private string _uptimeLine = "";

    public DashboardViewModel(SystemInfoService sys)
    {
        _sys = sys;
        IsElevated = AdminHelper.IsElevated();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning system...";
        try
        {
            Snapshot = await _sys.CaptureAsync();
            OsLine = $"{Snapshot.Os.Caption} ({Snapshot.Os.Architecture}) build {Snapshot.Os.BuildNumber}";
            CpuLine = $"{Snapshot.Cpu.Name} — {Snapshot.Cpu.Cores} cores / {Snapshot.Cpu.LogicalProcessors} threads @ {Snapshot.Cpu.MaxClockMHz} MHz — load {Snapshot.Cpu.LoadPercent:0}%";
            MemLine = $"{Snapshot.Memory.UsedGB:0.0} / {Snapshot.Memory.TotalGB:0.0} GB ({Snapshot.Memory.UsedPercent:0}%)";
            DiskLine = string.Join(" | ", Snapshot.Disks.Select(d => $"{d.FriendlyName} {d.SizeGB:0}GB {d.MediaType} {d.HealthStatus}"));
            UptimeLine = $"Uptime: {Snapshot.Os.Uptime.Days}d {Snapshot.Os.Uptime.Hours}h {Snapshot.Os.Uptime.Minutes}m";
            StatusMessage = $"Last scan: {Snapshot.CapturedAt:HH:mm:ss}";
            Log.Information("Dashboard scan completed");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void RequestElevation()
    {
        Log.Information("Admin elevation requested from Dashboard");
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current.Shutdown();
    }
}
