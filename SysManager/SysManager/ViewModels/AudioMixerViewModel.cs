// SysManager · AudioMixerViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Volume Control tab — a per-application volume mixer over the default render endpoint.
/// Lists each app that is playing audio with a volume slider, mute toggle, and a live peak
/// meter. Membership + volume/mute are reconciled on a ~1&#160;s loop (paused when the tab
/// isn't visible, mirroring <see cref="ProcessManagerViewModel"/>); the peak meters are
/// driven by one shared <see cref="DispatcherTimer"/> that runs only while the tab is
/// active. Rows are reconciled IN PLACE by session id so dragging a slider survives a
/// refresh (a wholesale replace would raise a Reset and drop the drag).
///
/// <para>Preview scope: default render device only; per-app output-device routing and
/// volume presets are intentionally not part of this preview (see the view's banner).</para>
/// </summary>
public sealed partial class AudioMixerViewModel : ViewModelBase
{
    private const double PeakIntervalMs = 50;

    private readonly IAudioMixerService _service;
    private readonly DispatcherTimer _peakTimer;
    private CancellationTokenSource? _reconcileCts;

    public BulkObservableCollection<AudioSessionRowViewModel> Sessions { get; } = new();

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _hasSessions;

    public AudioMixerViewModel(IAudioMixerService service)
    {
        _service = service;
        _peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PeakIntervalMs) };
        _peakTimer.Tick += (_, _) => UpdatePeaks();
        StatusMessage = "Reading audio sessions…";
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        await ReconcileAsync();
        _reconcileCts = new CancellationTokenSource();
        _ = ReconcileLoopAsync(_reconcileCts.Token);
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                if (!IsActive) continue;
                await ReconcileAsync();
            }
            catch (OperationCanceledException) { break; /* expected on shutdown */ }
            // A single refresh fault (transient device/COM hiccup) must not kill the loop —
            // log and keep polling, mirroring ProcessManagerViewModel.
            catch (Exception ex) { Log.Debug("Audio mixer reconcile error: {Error}", ex.Message); }
        }
    }

    /// <summary>
    /// One membership + volume/mute reconcile pass: snapshot sessions off the UI thread,
    /// then merge into <see cref="Sessions"/> in place (surviving rows keep their instance,
    /// new rows are added, gone rows removed).
    /// </summary>
    [RelayCommand]
    internal async Task ReconcileAsync()
    {
        var snapshot = await Task.Run(_service.GetSessions).ConfigureAwait(true);
        MergeInto(snapshot);
        HasSessions = Sessions.Count > 0;
        StatusMessage = Sessions.Count > 0
            ? $"{Sessions.Count} app{(Sessions.Count == 1 ? "" : "s")} playing audio."
            : "No apps are playing audio right now.";
    }

    /// <summary>
    /// Merges <paramref name="snapshot"/> into <see cref="Sessions"/> keyed by session id:
    /// surviving sessions keep their existing row (with volume/mute/name refreshed via
    /// <see cref="AudioSessionRowViewModel.ApplyUpdate"/>), new sessions are added, and
    /// ended sessions are removed. Preserving instances is what lets a slider being dragged
    /// survive the refresh instead of a Reset dropping it.
    /// </summary>
    internal void MergeInto(IReadOnlyList<AudioSessionInfo> snapshot)
    {
        var existing = Sessions.ToDictionary(r => r.SessionId, StringComparer.Ordinal);
        var seen = new HashSet<string>(snapshot.Count, StringComparer.Ordinal);

        foreach (var info in snapshot)
        {
            seen.Add(info.SessionId);
            if (existing.TryGetValue(info.SessionId, out var row))
                row.ApplyUpdate(info);
            else
                Sessions.Add(new AudioSessionRowViewModel(_service, info));
        }

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Sessions[i].SessionId))
                Sessions.RemoveAt(i);
        }

        // Keep display order stable: system sounds last, apps alphabetical (matches the
        // service's own ordering so surviving rows don't jump around between refreshes).
        SortInPlace();
    }

    private void SortInPlace()
    {
        var desired = Sessions
            .OrderBy(r => r.IsSystemSounds)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < desired.Count; i++)
        {
            int current = Sessions.IndexOf(desired[i]);
            if (current != i) Sessions.Move(current, i);
        }
    }

    /// <summary>Refresh every row's peak meter from the service (fast, cheap, in place).</summary>
    internal void UpdatePeaks()
    {
        foreach (var row in Sessions)
            row.PeakLevel = _service.GetPeak(row.SessionId);
    }

    partial void OnIsActiveChanged(bool value)
    {
        // The peak meter is expensive to poll and pointless when the tab is hidden — run
        // the timer only while the tab is active (R4: no poll work when not visible).
        if (value) _peakTimer.Start();
        else
        {
            _peakTimer.Stop();
            foreach (var row in Sessions) row.PeakLevel = 0f;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _peakTimer.Stop();
            _reconcileCts?.Cancel();
            _reconcileCts?.Dispose();
            _reconcileCts = null; // idempotent: a second Dispose() must not re-Cancel a disposed CTS
        }
        base.Dispose(disposing);
    }
}
