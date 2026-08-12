// SysManager · TracerouteViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Auto-traceroute + manual trace. Has its own Start/Stop for the auto-trace monitor, but that
/// monitor is <em>shared</em> with the Ping tab: <see cref="NetworkSharedState.StartMonitoring"/>
/// and <see cref="NetworkSharedState.StopMonitoring"/> start and stop the very same
/// <c>TraceMonitor</c>. Running state therefore lives on <see cref="NetworkSharedState"/>, not here.
/// </summary>
public sealed partial class TracerouteViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    private CancellationTokenSource? _traceCts;

    [ObservableProperty] private string _traceHost = "8.8.8.8";
    [ObservableProperty] private bool _isTracing;
    [ObservableProperty] private string _traceStatus = "";

    /// <summary>
    /// Whether the shared auto-trace monitor is running — a read-through to
    /// <see cref="NetworkSharedState.IsAutoTraceRunning"/>, which is the single owner of that state.
    /// This used to be a local <c>[ObservableProperty]</c>, which went stale the moment the Ping tab
    /// touched the monitor: Ping's Stop killed a live auto-trace while this tab still showed
    /// "Stop auto-trace" and claimed it was running.
    /// </summary>
    public bool IsAutoTraceRunning => Shared.IsAutoTraceRunning;

    public TracerouteViewModel(NetworkSharedState shared)
    {
        Shared = shared;
        // Re-raise so bindings on THIS VM's property update when the other tab flips the shared flag.
        Shared.PropertyChanged += OnSharedPropertyChanged;
    }

    private void OnSharedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NetworkSharedState.IsAutoTraceRunning))
            OnPropertyChanged(nameof(IsAutoTraceRunning));
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
        Shared.IsAutoTraceRunning = true;
        StatusMessage = $"Auto-trace running ({TraceHost})";
        Log.Information("Auto-traceroute started for {Host}", TraceHost);

        // Run an initial trace immediately so the user sees results right away
        await TraceAsync();
    }

    [RelayCommand]
    private void StopAutoTrace()
    {
        Shared.TraceMonitor.Stop();
        Shared.IsAutoTraceRunning = false;
        StatusMessage = "Auto-trace stopped";
        Log.Information("Auto-traceroute stopped");
    }

    [RelayCommand]
    private async Task TraceAsync()
    {
        if (string.IsNullOrWhiteSpace(TraceHost)) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Traceroute");
        if (opLock is null)
        {
            TraceStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
        IsTracing = true;
        TraceStatus = $"Tracing {TraceHost}…";

        _traceCts?.Dispose();
        _traceCts = new CancellationTokenSource();
        List<TracerouteHop> collected = [];
        void OnHop(TracerouteHop hop)
        {
            collected.Add(hop);
            Shared.InvokeOnUi(() =>
            {
                TraceStatus = $"Tracing {TraceHost}… hop {hop.HopNumber}";
            });
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // NetworkSharedState is a DI singleton that outlives this VM, so a live handler here
            // would keep the VM alive for the rest of the session.
            Shared.PropertyChanged -= OnSharedPropertyChanged;
            _traceCts?.Cancel();
            _traceCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
