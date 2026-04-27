// SysManager · SpeedTestViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>HTTP + Ookla speed tests.</summary>
public partial class SpeedTestViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    private CancellationTokenSource? _speedCts;

    [ObservableProperty] private SpeedTestResult? _httpResult;
    [ObservableProperty] private SpeedTestResult? _ooklaResult;
    [ObservableProperty] private int _speedProgress;
    [ObservableProperty] private string _speedStatus = "";
    [ObservableProperty] private string _httpStatus = "";
    [ObservableProperty] private string _ooklaStatus = "";
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private bool _isHttpTesting;
    [ObservableProperty] private bool _isOoklaTesting;

    public SpeedTestViewModel(NetworkSharedState shared)
    {
        Shared = shared;
    }

    [RelayCommand]
    private async Task RunHttpSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        IsHttpTesting = true;
        SpeedProgress = 0;
        HttpStatus = "Starting HTTP speed test…";
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t =>
        { SpeedProgress = t.p; HttpStatus = t.m; });
        try
        {
            HttpResult = await Shared.Speed.RunHttpAsync(progress, _speedCts.Token);
            HttpStatus = "HTTP done";
            Log.Information("HTTP speed test: {Down:F1} Mbps down, {Up:F1} Mbps up",
                HttpResult.DownloadMbps, HttpResult.UploadMbps);
        }
        catch (OperationCanceledException) { HttpStatus = "Cancelled"; }
        catch (System.Net.Http.HttpRequestException ex)
        { HttpStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { HttpStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsHttpTesting = false; }
    }

    [RelayCommand]
    private async Task RunOoklaSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        IsOoklaTesting = true;
        SpeedProgress = 0;
        OoklaStatus = "Starting Ookla speed test…";
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t =>
        { SpeedProgress = t.p; OoklaStatus = t.m; });
        try
        {
            OoklaResult = await Shared.Speed.RunOoklaAsync(progress, _speedCts.Token);
            OoklaStatus = "Ookla done";
            Log.Information("Ookla speed test: {Down:F1} Mbps down, {Up:F1} Mbps up",
                OoklaResult.DownloadMbps, OoklaResult.UploadMbps);
        }
        catch (OperationCanceledException) { OoklaStatus = "Cancelled"; }
        catch (System.ComponentModel.Win32Exception ex)
        { OoklaStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { OoklaStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsOoklaTesting = false; }
    }

    [RelayCommand]
    private void CancelSpeed() => _speedCts?.Cancel();
}
