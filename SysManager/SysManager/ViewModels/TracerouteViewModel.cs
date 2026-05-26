// SysManager · TracerouteViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Auto-traceroute + manual trace. Has its own Start/Stop for the
/// auto-trace monitor, independent of the ping monitor.
/// </summary>
public sealed partial class TracerouteViewModel : ViewModelBase
{
    public NetworkSharedState Shared { get; }
    public ConsoleViewModel Console { get; } = new();
    private CancellationTokenSource? _traceCts;
    private int _totalHops;

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
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Traceroute");
        if (opLock is null)
        {
            TraceStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
        IsTracing = true;
        TraceStatus = $"Tracing {TraceHost}…";
        Console.Append(PowerShellLine.Info($"Traceroute to {TraceHost} started…"));

        _traceCts?.Dispose();
        _traceCts = new CancellationTokenSource();
        var collected = new List<TracerouteHop>();
        _totalHops = 0;
        void OnHop(TracerouteHop hop)
        {
            collected.Add(hop);
            _totalHops = collected.Count;
            Shared.InvokeOnUi(() =>
            {
                TraceStatus = $"Tracing {TraceHost}… hop {hop.HopNumber}";
                AppendHopLine(hop, isLast: false);
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
                // Re-append the last hop with destination-reached explanation
                if (collected.Count > 0)
                {
                    var last = collected[^1];
                    AppendHopLine(last, isLast: true);
                }
                Console.Append(PowerShellLine.Info($"Traceroute complete — {collected.Count} hops."));
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

    private void AppendHopLine(TracerouteHop hop, bool isLast)
    {
        var explanation = GetHopExplanation(hop, isLast);
        string line;
        if (hop.LatencyMs.HasValue)
        {
            var hostPart = !string.IsNullOrEmpty(hop.HostName) && hop.HostName != hop.Address
                ? $"{hop.Address} ({hop.HostName})"
                : hop.Address;
            line = $"Hop {hop.HopNumber}: {hostPart} — {hop.LatencyMs.Value:F1} ms [{explanation}]";
            Console.Append(PowerShellLine.Output(line));
        }
        else
        {
            line = $"Hop {hop.HopNumber}: * — Request timed out [{explanation}]";
            Console.Append(PowerShellLine.Warn(line));
        }
    }

    /// <summary>
    /// Returns a human-readable explanation of a hop's role in the route.
    /// </summary>
    private static string GetHopExplanation(TracerouteHop hop, bool isLast)
    {
        if (isLast && hop.LatencyMs.HasValue)
            return "Destination reached";

        if (!hop.LatencyMs.HasValue)
            return "Filtered node — does not respond to ICMP";

        if (hop.HopNumber == 1 && IsPrivateAddress(hop.Address))
            return "Your local router/gateway";

        if (IsPrivateAddress(hop.Address))
            return "Local network node";

        return "Transit node";
    }

    /// <summary>
    /// Checks whether the given IP address is in a private range
    /// (10.x.x.x, 192.168.x.x, 172.16-31.x.x).
    /// </summary>
    private static bool IsPrivateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _traceCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
