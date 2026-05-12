// SysManager · SpeedTestViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>HTTP + Ookla speed tests with persistent history.</summary>
public partial class SpeedTestViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    private readonly SpeedTestHistoryService _history = new();
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

    /// <summary>Persisted history of HTTP speed test results (newest first).</summary>
    public ObservableCollection<SpeedTestResult> HttpHistory { get; } = new();

    /// <summary>Persisted history of Ookla speed test results (newest first).</summary>
    public ObservableCollection<SpeedTestResult> OoklaHistory { get; } = new();

    public SpeedTestViewModel(NetworkSharedState shared)
    {
        Shared = shared;
        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var all = await _history.LoadAsync();
            HttpHistory.Clear();
            OoklaHistory.Clear();
            foreach (var r in all.Where(r => string.Equals(r.Engine, "HTTP", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(r => r.CompletedAt))
                HttpHistory.Add(r);
            foreach (var r in all.Where(r => string.Equals(r.Engine, "Ookla", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(r => r.CompletedAt))
                OoklaHistory.Add(r);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "Failed to load speed test history");
        }
    }

    [RelayCommand]
    private async Task RunHttpSpeedAsync()
    {
        if (IsSpeedTesting) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "HTTP Speed Test");
        if (opLock == null)
        {
            HttpStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
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

            // Persist result to history.
            await _history.SaveAsync(HttpResult);
            HttpHistory.Insert(0, HttpResult);
            if (HttpHistory.Count > SpeedTestHistoryService.MaxPerEngine)
                HttpHistory.RemoveAt(HttpHistory.Count - 1);
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
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Ookla Speed Test");
        if (opLock == null)
        {
            OoklaStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
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

            // Persist result to history.
            await _history.SaveAsync(OoklaResult);
            OoklaHistory.Insert(0, OoklaResult);
            if (OoklaHistory.Count > SpeedTestHistoryService.MaxPerEngine)
                OoklaHistory.RemoveAt(OoklaHistory.Count - 1);
        }
        catch (OperationCanceledException) { OoklaStatus = "Cancelled"; }
        catch (System.ComponentModel.Win32Exception ex)
        { OoklaStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { OoklaStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsOoklaTesting = false; }
    }

    [RelayCommand]
    private async Task ClearHttpHistoryAsync()
    {
        await _history.ClearAsync("HTTP");
        HttpHistory.Clear();
    }

    [RelayCommand]
    private async Task ClearOoklaHistoryAsync()
    {
        await _history.ClearAsync("Ookla");
        OoklaHistory.Clear();
    }

    [RelayCommand]
    private void CancelSpeed() => _speedCts?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _speedCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
