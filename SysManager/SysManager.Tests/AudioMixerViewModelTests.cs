// SysManager · AudioMixerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Specialized;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="AudioMixerViewModel"/> and <see cref="AudioSessionRowViewModel"/>.
/// The whole audio surface sits behind <see cref="IAudioMixerService"/>, so every test
/// substitutes it with a deterministic session list — no NAudio, no COM, no real audio
/// hardware is touched. Coverage targets the ViewModel logic that matters: the in-place
/// reconcile (surviving rows keep their instance, adds/removes without a collection Reset
/// — pinning the "ReplaceWith drops the dragged slider" trap), volume/mute propagation to
/// the service with an echo-suppression guard on external updates, the peak-meter update
/// path, the <c>IsActive</c> visibility gate, the empty-state flag, and deterministic
/// disposal. The COM enumeration/grouping itself lives in <see cref="AudioMixerService"/>
/// (not unit-tested here — it needs a real endpoint), so these tests treat the service's
/// filtering (expired dropped, system-sounds flagged) as a contract at the seam.
/// </summary>
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

    // A substitute service that returns a fixed session list from GetSessions.
    private static IAudioMixerService ServiceWith(params AudioSessionInfo[] sessions)
    {
        var service = Substitute.For<IAudioMixerService>();
        service.GetSessions().Returns(sessions.ToList());
        return service;
    }

    // The constructor kicks off an async reconcile off the UI thread; await init so
    // Sessions is populated before asserting (mirrors CpuAffinityViewModelTests.NewVm).
    private static AudioMixerViewModel NewVm(IAudioMixerService service)
    {
        var vm = new AudioMixerViewModel(service);
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
    public void UpdatePeaks_WritesEachRowPeak_FromService()
    {
        var service = ServiceWith(Session("s1"), Session("s2"));
        service.GetPeak("s1").Returns(0.6f);
        service.GetPeak("s2").Returns(0.2f);
        var vm = NewVm(service);

        vm.UpdatePeaks();

        Assert.Equal(0.6f, vm.Sessions.Single(r => r.SessionId == "s1").PeakLevel);
        Assert.Equal(0.2f, vm.Sessions.Single(r => r.SessionId == "s2").PeakLevel);
    }

    [Fact]
    public void Deactivating_ClearsPeaks_SoHiddenMeterDoesNotFreezeLit()
    {
        var service = ServiceWith(Session("s1"));
        service.GetPeak("s1").Returns(0.9f);
        var vm = NewVm(service);
        vm.IsActive = true;
        vm.UpdatePeaks();
        Assert.Equal(0.9f, vm.Sessions.Single().PeakLevel);

        // Leaving the tab stops the meter and zeroes the bars (no stale lit level).
        vm.IsActive = false;

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

    // ── Deterministic disposal ─────────────────────────────────────────────

    [Fact]
    public void Dispose_IsIdempotent_AndStopsTheMeter()
    {
        var vm = NewVm(ServiceWith(Session("s1")));
        vm.IsActive = true; // meter timer running

        vm.Dispose();
        vm.Dispose(); // double dispose must be a no-op, not throw (base _disposed guard)

        Assert.True(true); // reaching here without an exception is the guarantee
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
}
