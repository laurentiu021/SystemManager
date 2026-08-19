// SysManager · AudioMixerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Specialized;
using System.Reflection;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AudioMixerViewModel"/> and <see cref="AudioSessionRowViewModel"/>.
/// The whole audio surface sits behind <see cref="IAudioMixerService"/>, so every test
/// substitutes it with a deterministic session list — no NAudio, no COM, no real audio
/// hardware is touched. Coverage targets the ViewModel logic that matters: the in-place
/// reconcile (surviving rows keep their instance, adds/removes without a collection Reset
/// — pinning the "ReplaceWith drops the dragged slider" trap), volume/mute propagation to
/// the service with an echo-suppression guard on external updates, the mid-drag guard, the
/// identity refresh, the peak-meter update path (batched into ONE service call, off the UI
/// thread, and guarded against a Dispose landing mid-sample), the empty-state flag, and
/// deterministic disposal. The <c>IsActive</c> gate is verified only for the peak-meter half
/// (the zeroing on the way out); both loops' pause-when-hidden branches are delay-driven and
/// are not unit-tested (they would need a time seam). The COM enumeration and
/// grouping itself lives in <see cref="AudioMixerService"/>
/// (not unit-tested here — it needs a real endpoint), so these tests treat the service's
/// filtering (expired dropped, system-sounds flagged) as a contract at the seam.
/// </summary>
// DeletePreset's confirmation gate swaps the global DialogService.Instance, so this class must
// run in the serialized collection — otherwise a parallel class's substitute steals the Confirm
// call and the gate assertions go flaky.
[Collection("ProcessWideStatics")]
public class AudioMixerViewModelTests
{
    private static AudioSessionInfo Session(
        string id,
        uint pid = 1000,
        string name = "app",
        float volume = 0.5f,
        bool muted = false,
        AudioSessionState state = AudioSessionState.Active,
        bool systemSounds = false,
        float peak = 0f) =>
        new(id, pid, name, ExePath: "", volume, muted, state, systemSounds, peak);

    // A session WITH an executable path. SavePreset derives each preset entry's key from the exe
    // name and drops entries whose name is empty, so the ExePath-less Session() helper above
    // produces an empty preset and SavePreset returns "No apps to save" before any confirm.
    private static AudioSessionInfo SessionWithExe(
        string id = "s1", uint pid = 10, string name = "Chrome", float volume = 0.5f,
        string exePath = @"C:\Program Files\Test\chrome.exe") =>
        new(id, pid, name, exePath, volume, IsMuted: false, AudioSessionState.Active,
            IsSystemSounds: false, PeakLevel: 0f);

    // A substitute service that returns a fixed session list from GetSessions.
    private static IAudioMixerService ServiceWith(params AudioSessionInfo[] sessions)
    {
        var service = Substitute.For<IAudioMixerService>();
        service.GetSessions().Returns(sessions.ToList());
        return service;
    }

    /// <summary>
    /// Stubs BOTH peak reads from one level table. Answers whatever ids the caller passes rather than a
    /// fixed list, so a test does not silently stop covering a row it added — and returns 0 for an unknown
    /// id, matching the service contract that every requested id gets a value.
    /// <para>The single-id <c>GetPeak</c> is stubbed deliberately, even though production no longer calls
    /// it: it makes the per-row shape return CORRECT levels. Otherwise a refactor back to a per-row loop
    /// would read an unstubbed method, get 0 everywhere, and fail the value assertions — so the call-count
    /// test would look redundant when it is the only thing that actually pins the batching.</para>
    /// </summary>
    private static void Peaks(IAudioMixerService service, params (string SessionId, float Peak)[] peaks)
    {
        var byId = peaks.ToDictionary(p => p.SessionId, p => p.Peak, StringComparer.Ordinal);
        service.GetPeaks(Arg.Any<IEnumerable<string>>()).Returns(call =>
            ((IEnumerable<string>)call[0]).ToDictionary(
                id => id,
                id => byId.TryGetValue(id, out var v) ? v : 0f,
                StringComparer.Ordinal));
        service.GetPeak(Arg.Any<string>()).Returns(call =>
            byId.TryGetValue((string)call[0], out var v) ? v : 0f);
    }

    // The constructor kicks off an async reconcile off the UI thread; await init so
    // Sessions is populated before asserting (mirrors CpuAffinityViewModelTests.NewVm).
    private static AudioMixerViewModel NewVm(IAudioMixerService service)
    {
        // A preset service pointed at a throwaway temp dir so tests never read/write the real
        // %LocalAppData%\SysManager\volume-presets.json.
        var presets = new VolumePresetService(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests", System.Guid.NewGuid().ToString("N")));
        var vm = new AudioMixerViewModel(service, presets);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // ── Construction / population ──────────────────────────────────────────

    [Fact]
    public void Constructor_PopulatesRows_FromService()
    {
        var vm = NewVm(ServiceWith(
            Session("s1", pid: 10, name: "Chrome", volume: 0.8f),
            Session("s2", pid: 20, name: "Spotify", volume: 0.3f, muted: true)));

        Assert.Equal(2, vm.Sessions.Count);
        Assert.True(vm.HasSessions);

        var chrome = vm.Sessions.Single(r => r.SessionId == "s1");
        Assert.Equal("Chrome", chrome.DisplayName);
        Assert.Equal(0.8f, chrome.Volume);
        Assert.False(chrome.IsMuted);

        var spotify = vm.Sessions.Single(r => r.SessionId == "s2");
        Assert.Equal(0.3f, spotify.Volume);
        Assert.True(spotify.IsMuted);
    }

    [Fact]
    public void Constructor_EmptyService_HasNoSessions()
    {
        var vm = NewVm(ServiceWith());
        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasSessions);
        Assert.NotNull(vm.ReconcileCommand);
    }

    // ── In-place reconcile (the ReplaceWith-Reset trap) ────────────────────

    [Fact]
    public void MergeInto_SurvivingSession_KeepsSameRowInstance()
    {
        var vm = NewVm(ServiceWith(Session("s1", name: "App", volume: 0.5f)));
        var original = vm.Sessions.Single();

        // A fresh snapshot for the same session id with a new (externally-changed) volume.
        vm.MergeInto([Session("s1", name: "App", volume: 0.9f)]);

        Assert.Single(vm.Sessions);
        Assert.Same(original, vm.Sessions[0]); // same instance → a dragged slider survives
        Assert.Equal(0.9f, vm.Sessions[0].Volume);
    }

    [Fact]
    public void MergeInto_AddsNewAndRemovesGoneSessions()
    {
        var vm = NewVm(ServiceWith(Session("keep"), Session("gone")));

        vm.MergeInto([Session("keep"), Session("new")]);

        var ids = vm.Sessions.Select(r => r.SessionId).OrderBy(x => x).ToList();
        Assert.Equal(["keep", "new"], ids);
    }

    [Fact]
    public void MergeInto_DoesNotRaiseCollectionReset_ForInPlaceUpdate()
    {
        var vm = NewVm(ServiceWith(Session("s1", volume: 0.5f)));

        bool sawReset = false;
        ((INotifyCollectionChanged)vm.Sessions).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) sawReset = true;
        };

        // Updating an existing row must not clear/replace the collection (a Reset would
        // drop the DataGrid's per-row state / a slider mid-drag).
        vm.MergeInto([Session("s1", volume: 0.7f)]);

        Assert.False(sawReset);
        Assert.Equal(0.7f, vm.Sessions.Single().Volume);
    }

    // ── Output-device list freshness ────────────────────────────────────────

    private static AudioDevice Speakers => new("{spk}", "Speakers", IsDefault: true);
    private static AudioDevice Headset => new("{hdst}", "USB Headset", IsDefault: false);

    /// <summary>
    /// A device plugged in after the tab was opened must appear in the picker of a row that ALREADY exists.
    /// <para>Two separate reasons it did not. <c>RefreshDevicesAsync</c> was called only from
    /// <c>InitAsync</c>, so the view model's list was read once and never again — and a tab's view model
    /// lives as long as the app does. And each row received <c>OutputDevices.ToList()</c>, an immutable
    /// snapshot, so even refreshing the view model's list would not have reached an existing picker.</para>
    /// <para>Deliberately does NOT activate the tab: <c>ReconcileAsync</c> does not need it, and leaving the
    /// loops parked keeps the pass count exactly what this test drives.</para>
    /// </summary>
    [Fact]
    public async Task DeviceAddedAfterTheTabOpened_ReachesAnAlreadyExistingRowsPicker()
    {
        var service = ServiceWith(Session("s1"));
        service.IsRoutingSupported.Returns(true);
        service.GetRenderDevices().Returns(_ => new List<AudioDevice> { Speakers });
        using var vm = NewVm(service);
        var row = vm.Sessions.Single();
        Assert.Equal(["Speakers"], row.OutputDevices.Select(d => d.FriendlyName).ToArray());

        // The user plugs in a headset. Drive enough reconcile passes to cross the device-refresh cadence.
        service.GetRenderDevices().Returns(_ => new List<AudioDevice> { Speakers, Headset });
        for (var i = 0; i < 10; i++) await vm.ReconcileAsync();

        Assert.Contains("USB Headset", row.OutputDevices.Select(d => d.FriendlyName));
    }

    /// <summary>
    /// After a refresh, the row's selection must still be an element OF the current list — not a stale
    /// instance left over from the previous one.
    /// <para>That is the assertion that can actually fail, and finding it took a wrong turn worth recording.
    /// <c>ReplaceWith</c> clears and refills, and a bound <c>SelectedItem</c> survives only while an EQUAL
    /// element comes back; <c>AudioDevice</c> is a record, so its equality covers <c>IsDefault</c>. Move the
    /// Windows default and every element is unequal, so the real <c>ComboBox</c> drops the selection and the
    /// picker forgets where the user sent that app. But a unit test has no ComboBox: nothing clears the VM's
    /// property, so asserting the id alone passes with or without the fix. What DOES discriminate is
    /// membership — without the re-resolve the selected instance is absent from the new list, which is
    /// precisely the condition that makes the real control drop it.</para>
    /// </summary>
    [Fact]
    public async Task RefreshingDevices_KeepsEachRowsChoice_EvenWhenTheDefaultMoves()
    {
        var service = ServiceWith(Session("s1"));
        service.IsRoutingSupported.Returns(true);
        service.GetRenderDevices().Returns(_ => new List<AudioDevice> { Speakers, Headset });
        service.GetSessionOutputDevice("s1").Returns("{hdst}");
        using var vm = NewVm(service);
        var row = vm.Sessions.Single();
        Assert.Equal("{hdst}", row.SelectedOutputDevice?.Id);

        // Windows' default moves to the headset, so every AudioDevice record comes back UNEQUAL.
        service.GetRenderDevices().Returns(_ => new List<AudioDevice>
        {
            new("{spk}", "Speakers", IsDefault: false),
            new("{hdst}", "USB Headset", IsDefault: true),
        });
        for (var i = 0; i < 10; i++) await vm.ReconcileAsync();

        Assert.Equal("{hdst}", row.SelectedOutputDevice?.Id);
        Assert.Contains(row.SelectedOutputDevice, row.OutputDevices);
    }

    // ── The default entry means "follow the system default", in both directions ──────────
    // The picker holds real endpoints only — there is no "System default" item — so the entry flagged
    // IsDefault carries that meaning instead, and an empty endpoint id is the pivot. Reading, an empty
    // id selects that entry; writing, selecting that entry sends an empty id, which is what
    // SetPersistedDefaultAudioEndpoint treats as CLEAR THE OVERRIDE.
    //
    // Nothing pinned either direction. `value.IsDefault ? string.Empty : value.Id` reads like a needless
    // special case, and simplifying it to `value.Id` compiles, keeps every existing test green, and
    // silently removes the only way a user can stop routing an app — the override would stay in Windows
    // forever, re-pinned to whichever device is default at the time of the pick. Same
    // invisible-capability-loss class as a command bound by no XAML.
    //
    // Calls are captured inside the stub rather than asserted with Received(n), for the reason the
    // neighbouring test gives: an NSubstitute substitute is not thread-safe.

    private static IAudioMixerService RoutableService(
        List<(string Session, string Device)> writes, string? route = null)
    {
        var service = ServiceWith(Session("s1"));
        service.IsRoutingSupported.Returns(true);
        service.GetRenderDevices().Returns(_ => new List<AudioDevice> { Speakers, Headset });
        if (route is not null) service.GetSessionOutputDevice("s1").Returns(route);
        service.SetSessionOutputDevice(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            writes.Add(((string)call[0], (string)call[1]));
            return true;
        });
        return service;
    }

    /// <summary>
    /// Routing an app to a device and then putting it back on the default must CLEAR the override, not pin
    /// the app to whichever device happens to be default right now.
    /// <para>Goes headset-then-default rather than selecting the default directly: the row is built with the
    /// default already selected (the service's route-read is a stub returning empty), so assigning it again
    /// is not a property change, the write path would never run, and the test would pass while asserting
    /// nothing.</para>
    /// </summary>
    [Fact]
    public void PuttingAnAppBackOnTheDefaultDevice_ClearsTheOverride()
    {
        var writes = new List<(string Session, string Device)>();
        using var vm = NewVm(RoutableService(writes));
        var row = vm.Sessions.Single();

        row.SelectedOutputDevice = row.OutputDevices.Single(d => d.Id == "{hdst}");
        row.SelectedOutputDevice = row.OutputDevices.Single(d => d.IsDefault);

        Assert.Equal([("s1", "{hdst}"), ("s1", "")], writes);
    }

    /// <summary>
    /// A real device keeps its own endpoint id on the way to the service — the clear-the-override branch
    /// must not swallow an ordinary pick.
    /// </summary>
    [Fact]
    public void RoutingAnAppToANonDefaultDevice_SendsThatDevicesEndpointId()
    {
        var writes = new List<(string Session, string Device)>();
        using var vm = NewVm(RoutableService(writes));

        vm.Sessions.Single().SelectedOutputDevice = Headset;

        Assert.Equal([("s1", "{hdst}")], writes);
    }

    /// <summary>
    /// The read mirror: a route the service cannot resolve selects the default entry, and doing so must NOT
    /// write back. A refresh that echoed its own snapshot would re-assert a route on every pass, and on the
    /// failure branch would report a routing error the user never caused.
    /// </summary>
    [Theory]
    [InlineData("", "the route-read stub returns empty, so nothing is known about this app's route")]
    [InlineData("{unplugged}", "the persisted route names a device that is no longer present")]
    public void ARouteTheServiceCannotResolve_SelectsTheDefaultEntry_AndWritesNothing(string route, string why)
    {
        var writes = new List<(string Session, string Device)>();
        using var vm = NewVm(RoutableService(writes, route));

        var row = vm.Sessions.Single();
        Assert.True(row.SelectedOutputDevice?.IsDefault, why);
        Assert.Empty(writes);
    }

    /// <summary>
    /// Devices must NOT be re-enumerated on every reconcile pass. Enumerating endpoints is COM-heavy and
    /// reconcile runs at 1 Hz; trading a stale list for that every second would be a worse bug than the one
    /// being fixed. Counted inside the stub rather than with <c>Received(n)</c> — an NSubstitute substitute
    /// is not thread-safe, and a call count is only meaningful here because the loops are parked.
    /// </summary>
    [Fact]
    public async Task Reconcile_DoesNotReEnumerateDevicesOnEveryPass()
    {
        var service = ServiceWith(Session("s1"));
        service.IsRoutingSupported.Returns(true);
        var enumerations = 0;
        service.GetRenderDevices().Returns(_ =>
        {
            System.Threading.Interlocked.Increment(ref enumerations);
            return new List<AudioDevice> { Speakers };
        });

        using var vm = NewVm(service);
        Assert.Equal(1, enumerations);                // init reads the list exactly once

        for (var i = 0; i < 3; i++) await vm.ReconcileAsync();

        Assert.Equal(1, enumerations);                // three passes later, still no re-read
    }

    /// <summary>
    /// A device refresh must not hand the system-sounds pseudo-session a routing destination. Windows cannot
    /// reroute it, so its picker is collapsed (<c>RoutingSupported</c> is false for that row) and the
    /// construction path in <c>MergeInto</c> already gates on exactly that. The refresh path re-resolves
    /// selections and has to keep the same gate, or the row starts claiming a destination nothing can act on.
    /// </summary>
    [Fact]
    public async Task RefreshingDevices_LeavesTheSystemSoundsRowWithoutADestination()
    {
        var service = ServiceWith(Session("sys", systemSounds: true));
        service.IsRoutingSupported.Returns(true);
        service.GetRenderDevices().Returns(_ => new List<AudioDevice> { Speakers, Headset });
        using var vm = NewVm(service);
        var row = vm.Sessions.Single();
        Assert.False(row.RoutingSupported);
        Assert.Null(row.SelectedOutputDevice);

        for (var i = 0; i < 10; i++) await vm.ReconcileAsync();

        Assert.Null(row.SelectedOutputDevice);
    }

    // ── Volume / mute propagation + echo suppression ───────────────────────

    [Fact]
    public void RowVolumeChange_ByUser_CallsServiceSetVolume()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f));
        var vm = NewVm(service);
        var row = vm.Sessions.Single();

        row.Volume = 0.25f;

        service.Received(1).SetVolume("s1", 0.25f);
    }

    [Fact]
    public void RowMuteToggle_CallsServiceSetMute_AndFlipsState()
    {
        var service = ServiceWith(Session("s1", muted: false));
        var vm = NewVm(service);
        var row = vm.Sessions.Single();

        row.ToggleMuteCommand.Execute(null);

        Assert.True(row.IsMuted);
        service.Received(1).SetMute("s1", true);
    }

    /// <summary>
    /// A refused write must be reported, not swallowed. All three service writes return whether they were
    /// applied and all three results used to be discarded, so the slider sat at 20% while the app kept
    /// playing at 80% with nothing on screen to say so.
    /// <para>Reachable, not theoretical: <c>EnumerateGroupsLocked</c> releases the COM group cache before
    /// repopulating it, and a failure part way through leaves <c>_groups</c> empty — so every write for up
    /// to a second afterwards hits the dictionary-miss branch and returns false. The service's own tests
    /// (<c>Service_SetVolume_UnknownSession_IsRejected</c>) already prove that branch returns false; this
    /// pins what the USER is told when it does.</para>
    /// </summary>
    [Fact]
    public void RowVolumeChange_WhenTheServiceRefusesIt_SaysSoInTheStatus()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f, name: "Chrome"));
        service.SetVolume(Arg.Any<string>(), Arg.Any<float>()).Returns(false);
        using var vm = NewVm(service);
        var row = vm.Sessions.Single();

        row.Volume = 0.25f;

        Assert.Contains("Could not change the volume", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Chrome", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RowMuteToggle_WhenTheServiceRefusesIt_SaysSoInTheStatus()
    {
        var service = ServiceWith(Session("s1", muted: false, name: "Spotify"));
        service.SetMute(Arg.Any<string>(), Arg.Any<bool>()).Returns(false);
        using var vm = NewVm(service);
        var row = vm.Sessions.Single();

        row.ToggleMuteCommand.Execute(null);

        Assert.Contains("Could not mute Spotify", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The successful path must stay silent — a status line that fires on every slider move would bury the
    /// one that matters. This is the negative half of the pair above: without it, reporting
    /// unconditionally would pass both tests.
    /// </summary>
    [Fact]
    public void RowVolumeChange_WhenItSucceeds_LeavesTheStatusAlone()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f));
        service.SetVolume(Arg.Any<string>(), Arg.Any<float>()).Returns(true);
        using var vm = NewVm(service);
        var before = vm.StatusMessage;

        vm.Sessions.Single().Volume = 0.25f;

        Assert.Equal(before, vm.StatusMessage);
    }

    /// <summary>
    /// A refresh-driven write must not report either: <c>ApplyUpdate</c> sets Volume/IsMuted from the
    /// service's own snapshot behind the echo guard, so it never calls the service at all — and a status
    /// line appearing once a second from a reconcile pass nobody triggered would be pure noise.
    /// </summary>
    [Fact]
    public void ExternalUpdate_DoesNotReportAFailure_EvenWhenWritesWouldBeRefused()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f));
        service.SetVolume(Arg.Any<string>(), Arg.Any<float>()).Returns(false);
        service.SetMute(Arg.Any<string>(), Arg.Any<bool>()).Returns(false);
        using var vm = NewVm(service);
        var before = vm.StatusMessage;

        vm.Sessions.Single().ApplyUpdate(Session("s1", volume: 0.8f, muted: true));

        Assert.Equal(before, vm.StatusMessage);
    }

    [Fact]
    public void MergeInto_ExternalUpdate_DoesNotEchoBackToService()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f, muted: false));
        var vm = NewVm(service);
        service.ClearReceivedCalls();

        // An external change surfaced by a refresh must update the row WITHOUT calling the
        // service setters again (the re-entrancy guard in ApplyUpdate).
        vm.MergeInto([Session("s1", volume: 0.2f, muted: true)]);

        var row = vm.Sessions.Single();
        Assert.Equal(0.2f, row.Volume);
        Assert.True(row.IsMuted);
        service.DidNotReceive().SetVolume(Arg.Any<string>(), Arg.Any<float>());
        service.DidNotReceive().SetMute(Arg.Any<string>(), Arg.Any<bool>());
    }

    // ── Peak meter update path ─────────────────────────────────────────────

    [Fact]
    public async Task UpdatePeaks_WritesEachRowPeak_FromService()
    {
        var service = ServiceWith(Session("s1"), Session("s2"));
        Peaks(service, ("s1", 0.6f), ("s2", 0.2f));
        using var vm = NewVm(service);
        // Active, because that is the only state in which the meters are read: MainWindowViewModel calls
        // SetActive(content, IsWindowVisible) the moment a tab's VM is materialised, so a peak sample never
        // lands on an inactive tab in production. Sampling with IsActive false is a state the app does not
        // have, and asserting levels there asks the post-await guard to write onto rows it just zeroed.
        vm.IsActive = true;

        await vm.UpdatePeaksAsync(CancellationToken.None);

        Assert.Equal(0.6f, vm.Sessions.Single(r => r.SessionId == "s1").PeakLevel);
        Assert.Equal(0.2f, vm.Sessions.Single(r => r.SessionId == "s2").PeakLevel);
    }

    /// <summary>
    /// Every visible row must be read in ONE service call, not one call per row.
    /// <para>The meters refresh 20 times a second. Per-row calls each took the service lock and
    /// marshalled a cross-apartment COM call, so ten apps playing meant ~200 COM transitions a second on
    /// the UI thread — and one tick in twenty blocked there behind the 1 Hz reconcile, which holds the
    /// same lock while enumerating every session. Counting the calls is what pins that: a refactor back
    /// to a per-row loop would still produce correct peaks and pass every other test in this file.</para>
    /// </summary>
    [Fact]
    public async Task UpdatePeaks_ReadsEveryRowInOneServiceCall()
    {
        var service = ServiceWith(Session("s1"), Session("s2"), Session("s3"));

        // Recorded by the stubs themselves into thread-safe collections, and asserted on THOSE rather than
        // through NSubstitute's Received(). Two reasons, both learned the hard way:
        //
        // 1. Activating the tab releases the peak loop, which samples every 50 ms — so an exact
        //    Received(1) is not an observable property, and a second sample landing first failed a
        //    release. What the name actually claims is "one call carried every row", not "one call total".
        // 2. An NSubstitute substitute is NOT thread-safe. With the loop recording calls concurrently, a
        //    Received(...) assertion enumerates a collection that is being mutated and reports "received
        //    no matching calls" even when a matching call was made. That is exactly what happened: this
        //    test passed 8/8 alone and failed inside the full class, where the slower run gives the loop
        //    time to tick during the assertion.
        var batchSizes = new System.Collections.Concurrent.ConcurrentBag<int>();
        var perRowReads = new System.Collections.Concurrent.ConcurrentBag<string>();
        var levels = new Dictionary<string, float>(StringComparer.Ordinal)
        { ["s1"] = 0.1f, ["s2"] = 0.2f, ["s3"] = 0.3f };
        service.GetPeaks(Arg.Any<IEnumerable<string>>()).Returns(call =>
        {
            var ids = ((IEnumerable<string>)call[0]).ToArray();
            batchSizes.Add(ids.Length);
            return ids.ToDictionary(id => id, id => levels.GetValueOrDefault(id), StringComparer.Ordinal);
        });
        service.GetPeak(Arg.Any<string>()).Returns(call =>
        {
            perRowReads.Add((string)call[0]);
            return levels.GetValueOrDefault((string)call[0]);
        });

        using var vm = NewVm(service);
        vm.IsActive = true;

        await vm.UpdatePeaksAsync(CancellationToken.None);

        // Every batch that happened covered all three rows, and no row was ever read on its own. A per-row
        // refactor fails both halves: its batches would be size 1, and GetPeak would be called.
        Assert.NotEmpty(batchSizes);
        Assert.All(batchSizes, size => Assert.Equal(3, size));
        Assert.Empty(perRowReads);
    }

    /// <summary>
    /// A hidden tab must PARK the peak loop, not tick-and-skip.
    /// <para>The <c>DispatcherTimer</c> this replaced was genuinely stopped on deactivate. A loop that
    /// only checks <c>IsActive</c> after its delay still wakes 20 times a second and, because the
    /// continuation resumes on the captured context, queues 20 work items a second onto the Dispatcher
    /// for the whole life of the app once the tab has been opened once — measurable overhead on every
    /// other tab, from a tab nobody is looking at. The gate is what restores timer parity, so it is
    /// asserted directly: hidden means the loop is waiting on an incomplete task.</para>
    /// </summary>
    [Fact]
    public void HiddenTab_ParksThePeakLoop_RatherThanTickingAndSkipping()
    {
        // Disposed, and left INACTIVE, deliberately. Ending on IsActive = true without disposing left this
        // VM's peak loop waking ~20 times a second against the stub for the remainder of the test process
        // — the exact cost this test is named after, reintroduced by the test itself. It also kept the
        // process alive past the end of the run, locking its own output assemblies against the next build.
        using var vm = NewVm(ServiceWith(Session("s1")));

        var gate = typeof(AudioMixerViewModel)
            .GetField("_activated", BindingFlags.NonPublic | BindingFlags.Instance)!;

        vm.IsActive = true;
        Assert.True(((TaskCompletionSource)gate.GetValue(vm)!).Task.IsCompleted);

        vm.IsActive = false;
        Assert.False(((TaskCompletionSource)gate.GetValue(vm)!).Task.IsCompleted);

        // Re-showing the tab must release it again — a one-shot gate would leave the meters dead
        // after the first time the user navigated away.
        vm.IsActive = true;
        Assert.True(((TaskCompletionSource)gate.GetValue(vm)!).Task.IsCompleted);

        vm.IsActive = false;
    }

    [Fact]
    public async Task Deactivating_ClearsPeaks_SoHiddenMeterDoesNotFreezeLit()
    {
        var service = ServiceWith(Session("s1"));
        Peaks(service, ("s1", 0.9f));
        using var vm = NewVm(service);
        vm.IsActive = true;
        await vm.UpdatePeaksAsync(CancellationToken.None);
        Assert.Equal(0.9f, vm.Sessions.Single().PeakLevel);

        // Leaving the tab zeroes the bars (no stale lit level); the loop itself skips while hidden.
        vm.IsActive = false;

        Assert.Equal(0f, vm.Sessions.Single().PeakLevel);
    }

    /// <summary>
    /// The sibling of <see cref="UpdatePeaks_WhenDisposedMidSample_DoesNotWriteBackOntoTheTornDownRows"/>,
    /// and the half of that guard that was missing. Hiding the tab — or minimising the window, which
    /// routes to the same <c>SetActive(false)</c> — zeroes every row; the sample already in flight then
    /// resumed and wrote the old levels straight back. The loop parks on its next iteration, so those
    /// stale bars survive for as long as the tab stays hidden: on re-show the user sees a lit meter from
    /// minutes ago until a fresh sample lands.
    /// <para>Both waits are BOUNDED for the same reason as the dispose test: on the correct shape neither
    /// timeout is reached, but a wrong shape would hang the whole suite instead of failing one test.</para>
    /// </summary>
    [Fact]
    public async Task UpdatePeaks_WhenTheTabIsHiddenMidSample_DoesNotRelightTheZeroedRows()
    {
        var service = ServiceWith(Session("s1"));
        using var vm = NewVm(service);
        vm.IsActive = true;

        var sampleReached = new TaskCompletionSource();
        var releaseSample = new TaskCompletionSource();
        service.GetPeaks(Arg.Any<IEnumerable<string>>()).Returns(call =>
        {
            sampleReached.TrySetResult();
            releaseSample.Task.Wait(TimeSpan.FromSeconds(10));
            return ((IEnumerable<string>)call[0]).ToDictionary(id => id, _ => 0.8f, StringComparer.Ordinal);
        });
        // Held open the same way, so this test is about the post-await guard and not about which call
        // shape reads the levels — see the dispose test for why that distinction matters.
        service.GetPeak(Arg.Any<string>()).Returns(_ =>
        {
            sampleReached.TrySetResult();
            releaseSample.Task.Wait(TimeSpan.FromSeconds(10));
            return 0.8f;
        });

        try
        {
            var inFlight = vm.UpdatePeaksAsync(CancellationToken.None);
            await sampleReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            vm.IsActive = false;             // zeroes the rows while the sample is outstanding
            releaseSample.SetResult();
            await inFlight;
        }
        finally
        {
            releaseSample.TrySetResult();
        }

        Assert.Equal(0f, vm.Sessions.Single().PeakLevel);
    }

    // ── Empty-state flag ───────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_TogglesHasSessions_WithMembership()
    {
        var service = Substitute.For<IAudioMixerService>();
        service.GetSessions().Returns(_ => new List<AudioSessionInfo> { Session("s1") });
        var vm = NewVm(service);
        Assert.True(vm.HasSessions);

        // Now the app stopped playing — the next reconcile empties the list.
        service.GetSessions().Returns(_ => new List<AudioSessionInfo>());
        await vm.ReconcileAsync();

        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasSessions);
    }

    // ── Contract at the seam: system-sounds flagged, expired never surfaces ─

    [Fact]
    public void SystemSoundsRow_UsesWindowsIcon_AndIsFlagged()
    {
        var vm = NewVm(ServiceWith(Session("sys", pid: 0, name: "System Sounds", systemSounds: true)));
        var row = vm.Sessions.Single();
        Assert.True(row.IsSystemSounds);
        // System sounds sort after apps (stable order) — with one row it's simply present.
        Assert.Equal("System Sounds", row.DisplayName);
    }

    [Fact]
    public void Ordering_AppsAlphabetical_SystemSoundsLast()
    {
        var vm = NewVm(ServiceWith(
            Session("sys", pid: 0, name: "System Sounds", systemSounds: true),
            Session("z", name: "Zoom"),
            Session("a", name: "Audacity")));

        var order = vm.Sessions.Select(r => r.DisplayName).ToList();
        Assert.Equal(["Audacity", "Zoom", "System Sounds"], order);
    }

    // ── Mid-drag guard: a refresh must not clobber the slider being dragged ─

    [Fact]
    public void MergeInto_WhileUserDragging_DoesNotOverwriteVolume()
    {
        var service = ServiceWith(Session("s1", volume: 0.5f));
        var vm = NewVm(service);
        var row = vm.Sessions.Single();
        service.ClearReceivedCalls();

        // User grabs the thumb and drags to 0.9 (view sets IsUserAdjusting during the drag).
        row.IsUserAdjusting = true;
        row.Volume = 0.9f;
        // The live drag still propagates the user's value to the service (the guard only blocks
        // REFRESH writes, never the user's own change).
        service.Received(1).SetVolume("s1", 0.9f);

        // A reconcile tick arrives carrying a stale snapshot (0.5). It must NOT snap the thumb back.
        vm.MergeInto([Session("s1", volume: 0.5f)]);
        Assert.Equal(0.9f, row.Volume);

        // After the drag ends, a later refresh applies external changes normally again.
        row.IsUserAdjusting = false;
        vm.MergeInto([Session("s1", volume: 0.3f)]);
        Assert.Equal(0.3f, row.Volume);
    }

    // ── Identity refresh: a row that rebinds to a different exe re-extracts its icon ─

    [Fact]
    public void MergeInto_IdentityChange_UpdatesProcessId()
    {
        var vm = NewVm(ServiceWith(Session("k", pid: 100, name: "AppA")));
        var row = vm.Sessions.Single();
        Assert.Equal(100u, row.ProcessId);

        // Same stable key, but the resolved process changed (new pid + exe). The row is kept
        // (in place) and its identity fields are refreshed rather than showing stale ones.
        vm.MergeInto([new AudioSessionInfo("k", 200, "AppB", ExePath: "", 0.5f, false, AudioSessionState.Active, false, 0f)]);

        Assert.Same(row, vm.Sessions.Single());
        Assert.Equal(200u, row.ProcessId);
        Assert.Equal("AppB", row.DisplayName);
    }

    // ── Deterministic disposal ─────────────────────────────────────────────

    [Fact]
    public async Task Dispose_RacingInit_LeavesNoLiveReconcileLoop()
    {
        // Dispose the VM BEFORE its async init has completed. Because the CTS is created before
        // the first await, Dispose cancels+disposes+nulls it, and InitAsync's post-await guard
        // sees the null/cancelled token and starts NO reconcile loop.
        var service = ServiceWith(Session("s1"));
        var presets = new VolumePresetService(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests", System.Guid.NewGuid().ToString("N")));
        var vm = new AudioMixerViewModel(service, presets);

        vm.Dispose();                    // races the fire-and-forget InitAsync
        await vm.InitializationComplete; // must complete, not fault
        vm.Dispose();                    // still idempotent after init resolves

        Assert.True(vm.InitializationComplete.IsCompletedSuccessfully);

        // Discriminating assertion (pins the fix, not just "init ran"): after a disposed init the
        // CTS field is null — the loop is not running. In the OLD assign-after-await ordering,
        // InitAsync would recreate a LIVE CTS after Dispose ran, leaving this non-null (an orphan
        // loop). Reflection mirrors DriversViewModelTests' _cts pattern.
        var cts = typeof(AudioMixerViewModel)
            .GetField("_reconcileCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm);
        Assert.Null(cts);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndZeroesTheMeter()
    {
        var service = ServiceWith(Session("s1"));
        Peaks(service, ("s1", 0.7f));
        var vm = NewVm(service);
        vm.IsActive = true;
        await vm.UpdatePeaksAsync(CancellationToken.None); // light the meter so zeroing is observable
        Assert.Equal(0.7f, vm.Sessions.Single().PeakLevel);

        vm.Dispose();
        // Idempotent: the derived Dispose(bool) body is itself safe to run twice (the CTS is
        // nulled after disposal and the peaks are simply re-zeroed) — not merely because the base
        // _disposed guard blocks the base body.
        vm.Dispose();

        // Behavioral post-condition: Dispose stopped the meter and zeroed the lit bar (it must
        // not leave a stale level frozen on screen).
        Assert.Equal(0f, vm.Sessions.Single().PeakLevel);
    }

    /// <summary>
    /// A peak sample that was already in flight when Dispose ran must not write its result back.
    /// <para>The sample now genuinely yields (the COM work runs on a worker thread), so Dispose — which
    /// cancels the shared token and zeroes every meter — can land while one is outstanding. The token
    /// only prevents a sample from STARTING; the continuation still resumes on the UI thread. Without the
    /// post-await guard it re-lights meters Dispose had just cleared, on a torn-down view model. Same
    /// defect the Bandwidth Monitor and Resource History fixes pinned (v1.63.1, v1.64.1).</para>
    /// </summary>
    [Fact]
    public async Task UpdatePeaks_WhenDisposedMidSample_DoesNotWriteBackOntoTheTornDownRows()
    {
        var service = ServiceWith(Session("s1"));
        var vm = NewVm(service);
        vm.IsActive = true;

        // Hold the sample open, dispose while it is outstanding, then let it complete.
        //
        // Both waits below are BOUNDED, and the bound is load-bearing for the test suite rather than for
        // this test: on the correct shape neither timeout is ever reached (the stub is entered on the
        // worker thread within microseconds and released explicitly), so no assertion here depends on
        // wall-clock timing. They exist because a WRONG shape would otherwise deadlock the whole run
        // instead of failing one test — code that samples via a different call never enters the stub, so
        // `sampleReached` is never set; and code that samples on the CALLING thread parks the test itself
        // inside the stub, before `inFlight` is even assigned. An unbounded wait in either place turns a
        // red test into a hung suite, which reports as nothing at all.
        var sampleReached = new TaskCompletionSource();
        var releaseSample = new TaskCompletionSource();
        service.GetPeaks(Arg.Any<IEnumerable<string>>()).Returns(call =>
        {
            sampleReached.TrySetResult();
            releaseSample.Task.Wait(TimeSpan.FromSeconds(10));
            return ((IEnumerable<string>)call[0]).ToDictionary(id => id, _ => 0.8f, StringComparer.Ordinal);
        });
        // Hold the single-id read open the same way, so this test is about the post-await GUARD and
        // nothing else: a shape that samples per row still reaches the dispose window and still gets a
        // non-zero level to (wrongly) write back. Without this the test would go red on any refactor
        // that stops calling GetPeaks — duplicating what the call-count test already pins, for the wrong
        // reason.
        service.GetPeak(Arg.Any<string>()).Returns(_ =>
        {
            sampleReached.TrySetResult();
            releaseSample.Task.Wait(TimeSpan.FromSeconds(10));
            return 0.8f;
        });

        try
        {
            var inFlight = vm.UpdatePeaksAsync(CancellationToken.None);
            await sampleReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            vm.Dispose();
            releaseSample.SetResult();
            await inFlight;
        }
        finally
        {
            // Never leave the stub's thread parked on releaseSample if the wait above timed out.
            releaseSample.TrySetResult();
        }

        Assert.Equal(0f, vm.Sessions.Single().PeakLevel);
    }

    // ── Preset deletion is confirmed (the file is rewritten immediately) ────

    [Fact]
    public async Task DeletePreset_WhenDeclined_KeepsThePresetOnDisk()
    {
        // VolumePresetService.Delete() calls Persist() -> File.WriteAllText straight away, so a
        // stray click was unrecoverable. Answering "No" must leave both the in-memory list and
        // the file exactly as they were.
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests", System.Guid.NewGuid().ToString("N"));
        var presets = new VolumePresetService(dir);
        var service = ServiceWith(Session("s1", pid: 10, name: "Chrome", volume: 0.8f));
        var vm = new AudioMixerViewModel(service, presets);
        await vm.InitializationComplete;

        // Seed through the service rather than SavePresetCommand: that command derives entries from
        // each row's exe path, and the shared Session(...) helper leaves ExePath empty, so it would
        // correctly no-op ("No apps to save into a preset right now") and save nothing. What is under
        // test here is the DELETE gate, so the preset is put on disk directly.
        presets.Save(new VolumePreset("Movie night",
            [new VolumePresetEntry("chrome.exe", "Chrome", 0.8f, false)]));
        vm.Presets.ReplaceWith(presets.Load());
        Assert.Single(vm.Presets);
        vm.SelectedPreset = vm.Presets[0];

        using var answer = new DialogAnswer(false);
        vm.DeletePresetCommand.Execute(null);

        Assert.Equal(1, answer.Calls);                       // the gate really ran
        Assert.Single(vm.Presets);                           // still in the list
        Assert.Equal("Movie night", vm.Presets[0].Name);
        Assert.Single(presets.Load());                       // and still on disk
    }

    [Fact]
    public async Task DeletePreset_WhenConfirmed_RemovesIt()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "SysManagerTests", System.Guid.NewGuid().ToString("N"));
        var presets = new VolumePresetService(dir);
        var service = ServiceWith(Session("s1", pid: 10, name: "Chrome", volume: 0.8f));
        var vm = new AudioMixerViewModel(service, presets);
        await vm.InitializationComplete;

        // Seeded directly for the same reason as the declined case above.
        presets.Save(new VolumePreset("Movie night",
            [new VolumePresetEntry("chrome.exe", "Chrome", 0.8f, false)]));
        vm.Presets.ReplaceWith(presets.Load());
        vm.SelectedPreset = vm.Presets[0];

        using var _ = new DialogAnswer(true);
        vm.DeletePresetCommand.Execute(null);

        Assert.Empty(vm.Presets);
        Assert.Empty(presets.Load());
        Assert.Null(vm.SelectedPreset);
    }

    // ── Input validation at the trust boundary (real service, no COM) ──────

    [Fact]
    public void Service_SetVolume_UnknownSession_IsRejected()
    {
        // AudioMixerService rejects a set for a session it isn't tracking (dictionary miss)
        // before any COM call — a genuine trust-boundary check that needs no audio hardware.
        using var service = new AudioMixerService();
        Assert.False(service.SetVolume("no-such-session", 0.5f));
        Assert.False(service.SetMute("no-such-session", true));
        Assert.Equal(0f, service.GetPeak("no-such-session"));
    }

    [Fact]
    public void Service_SetVolume_AfterDispose_IsRejected()
    {
        var service = new AudioMixerService();
        service.Dispose();
        // A disposed service accepts no writes and reads a zero peak — never throws.
        Assert.False(service.SetVolume("s1", 0.5f));
        Assert.False(service.SetMute("s1", true));
        Assert.Equal(0f, service.GetPeak("s1"));
    }

    // ── StripStreamGuid: the load-bearing PID-reuse group-key derivation ───

    [Theory]
    // Two streams of one app share everything before the final "%b<guid>" → same group key.
    [InlineData(@"{0.0.0.00000000}.{guid}|\Device\...|MyApp%b{stream-A}", @"{0.0.0.00000000}.{guid}|\Device\...|MyApp")]
    [InlineData(@"{0.0.0.00000000}.{guid}|\Device\...|MyApp%b{stream-B}", @"{0.0.0.00000000}.{guid}|\Device\...|MyApp")]
    // No "%b" marker → returned unchanged.
    [InlineData("plain-identifier-no-marker", "plain-identifier-no-marker")]
    // Marker at the very start (index 0) → not stripped (marker > 0 guard), returned unchanged.
    [InlineData("%bleadingmarker", "%bleadingmarker")]
    public void StripStreamGuid_DropsTrailingStreamGuid(string input, string expected)
    {
        Assert.Equal(expected, AudioMixerService.StripStreamGuid(input));
    }

    [Fact]
    public void StripStreamGuid_TwoStreamsOfSameApp_ProduceSameKey()
    {
        const string prefix = @"{0.0.0.00000000}.{abc}|\Device\Harddisk\chrome.exe";
        var a = AudioMixerService.StripStreamGuid(prefix + "%b{11111111-1111-1111-1111-111111111111}");
        var b = AudioMixerService.StripStreamGuid(prefix + "%b{22222222-2222-2222-2222-222222222222}");
        Assert.Equal(a, b); // both collapse to one row
        Assert.Equal(prefix, a);
    }

    [Fact]
    public void StripStreamGuid_EmptyOrNull_ReturnedAsIs()
    {
        Assert.Equal("", AudioMixerService.StripStreamGuid(""));
        Assert.Null(AudioMixerService.StripStreamGuid(null!));
    }

    // ── Mid-adjust guard: drag OR keyboard-focus (regression: drag-then-arrow-key) ─

    // dragging=false + focused=true is the Audit #3 regression case: a mouse drag has ENDED but
    // the slider still holds keyboard focus (drag-then-arrow-key fine-tune), so it must STILL read
    // as adjusting — a reconcile tick must not clobber the value. DragCompleted clearing the flag
    // unconditionally (the old bug) would make this false.
    [Theory]
    [InlineData(false, false, false)] // idle → not adjusting
    [InlineData(true, false, true)]   // mouse dragging → adjusting
    [InlineData(false, true, true)]   // focused only (arrow-key / track-click / post-drag) → adjusting
    [InlineData(true, true, true)]    // both → adjusting
    public void ComputeAdjusting_IsTrueWhileDraggingOrKeyboardFocused(bool dragging, bool focused, bool expected)
    {
        Assert.Equal(expected, AudioMixerView.ComputeAdjusting(dragging, focused));
    }

    [Fact]
    public void ApplyAdjustingState_DragEndsWhileStillFocused_StaysAdjusting()
    {
        // Drives the actual guard WIRING (not just the terminal OR) through the exact event
        // sequence that held the Audit-3 bug: GotKeyboardFocus → DragStarted → DragCompleted,
        // with focus never lost. The row must remain adjusting after the drag ends, so a reconcile
        // cannot clobber a subsequent arrow-key nudge. Unconditionally clearing on DragCompleted
        // (the old bug) would flip this to false.
        var vm = NewVm(ServiceWith(Session("s1")));
        var row = vm.Sessions.Single();

        AudioMixerView.ApplyAdjustingState(row, dragging: false, keyboardFocused: true);  // GotKeyboardFocus
        AudioMixerView.ApplyAdjustingState(row, dragging: true, keyboardFocused: true);   // DragStarted
        AudioMixerView.ApplyAdjustingState(row, dragging: false, keyboardFocused: true);  // DragCompleted (still focused)
        Assert.True(row.IsUserAdjusting); // the regression pin

        AudioMixerView.ApplyAdjustingState(row, dragging: false, keyboardFocused: false); // LostKeyboardFocus
        Assert.False(row.IsUserAdjusting); // fully released → refreshes resume
    }

    [Fact]
    public void ApplyAdjustingState_NullRow_DoesNotThrow()
    {
        // Recycled/blank DataContext must be a safe no-op (the handlers pass DataContext-as-row).
        AudioMixerView.ApplyAdjustingState(null, dragging: true, keyboardFocused: true);
    }

    // ── Progress feedback (regression) ──
    // AudioMixerView.xaml binds a progress bar to IsBusy and the sidebar spinner reads the same flag,
    // but this VM never assigned it — so the initial COM enumeration of sessions and render devices
    // showed nothing. Only the FIRST load reports progress; the 1 s reconcile loop deliberately does
    // not, or the bar would strobe for as long as the tab stays open.

    [Fact]
    public async Task AfterTheInitialLoad_TheBusyFlagIsClear()
    {
        // The load raises the flag; it must be released when init finishes, or the bar would spin
        // forever on a freshly opened tab. (The raise itself happens inside the constructor, before a
        // test can subscribe — the reconcile-loop test below covers the "never set again" half.)
        var vm = NewVm(ServiceWith(Session("s1")));

        await vm.InitializationComplete;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task TheReconcileLoop_DoesNotStrobeTheProgressBar()
    {
        // Deliberate asymmetry, pinned so a later refactor does not "fix" it back: a per-reconcile
        // flag would flash the bar on and off every second for as long as the tab is open.
        var vm = NewVm(ServiceWith(Session("s1")));

        var seen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsBusy)) seen.Add(vm.IsBusy);
        };

        await vm.ReconcileAsync();

        Assert.Empty(seen);
        Assert.False(vm.IsBusy);
    }

    // ── Saving over an existing preset confirms, like deleting one already did ─────────────────────
    //
    // Save() rewrites the presets file on disk immediately, so overwriting destroys exactly the same data
    // as deleting, with the same absence of undo — and DeletePreset confirmed for precisely that reason
    // while SavePreset did not. Its own doc comment stated the behaviour plainly ("Overwrites a
    // same-named preset") without the UI ever telling the user.

    [Fact]
    public void SavePreset_WithANewName_DoesNotInterrupt()
    {
        // A new name is not destructive, so it must NOT prompt — otherwise the confirmation becomes
        // noise that people learn to dismiss without reading.
        var vm = NewVm(ServiceWith(SessionWithExe(volume: 0.8f)));
        vm.NewPresetName = "Gaming";

        using var dialog = new DialogAnswer(confirm: false);
        vm.SavePresetCommand.Execute(null);

        Assert.Equal(0, dialog.Calls);
        Assert.Contains(vm.Presets, p => p.Name == "Gaming");
    }

    [Fact]
    public void SavePreset_OverAnExistingName_WhenDeclined_KeepsTheOldPreset()
    {
        var vm = NewVm(ServiceWith(SessionWithExe(volume: 0.8f)));
        vm.NewPresetName = "Gaming";
        using (new DialogAnswer(confirm: false)) vm.SavePresetCommand.Execute(null);
        var originalVolume = Assert.Single(vm.Presets, p => p.Name == "Gaming").Entries[0].Volume;

        // Change the live volume, then try to save over the same name and decline.
        vm.Sessions[0].Volume = 0.2f;
        vm.NewPresetName = "Gaming";

        using var dialog = new DialogAnswer(confirm: false);
        vm.SavePresetCommand.Execute(null);

        Assert.Equal(1, dialog.Calls);   // the gate ran…
        var after = Assert.Single(vm.Presets, p => p.Name == "Gaming");
        Assert.Equal(originalVolume, after.Entries[0].Volume);   // …and the old values survived
    }

    [Fact]
    public void SavePreset_OverAnExistingName_WhenConfirmed_Overwrites()
    {
        // The other half: the gate must not have turned saving into a no-op.
        var vm = NewVm(ServiceWith(SessionWithExe(volume: 0.8f)));
        vm.NewPresetName = "Gaming";
        using (new DialogAnswer(confirm: false)) vm.SavePresetCommand.Execute(null);

        vm.Sessions[0].Volume = 0.2f;
        vm.NewPresetName = "Gaming";

        using var dialog = new DialogAnswer(confirm: true);
        vm.SavePresetCommand.Execute(null);

        Assert.Equal(1, dialog.Calls);
        var after = Assert.Single(vm.Presets, p => p.Name == "Gaming");
        Assert.Equal(0.2f, after.Entries[0].Volume, precision: 3);
    }

    [Fact]
    public void SavePreset_OverAnExistingName_MatchesCaseInsensitively()
    {
        // Windows users do not distinguish "Gaming" from "gaming", and the presets file does not either —
        // saving as "gaming" replaces "Gaming", so it has to prompt like any other overwrite.
        var vm = NewVm(ServiceWith(SessionWithExe(volume: 0.8f)));
        vm.NewPresetName = "Gaming";
        using (new DialogAnswer(confirm: false)) vm.SavePresetCommand.Execute(null);

        vm.NewPresetName = "gaming";

        using var dialog = new DialogAnswer(confirm: false);
        vm.SavePresetCommand.Execute(null);

        Assert.Equal(1, dialog.Calls);
    }
}
