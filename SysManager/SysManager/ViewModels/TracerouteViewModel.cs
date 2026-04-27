// SysManager · TracerouteViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>
/// Auto-traceroute + manual trace. Has its own Start/Stop for the
/// auto-trace monitor, independent of the ping monitor.
/// </summary>
public partial class TracerouteViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    private CancellationTokenSource? _traceCts;

    [ObservableProperty] private string _traceHost = "8.8.8.8";
    [ObservableProperty] private bool _isTracing;
    [ObservableProperty] private string _traceStatus = "";
    [ObservableProperty] private bool _isAutoTraceRunning;

    public TracerouteViewModel(NetworkSharedState shared)
    {
        Shared = shared;
    }

    [RelayCommand]
    private async Task StartAutoTraceAsync()
    {
        if (string.IsNullOrWhiteSpace(TraceHost)) return;

        // Ensure the current TraceHost is tracked by the monitor
        Shared.TraceMonitor.AddHost(TraceHost);
        Shared.TraceMonitor.Interval = TimeSpan.FromSeconds(
            Math.Max(10, Shared.TraceIntervalSeconds));
        Shared.TraceMonitor.Start();
        IsAutoTraceRunning = true;
        StatusMessage = $"Auto-trace running ({TraceHost})";
        Log.Information("Auto-traceroute started for {Host}", TraceHost);

        // Run an initial trace immediately so the user sees results right away
        await TraceAsync();
    }

    [RelayCommand]
    private void StopAutoTrace()
    {
        Shared.TraceMonitor.Stop();
        IsAutoTraceRunning = false;
        StatusMessage = "Auto-trace stopped";
        Log.Information("Auto-traceroute stopped");
    }

    [RelayCommand]
    private async Task TraceAsync()
    {
        if (string.IsNullOrWhiteSpace(TraceHost)) return;
        IsTracing = true;
        TraceStatus = $"Tracing {TraceHost}…";

        _traceCts = new CancellationTokenSource();
        var collected = new List<TracerouteHop>();
        void OnHop(TracerouteHop hop)
        {
            collected.Add(hop);
            Shared.InvokeOnUi(() =>
                TraceStatus = $"Tracing {TraceHost}… hop {hop.HopNumber}");
        }

        Shared.Tracer.HopCompleted += OnHop;
        try
        {
            await Shared.Tracer.RunAsync(TraceHost, _traceCts.Token);
            Shared.InvokeOnUi(() =>
            {
                Shared.ApplyRoute(TraceHost, collected);
                TraceStatus = $"Done — {collected.Count} hops";
            });
        }
        catch (OperationCanceledException) { TraceStatus = "Cancelled"; }
        catch (System.ComponentModel.Win32Exception ex)
        { TraceStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { TraceStatus = "Error: " + ex.Message; }
        finally
        {
            Shared.Tracer.HopCompleted -= OnHop;
            IsTracing = false;
        }
    }

    [RelayCommand]
    private void CancelTrace() => _traceCts?.Cancel();
}
