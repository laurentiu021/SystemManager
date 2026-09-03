// SysManager · AudioMixerViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
/// meter. Two loops drive it, both idle while the tab is hidden and both sampling off the UI
/// thread: membership + volume/mute reconcile on a ~1&#160;s cadence (mirroring
/// <see cref="ProcessManagerViewModel"/>), and the peak meters refresh every 50&#160;ms via a
/// single batched service call — that one PARKS while hidden rather than ticking and skipping,
/// because at 50&#160;ms even a skip-check costs 20 Dispatcher items a second. Rows are reconciled IN PLACE by session id
/// so dragging a slider survives a refresh (a wholesale replace would raise a Reset and
/// drop the drag).
///
/// <para>Preview scope: default render device only; per-app output-device routing and
/// volume presets are intentionally not part of this preview (see the view's banner).</para>
/// </summary>
public sealed partial class AudioMixerViewModel : ViewModelBase
{
    private const double PeakIntervalMs = 50;

    private readonly IAudioMixerService _service;
    private readonly VolumePresetService _presets;
    private CancellationTokenSource? _reconcileCts;

    // Completed while the tab is visible, replaced with a fresh incomplete source when it is hidden.
    // PeakLoopAsync awaits this BEFORE its delay, so a hidden tab costs nothing at all rather than
    // waking 20 times a second to decide it has nothing to do. Only touched on the UI thread
    // (OnIsActiveChanged and the loop's own continuations), so it needs no synchronisation.
    private TaskCompletionSource _activated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BulkObservableCollection<AudioSessionRowViewModel> Sessions { get; } = new();

    /// <summary>
    /// Output devices offered in each row's routing picker. Every row is handed THIS collection, not a copy,
    /// so a refresh reaches pickers that already exist — rows used to receive <c>OutputDevices.ToList()</c>,
    /// which froze each picker at the moment its row was created.
    /// </summary>
    public BulkObservableCollection<AudioDevice> OutputDevices { get; } = new();

    /// <summary>
    /// Reconcile passes between device re-enumerations. Reconcile runs at 1 Hz and enumerating endpoints is
    /// COM-heavy, so devices are re-read every tenth pass (~10 s) rather than every one. Devices change when
    /// somebody plugs something in, which is a human-timescale event; sessions change when an app starts
    /// playing, which is not — hence the two cadences.
    /// </summary>
    private const int ReconcilePassesPerDeviceRefresh = 10;

    private int _passesSinceDeviceRefresh;

    /// <summary>Saved volume presets the user can apply or delete.</summary>
    public BulkObservableCollection<VolumePreset> Presets { get; } = new();

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _hasSessions;

    /// <summary>True when true in-app per-app routing is available; false shows the guided fallback.</summary>
    [ObservableProperty] private bool _routingSupported;

    /// <summary>The preset the user has selected to apply/delete.</summary>
    [ObservableProperty] private VolumePreset? _selectedPreset;

    /// <summary>Name typed in the "save preset" box.</summary>
    [ObservableProperty] private string _newPresetName = "";

    public AudioMixerViewModel(IAudioMixerService service, VolumePresetService presets)
    {
        _service = service;
        _presets = presets;
        StatusMessage = "Reading audio sessions…";
        InitializeAsync(InitAsync);
    }

    private async Task InitAsync()
    {
        // Create the CTS BEFORE the first await so a Dispose() that races this init can see and
        // cancel it (otherwise Dispose's _reconcileCts?.Cancel() would no-op on a still-null CTS,
        // and the loop below would start uncancellable). If Dispose already ran, Dispose() nulled
        // the field back out — so re-check and don't start an orphan loop.
        _reconcileCts = new CancellationTokenSource();
        var ct = _reconcileCts.Token;

        // Only the FIRST load drives the progress bar. The 1 s reconcile loop below deliberately
        // does not: it would strobe the bar on and off for as long as the tab stays open. Without
        // this the bound bar (and the sidebar spinner) could never appear at all, even though
        // enumerating audio sessions and render devices is COM work run off the UI thread.
        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            RoutingSupported = _service.IsRoutingSupported;
            Presets.ReplaceWith(_presets.Load());
            await RefreshDevicesAsync();
            await ReconcileAsync();
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }

        if (_reconcileCts is null || ct.IsCancellationRequested) return; // disposed during init
        _ = ReconcileLoopAsync(ct);
        _ = PeakLoopAsync(ct);
    }

    /// <summary>
    /// Drives the VU meters at <see cref="PeakIntervalMs"/>, sampling OFF the UI thread.
    /// <para>This was a <c>DispatcherTimer</c> whose Tick called the service synchronously, once per
    /// visible row. Each of those calls took the service lock and marshalled a cross-apartment COM call
    /// per audio control, so with ten apps playing it was ~200 COM transitions a second on the thread
    /// that draws the window — and roughly once a second a tick blocked there behind the 1 Hz reconcile,
    /// which holds the same lock while enumerating every session. The symptom was choppy meters and
    /// sliders that felt laggy. Same defect as the Bandwidth Monitor fix in 1.61.9, at 20x the
    /// cadence.</para>
    /// <para>Now one <c>GetPeaks</c> call per tick does all the COM work on a worker thread and only the
    /// resulting numbers come back here. Shares the reconcile loop's token, so Dispose stops both.</para>
    /// <para>While the tab is hidden the loop parks on <c>_activated</c> instead of ticking and skipping:
    /// at 50&#160;ms a skip-check would still queue 20 continuations a second onto the Dispatcher for the
    /// entire life of the app once the tab had been opened once — the timer this replaced was genuinely
    /// STOPPED when the tab was hidden, and parity with that matters more here than anywhere else in the
    /// app because of the cadence.</para>
    /// </summary>
    private async Task PeakLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _activated.Task.WaitAsync(ct).ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromMilliseconds(PeakIntervalMs), ct).ConfigureAwait(true);
                if (!IsActive) continue;
                await UpdatePeaksAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { break; /* expected on shutdown */ }
            // A transient COM/device fault must not kill the meters for the rest of the session —
            // log and keep polling, mirroring ReconcileLoopAsync.
            catch (Exception ex) { Log.Debug("Audio mixer peak error: {Error}", ex.Message); }
        }
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

        // Re-read the device list on a slower cadence than the sessions. Before this it was read ONCE, in
        // InitAsync, so a headset plugged in after the tab opened never appeared in any picker for the rest
        // of the session — and the tab's view model lives as long as the app does.
        if (++_passesSinceDeviceRefresh >= ReconcilePassesPerDeviceRefresh)
        {
            _passesSinceDeviceRefresh = 0;
            await RefreshDevicesAsync().ConfigureAwait(true);
        }

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
            {
                var newRow = new AudioSessionRowViewModel(
                    // The LIVE collection, not a snapshot: a device plugged in after this row was created
                    // must appear in its picker. BulkObservableCollection raises collection-changed, so the
                    // bound ComboBox updates itself.
                    _service, info, OutputDevices, RoutingSupported,
                    reportFailure: message => StatusMessage = message);
                if (RoutingSupported && !info.IsSystemSounds)
                    newRow.SetOutputDeviceFromService(_service.GetSessionOutputDevice(info.SessionId));
                Sessions.Add(newRow);
            }
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

    /// <summary>
    /// One peak-meter pass: read every visible row's level in a SINGLE service call made on a worker
    /// thread, then write the numbers back here. One call for all rows (not one per row) is what keeps
    /// the service lock taken once per tick instead of once per app.
    /// </summary>
    internal async Task UpdatePeaksAsync(CancellationToken ct)
    {
        var ids = Sessions.Select(r => r.SessionId).ToArray();
        if (ids.Length == 0) return;

        var peaks = await Task.Run(() => _service.GetPeaks(ids), ct).ConfigureAwait(true);

        // Dispose can land while the sample is in flight; the rows have already been zeroed by then,
        // so writing these levels back would re-light meters on a torn-down tab.
        //
        // !IsActive belongs in the SAME guard for the same reason, and was the half that was missing: the
        // Task.Run genuinely yields, so hiding the tab or minimising the window mid-sample zeroes every
        // row in OnIsActiveChanged and the continuation then writes the old levels straight back. The loop
        // parks on its next iteration, so those stale bars survive for as long as the tab stays hidden —
        // on re-show the user sees a lit meter from minutes ago until the next sample lands.
        if (_reconcileCts is null || ct.IsCancellationRequested || !IsActive) return;

        foreach (var row in Sessions)
            if (peaks.TryGetValue(row.SessionId, out var peak))
                row.PeakLevel = peak;
    }

    /// <summary>
    /// Re-enumerate output devices (off the UI thread) into the shared picker list, preserving each row's
    /// current choice across the replacement.
    /// <para>Preserving it is not optional. <c>ReplaceWith</c> clears and refills, so a bound
    /// <c>SelectedItem</c> survives only while the new list contains an EQUAL element — and
    /// <c>AudioDevice</c> is a record, so equality also covers <c>IsDefault</c>. Change the Windows default
    /// output and every row's selection would silently fall to null, i.e. the picker forgets where the user
    /// sent that app. Re-resolving by endpoint id is what keeps it.</para>
    /// </summary>
    private async Task RefreshDevicesAsync()
    {
        // Defensive: a substituted/edge-case service could return null; treat it as "no devices"
        // rather than letting ReplaceWith's null-guard throw into the fire-and-forget init.
        var devices = await Task.Run(_service.GetRenderDevices).ConfigureAwait(true);

        // string?, carrying all three states. Collapsing an unknown route to string.Empty here undid the fix
        // that introduced the placeholder: empty means "read succeeded, no override", which
        // SetOutputDeviceFromService resolves to the IsDefault entry and marks as KNOWN. Since this runs every
        // tenth reconcile pass, every routable row silently went from "Choose a device" back to naming the
        // default device about ten seconds after the tab was opened — the exact false claim the placeholder
        // exists to avoid.
        var chosen = Sessions.ToDictionary(
            r => r.SessionId,
            r => r.OutputRouteUnknown ? null : (r.SelectedOutputDevice?.Id ?? string.Empty),
            StringComparer.Ordinal);
        OutputDevices.ReplaceWith(devices ?? []);
        // Gated on the row's own routing capability, matching the construction-time gate in MergeInto: the
        // system-sounds pseudo-session shows no picker, so it must not be handed a destination it cannot use.
        foreach (var row in Sessions)
            if (row.RoutingSupported && chosen.TryGetValue(row.SessionId, out var id))
                row.SetOutputDeviceFromService(id);
    }

    /// <summary>
    /// Save the current per-app volume/mute as a named preset (keyed by exe name so it re-applies
    /// across restarts). Confirms before overwriting a same-named preset. No-ops on a blank name.
    /// </summary>
    [RelayCommand]
    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0) { StatusMessage = "Enter a name for the preset first."; return; }

        var entries = Sessions
            .Where(s => !s.IsSystemSounds)
            .Select(s => new VolumePresetEntry(
                ExeNameOf(s), s.DisplayName, s.Volume, s.IsMuted))
            .Where(e => e.ExecutableName.Length > 0)
            .ToList();

        if (entries.Count == 0) { StatusMessage = "No apps to save into a preset right now."; return; }

        // Overwriting destroys exactly the same data as deleting, with the same absence of undo —
        // Save() rewrites the presets file on disk immediately — and DeletePreset below already confirms
        // for that reason. So saving over an existing name asks too. A NEW name is not destructive and
        // is not interrupted; only the overwrite is.
        var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !DialogService.Instance.Confirm(
                $"Replace the existing preset \"{existing.Name}\"?\n\n" +
                $"Its saved levels for {existing.Entries.Count} app{(existing.Entries.Count == 1 ? "" : "s")} " +
                "will be overwritten with the current ones, and the old values are gone for good.",
                "Replace Preset — Confirm"))
        {
            return;
        }

        Presets.ReplaceWith(_presets.Save(new VolumePreset(name, entries)));
        NewPresetName = "";
        StatusMessage = $"Saved preset \"{name}\" with {entries.Count} app{(entries.Count == 1 ? "" : "s")}.";
        ActivityLogService.Instance.Log("Volume", $"Saved preset '{name}'");
    }

    /// <summary>Apply the selected preset's volumes/mutes to the matching live sessions.</summary>
    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is null) { StatusMessage = "Pick a preset to apply."; return; }

        var live = Sessions.Select(s => new AudioSessionInfo(
            s.SessionId, s.ProcessId, s.DisplayName, ExePathOf(s), s.Volume, s.IsMuted,
            s.IsActive ? AudioSessionState.Active : AudioSessionState.Inactive, s.IsSystemSounds, 0f)).ToList();

        var plan = VolumePresetService.BuildApplyPlan(SelectedPreset, live);
        int applied = 0;
        foreach (var (sessionId, volume, muted) in plan)
        {
            var row = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (row is null) continue;
            row.Volume = volume;   // propagates to the service via the row's changed-handler
            row.IsMuted = muted;
            applied++;
        }
        StatusMessage = applied > 0
            ? $"Applied \"{SelectedPreset.Name}\" to {applied} app{(applied == 1 ? "" : "s")}."
            : $"No running apps matched \"{SelectedPreset.Name}\".";
    }

    /// <summary>Delete the selected preset (with confirmation — the file is rewritten immediately).</summary>
    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;
        var name = SelectedPreset.Name;

        // Delete() rewrites the presets file on disk straight away, so there is no undo.
        if (!DialogService.Instance.Confirm(
                $"Delete the preset \"{name}\"?\n\n" +
                "The saved volume levels for its apps will be gone for good.",
                "Delete Preset — Confirm"))
            return;

        Presets.ReplaceWith(_presets.Delete(name));
        SelectedPreset = null;
        StatusMessage = $"Deleted preset \"{name}\".";
    }

    // The row doesn't expose the exe path directly; derive both from its icon-source path proxy.
    private static string ExeNameOf(AudioSessionRowViewModel row) => VolumePresetService.ExeName(ExePathOf(row));
    private static string ExePathOf(AudioSessionRowViewModel row) => row.ExePath;

    partial void OnIsActiveChanged(bool value)
    {
        // Release / re-arm the peak loop's gate (R4: no poll work at all when not visible — the loop
        // parks rather than ticking), and darken the meters on the way out so a hidden tab isn't left
        // showing whatever level it happened to stop on.
        if (value)
        {
            _activated.TrySetResult();
            return;
        }

        if (_activated.Task.IsCompleted)
            _activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var row in Sessions) row.PeakLevel = 0f;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Cancel FIRST: a sample already in flight must be told to stop before the meters are
            // zeroed, or its post-await write-back would re-light them.
            _reconcileCts?.Cancel();
            _reconcileCts?.Dispose();
            _reconcileCts = null; // idempotent: a second Dispose() must not re-Cancel a disposed CTS
            foreach (var row in Sessions) row.PeakLevel = 0f; // don't leave stale lit meters
        }
        base.Dispose(disposing);
    }
}
