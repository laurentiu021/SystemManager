// SysManager · UndoChangesServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UndoChangesService"/> (#1525): what each kept copy is listed as, when it is not listed, and
/// what putting it back does and says.
/// </summary>
/// <remarks>
/// Every store is a real one over a temp folder or a throwaway registry key, so the comparisons are the real ones.
/// The calls that would read or change this PC — the current Performance Mode settings, its restore, sc.exe, the
/// environment broadcast, the Gaming Profile service — are stand-ins, and so nothing here changes the machine running
/// the suite.
/// </remarks>
// Serialized: puts back through OperationLockService.Instance, and some tests hold its lock. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public sealed class UndoChangesServiceTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 10, 5, 19, 40, 0, TimeSpan.Zero);

    private static PerformanceService.OriginalSnapshot Original() => new(
        PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
        PowerPlanName: "Balanced",
        UiEffectsEnabled: true,
        GameModeEnabled: true,
        XboxGameBarEnabled: true,
        XboxGameDvrEnabled: true,
        GpuDynamicPstate: true,
        ProcessorMinPercentAc: 5,
        NvidiaSubKey: null,
        CapturedAtUtc: Recorded);

    private static PerformanceService.OriginalSnapshot Tweaked() => Original() with
    {
        PowerPlanGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61",
        PowerPlanName = "Ultimate Performance",
        UiEffectsEnabled = false,
        CapturedAtUtc = null,
    };

    /// <summary>The stores and stand-ins one service is built over, all thrown away on dispose.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("UndoChanges_");

        public Rig()
        {
            Runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(0);
            // No restore points unless a test lists some; a real runner always answers with a collection.
            Runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
                .Returns(new Collection<PSObject>());
            Performance = new PerformanceService(Runner, new RestorePointService(Runner), Dir);
            Ledger = new ServiceStartupLedgerService(Dir);
            HostsPath = Path.Combine(Dir, "hosts");
            Hosts = new HostsFileService(HostsPath);
            RestorePoints = new RestorePointService(Runner);
            Signal.PutBack += Raised.Add;
            RestorePerformance = snapshot =>
            {
                Restored.Add(snapshot);
                return Task.FromResult(Performance.DeleteSnapshot());
            };
            // Not left on unless a test says so. A substitute's own answer would be null, which is "could not be read".
            Gaming.HasPendingRecovery.Returns(false);
            // The card a record names is still there, set as Now() has it, unless a test says otherwise.
            Graphics = subKey => Now() is var now && now.NvidiaSubKey == subKey
                ? new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Present, now.GpuDynamicPstate)
                : new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Gone, DynamicPstate: false);
        }

        public string Dir => _dir.FullName;
        public string SnapshotPath => Path.Combine(Dir, "performance-snapshot.json");
        public IPowerShellRunner Runner { get; } = Substitute.For<IPowerShellRunner>();
        public PerformanceService Performance { get; }
        public ServiceStartupLedgerService Ledger { get; }
        public string HostsPath { get; }
        public HostsFileService Hosts { get; }
        public RedirectedEnvironment Environment { get; } = new();
        public IGamingProfileService Gaming { get; } = Substitute.For<IGamingProfileService>();
        public ISettingsWatchdogService Watchdog { get; } = Substitute.For<ISettingsWatchdogService>();
        public RestorePointService RestorePoints { get; }
        public PutBackSignal Signal { get; } = new();
        public List<UndoChangeKind> Raised { get; } = [];
        public bool Elevated { get; set; }
        public Dictionary<string, ServiceEntry> ServicesOnPc { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the Performance Mode settings are now; the original unless a test says otherwise.</summary>
        public Func<PerformanceService.OriginalSnapshot> Now { get; set; } = Original;

        /// <summary>What has become of the graphics card a record names, and how it is set now.</summary>
        public Func<string, UndoChangesService.GraphicsNow> Graphics { get; set; }
        public List<PerformanceService.OriginalSnapshot> Restored { get; } = [];
        public Func<PerformanceService.OriginalSnapshot, Task<bool>> RestorePerformance { get; set; }

        /// <summary>The environment restore, or null for the real one over the throwaway registry keys.</summary>
        public Func<EnvironmentVariableService.RestoreResult>? RestoreEnvironment { get; set; }
        public int Announced { get; private set; }

        public UndoChangesService Service => new(
            Performance, Ledger, Runner, Hosts, Environment.Service, Gaming, Watchdog, RestorePoints, Signal,
            () => Elevated,
            name => ServicesOnPc.TryGetValue(name, out var entry) ? entry : null,
            readPerformanceNow: _ => Task.FromResult(Now()),
            readGraphics: subKey => Graphics(subKey),
            restorePerformance: (snapshot, _) => RestorePerformance(snapshot),
            restoreEnvironment: RestoreEnvironment,
            announceEnvironment: () => Announced++,
            zone: TimeZoneInfo.Utc);

        public void TurnedOff(string name, string displayName, string previous, DateTimeOffset when)
        {
            Assert.True(Ledger.Remember(name, previous, when));
            ServicesOnPc[name] = new ServiceEntry { Name = name, DisplayName = displayName, StartType = "Disabled" };
        }

        /// <summary>A hosts file SysManager has written over a smaller one, which leaves the copy beside it.</summary>
        public void HostsChangedBySysManager()
        {
            File.WriteAllText(HostsPath, "127.0.0.1 localhost\n");
            Hosts.SaveHosts([Hosts.AddEntry("0.0.0.0", "ads.example.com")]);
        }

        /// <summary>A copy of the user's variables, then a change to one of them.</summary>
        public void VariablesChangedSinceTheirCopy()
        {
            Environment.SetUser("SAFE_USER", "original");
            Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
            Environment.SetUser("SAFE_USER", "changed");
        }

        public void Dispose()
        {
            Environment.Dispose();
            try { _dir.Delete(recursive: true); }
            catch (IOException) { /* cleanup only */ }
        }
    }

    private static async Task<UndoChange> OnlyChangeAsync(Rig rig, UndoChangeKind kind)
    {
        var scan = await rig.Service.ScanAsync();
        return Assert.Single(scan.Changes, c => c.Kind == kind);
    }

    // ── Nothing kept ────────────────────────────────────────────────────────

    [Fact]
    public async Task AFreshPc_HasNothingToPutBack_AndNoProblem()
    {
        using var rig = new Rig();

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Empty(scan.Problems);
        Assert.False(scan.PerformanceWaitsForGameMode);
    }

    // ── Performance Mode ────────────────────────────────────────────────────

    [Fact]
    public async Task PerformanceMode_ListsEachSettingThatDiffers_FromNowToTheOriginal()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;

        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        Assert.Equal("Performance Mode", change.Title);
        Assert.Equal("Changed since Performance Mode was first used: the power plan and visual effects.", change.Detail);
        Assert.Equal(["Power plan: Ultimate Performance → Balanced", "Visual effects: reduced → normal"], change.Lines);
        Assert.Equal("First changed 5 Oct 2026, 19:40", change.Caption);
        Assert.Equal("Put back…", change.ActionText);
        Assert.False(change.NeedsAdmin);
        Assert.Equal("Put back Performance Mode — Confirm", change.QuestionTitle);
        Assert.Equal("Put back Performance Mode?\n\nThese go back to how they were before Performance Mode was first "
            + "used, on 5 Oct 2026, 19:40:\n\n• Power plan: Ultimate Performance → Balanced\n• Visual effects: reduced → "
            + "normal\n\nYou can change any of them again in Performance Mode.", change.Question);
        Assert.Null(change.OpensTab);
    }

    [Fact]
    public async Task PerformanceMode_WhoseSettingsAreAllBack_IsNotListed()
    {
        // The record outlives the settings when they are set back by hand, and a row that puts back nothing would say
        // there was something left to undo.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public async Task PerformanceMode_RecordingAnNvidiaCard_NeedsAdministratorRights()
    {
        // The restore writes the card's setting whenever the record names one, unchanged or not, and only an
        // administrator can write it — so the row says so even when only the power plan moved.
        using var rig = new Rig();
        var original = Original() with { NvidiaSubKey = "0000" };
        Assert.True(rig.Performance.SaveSnapshot(original));
        rig.Now = () => original with { PowerPlanGuid = Tweaked().PowerPlanGuid, PowerPlanName = "Ultimate Performance" };

        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        Assert.True(change.NeedsAdmin);
        Assert.Equal(["Power plan: Ultimate Performance → Balanced"], change.Lines);
    }

    [Fact]
    public async Task PerformanceMode_WhoseRecordIsDamaged_IsNamedAsDamaged_NotAsNothing()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.SnapshotPath, "{ not json");

        var scan = await rig.Service.ScanAsync();

        Assert.DoesNotContain(scan.Changes, c => c.Kind == UndoChangeKind.PerformanceMode);
        Assert.Equal([new UndoProblem(UndoChangeKind.PerformanceMode, UndoProblemKind.Damaged)], scan.Problems);
    }

    [Fact]
    public async Task PerformanceMode_WhoseRecordCannotBeReadJustNow_IsNamedAsUnreadable()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        // Held open by another writer: there, and unreadable just now.
        using var held = File.Open(rig.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Equal([new UndoProblem(UndoChangeKind.PerformanceMode, UndoProblemKind.Unreadable)], scan.Problems);
    }

    [Fact]
    public async Task PerformanceMode_WhoseCurrentSettingsCannotBeRead_IsNamedAsNotCompared()
    {
        // The record was read, so saying SysManager could not read what it kept would be wrong.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = () => throw new IOException("The registry key is marked for deletion.");

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Equal([new UndoProblem(UndoChangeKind.PerformanceMode, UndoProblemKind.CannotCompare)], scan.Problems);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PerformanceMode_WaitsForGameModeToBeOff(bool on, bool leftOn)
    {
        // While game mode is on, the settings are its own, and turning it off later would put back the ones it started
        // from — Performance Mode's — with the record gone.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.Gaming.IsActive.Returns(on);
        rig.Gaming.HasPendingRecovery.Returns(leftOn);

        var scan = await rig.Service.ScanAsync();

        Assert.DoesNotContain(scan.Changes, c => c.Kind == UndoChangeKind.PerformanceMode);
        Assert.Contains(scan.Changes, c => c.Kind == UndoChangeKind.GamingProfile);
        Assert.True(scan.PerformanceWaitsForGameMode);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public async Task PuttingBackPerformanceMode_AfterGameModeCameOn_ChangesNothing()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        rig.Gaming.IsActive.Returns(true);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Nothing was changed: game mode is on. Turn it off first — while it is on, the power plan and visual "
            + "effects are its own, and turning it off puts back the ones it started from.", outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.NotNull(rig.Performance.LoadSnapshot());
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public void PerformanceDifferences_WordEverySetting_AndSayWhatCouldNotBeRead()
    {
        var original = Original() with { NvidiaSubKey = "0001", GpuDynamicPstate = true };
        var now = original with
        {
            PowerPlanGuid = "",
            PowerPlanName = "Unknown",
            GameModeEnabled = false,
            XboxGameBarEnabled = false,
            XboxGameDvrEnabled = false,
            GpuDynamicPstate = false,
            ProcessorMinPercentAc = null,
        };

        var graphics = new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Present, DynamicPstate: false);

        var lines = UndoChangesService.PerformanceDifferences(original, now, graphics).Select(d => d.Line);

        Assert.Equal(
        [
            "Power plan: could not be read → Balanced",
            "Game Mode: off → on",
            "Xbox Game Bar: off → on",
            "Game DVR: off → on",
            "NVIDIA graphics: maximum performance → adaptive, after a restart",
            "Processor minimum: could not be read → 5%",
        ], lines);
    }

    [Fact]
    public void PerformanceDifferences_LeaveOutWhatTheRestoreLeavesAlone()
    {
        // No plan and no processor minimum in the record: the restore sets neither, so neither is listed, whatever
        // they are now.
        var original = Original() with { PowerPlanGuid = "", ProcessorMinPercentAc = null };
        var now = original with { PowerPlanGuid = Tweaked().PowerPlanGuid, PowerPlanName = "Ultimate Performance", ProcessorMinPercentAc = 100 };

        Assert.Empty(UndoChangesService.PerformanceDifferences(original, now, graphics: null));
    }

    [Fact]
    public void PerformanceDifferences_LeaveOutAGraphicsCardThatIsGone()
    {
        // Taken out, or its driver removed: the restore has nothing to write the setting to, and leaves it out, so a
        // line for it would be a change that never happens — and kept the row there for good.
        var original = Original() with { NvidiaSubKey = "0000", GpuDynamicPstate = false };
        var gone = new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Gone, DynamicPstate: true);

        Assert.Empty(UndoChangesService.PerformanceDifferences(original, original with { NvidiaSubKey = null }, gone));
    }

    [Fact]
    public void PerformanceDifferences_ListAGraphicsCardThatCouldNotBeRead()
    {
        // It may still be there, and the restore writes its setting whichever way it is.
        var original = Original() with { NvidiaSubKey = "0000" };
        var unread = new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Unreadable, DynamicPstate: false);

        var difference = Assert.Single(UndoChangesService.PerformanceDifferences(original, original, unread));

        Assert.Equal("NVIDIA graphics: could not be read → adaptive, after a restart", difference.Line);
    }

    [Fact]
    public void PerformanceDifferences_ReadTheCardTheRecordNames_NotTheFirstOneFoundNow()
    {
        // Two NVIDIA cards, or one that moved: the first found now is not the one the restore writes to.
        var original = Original() with { NvidiaSubKey = "0001", GpuDynamicPstate = true };
        var now = original with { NvidiaSubKey = "0000", GpuDynamicPstate = false };
        var recorded = new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Present, DynamicPstate: true);

        Assert.Empty(UndoChangesService.PerformanceDifferences(original, now, recorded));
    }

    [Fact]
    public async Task PerformanceMode_WhoseGraphicsCardIsGone_LeavesItOut_AndNeedsNoAdministrator()
    {
        using var rig = new Rig();
        var original = Original() with { NvidiaSubKey = "0000", GpuDynamicPstate = false };
        Assert.True(rig.Performance.SaveSnapshot(original));
        rig.Now = () => Tweaked() with { NvidiaSubKey = null };
        rig.Graphics = _ => new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Gone, DynamicPstate: false);

        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        Assert.Equal(["Power plan: Ultimate Performance → Balanced", "Visual effects: reduced → normal"], change.Lines);
        Assert.False(change.NeedsAdmin);
    }

    [Fact]
    public async Task PerformanceMode_WhoseGraphicsCardCouldNotBeRead_StillNeedsAdministrator()
    {
        using var rig = new Rig();
        var original = Original() with { NvidiaSubKey = "0000" };
        Assert.True(rig.Performance.SaveSnapshot(original));
        rig.Graphics = _ => new UndoChangesService.GraphicsNow(PerformanceService.RecordedAdapter.Unreadable, DynamicPstate: false);

        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        Assert.Equal(["NVIDIA graphics: could not be read → adaptive, after a restart"], change.Lines);
        Assert.True(change.NeedsAdmin);
    }

    [Fact]
    public async Task PuttingBackPerformanceMode_RestoresTheRecord_ClearsIt_AndTellsTheTab()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        Assert.Equal("Performance Mode is put back: your original settings are on again.", outcome.Message);
        Assert.Equal(Original(), Assert.Single(rig.Restored));
        Assert.Null(rig.Performance.LoadSnapshot());
        Assert.Equal([UndoChangeKind.PerformanceMode], rig.Raised);
    }

    [Fact]
    public async Task PuttingBackPerformanceMode_WithTheGraphicsSettingMoved_SaysARestartIsNeeded()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        var original = Original() with { NvidiaSubKey = "0000" };
        Assert.True(rig.Performance.SaveSnapshot(original));
        rig.Now = () => original with { GpuDynamicPstate = false };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal("Performance Mode is put back. Restart the PC for the graphics setting to take effect.", outcome.Message);
    }

    [Fact]
    public async Task PuttingBackPerformanceMode_ThatCouldNotDeleteItsRecord_IsPartlyPutBack()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.RestorePerformance = _ => Task.FromResult(false);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("Your original settings are back, but SysManager could not delete its record of them. Restore All on "
            + "Performance Mode deletes it.", outcome.Message);
        Assert.Equal([UndoChangeKind.PerformanceMode], rig.Raised);
    }

    [Fact]
    public async Task APerformanceModeRestoreThatChangedNothing_FailsWithTheReason_KeepsTheRecord_AndStillTellsTheTab()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.RestorePerformance = _ => throw new InvalidOperationException("powercfg failed to activate the power plan (exit code 1).");
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Performance Mode could not be put back: powercfg failed to activate the power plan (exit code 1).",
            outcome.Message);
        Assert.NotNull(rig.Performance.LoadSnapshot());
        Assert.Equal([UndoChangeKind.PerformanceMode], rig.Raised);
    }

    [Fact]
    public async Task APerformanceModeRestoreThatStoppedPartWay_IsPartlyPutBack_AndSaysSo()
    {
        // The restore stops at the first setting it cannot write, so the ones before it are back already: read, not
        // assumed, and logged as a change.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.RestorePerformance = _ =>
        {
            rig.Now = () => Tweaked() with { PowerPlanGuid = Original().PowerPlanGuid, PowerPlanName = "Balanced" };
            throw new IOException("The NVIDIA adapter key could not be written.");
        };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("Performance Mode is only partly back: The NVIDIA adapter key could not be written. The list shows what "
            + "is still different.", outcome.Message);
        Assert.True(outcome.ChangedSomething);
    }

    [Fact]
    public async Task APerformanceModeRestoreThatFailed_WithTheSettingsThenUnreadable_DoesNotSayNothingChanged()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.RestorePerformance = _ =>
        {
            rig.Now = () => throw new InvalidOperationException("powercfg did not answer.");
            throw new InvalidOperationException("powercfg failed to activate the power plan (exit code 1).");
        };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        // The look after it most likely fails the same way, so the list cannot be what shows what is left.
        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Performance Mode may be only partly back: powercfg failed to activate the power plan (exit code 1). "
            + "Look again in a moment to see what is still different.", outcome.Message);
    }

    [Fact]
    public async Task APerformanceModeRestoreThatStoppedAfterEverythingListed_SaysTheSettingsAreBack_AndKeepsTheRecord()
    {
        // The step that failed is one the list does not show — the battery value, say — so nothing listed is left, and
        // the row goes. Saying "the list shows what is still different" would point at a list with nothing in it.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.RestorePerformance = _ =>
        {
            rig.Now = Original;
            throw new InvalidOperationException("powercfg could not set the battery value (exit code 1).");
        };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("Performance Mode's settings are back, but the restore stopped before it finished: powercfg could not "
            + "set the battery value (exit code 1). Restore All on Performance Mode tries the rest again.", outcome.Message);
        Assert.NotNull(rig.Performance.LoadSnapshot());
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task APerformanceModeRestoreThatSwitchedThePlanAndThenStopped_IsPartlyBack_ThoughAsManyLinesAreLeft()
    {
        // The restore switches the plan first, and the processor minimum compared after it is the new plan's. One line
        // before, one line after — a different one — so a count said nothing had changed.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = () => Original() with { PowerPlanGuid = Tweaked().PowerPlanGuid, PowerPlanName = "Ultimate Performance" };
        rig.RestorePerformance = _ =>
        {
            rig.Now = () => Original() with { ProcessorMinPercentAc = 100 };
            throw new InvalidOperationException("powercfg could not set the processor minimum (exit code 1).");
        };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        Assert.Equal(["Power plan: Ultimate Performance → Balanced"], change.Lines);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("Performance Mode is only partly back: powercfg could not set the processor minimum (exit code 1). The "
            + "list shows what is still different.", outcome.Message);
    }

    [Theory]
    [InlineData(null, "NotKnown")]                 // could not be read again
    [InlineData("", "AllListedBack")]
    [InlineData("B", "SomeBack")]
    [InlineData("A,B,C", "NothingBack")]           // a new line, and neither listed one went back
    [InlineData("C", "SomeBack")]                  // as many lines as before, different ones
    public void WhatWentBack_ComparesTheLinesThemselves(string? left, string expected)
    {
        // The question listed A and B.
        var after = left?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Enum.Parse<UndoChangesService.StoppedRestore>(expected),
            UndoChangesService.WhatWentBack(["A", "B"], after));
    }

    [Fact]
    public async Task PerformanceMode_IsNotPutBack_WhileAnotherSystemChangeHoldsTheLock()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Busy, outcome.Kind);
        Assert.Equal("Cannot start — Test Holder is already running.", outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task APerformanceModeRecordThatNamesAGraphicsCard_IsNotPutBackWithoutAdministratorRights()
    {
        using var rig = new Rig();
        var original = Original() with { NvidiaSubKey = "0000" };
        Assert.True(rig.Performance.SaveSnapshot(original));
        rig.Now = () => original with { GpuDynamicPstate = false };
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Putting back Performance Mode needs administrator rights. Use Run as administrator at the top of the page.",
            outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task APutBack_WhoseSettingsMovedSinceTheListWasRead_ChangesNothing()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        rig.Now = () => Tweaked() with { GameModeEnabled = false };

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.ChangedSinceListed, outcome.Kind);
        Assert.Equal("Nothing was changed: Performance Mode is no longer as this list showed. The list now shows it as it "
            + "is.", outcome.Message);
        Assert.Empty(rig.Restored);
    }

    [Fact]
    public async Task APutBack_WhoseCopyIsGone_HasNothingLeft()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        Assert.True(rig.Performance.DeleteSnapshot());

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.NothingLeft, outcome.Kind);
        Assert.Equal("Nothing is left to put back for Performance Mode.", outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task APutBack_WhoseRecordCannotBeReadJustNow_ChangesNothing_AndSaysToTryAgain()
    {
        // A record that is there but unreadable is not "nothing left": saying so would have the user believe the
        // change was gone.
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        using var held = File.Open(rig.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("SysManager could not read what it kept for Performance Mode just now, so nothing was changed. Try "
            + "again in a moment.", outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task APutBack_WhoseRecordIsNowDamaged_ChangesNothing_AndSaysItIsDamaged()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        File.WriteAllText(rig.SnapshotPath, "{ not json");

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("What SysManager kept for Performance Mode is damaged, so nothing was changed.", outcome.Message);
        Assert.Empty(rig.Restored);
    }

    // ── Services ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Services_ListOnlyTheOnesStillOff_ByTheNameWindowsShows()
    {
        using var rig = new Rig();
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", new(2026, 10, 2, 21, 12, 0, TimeSpan.Zero));
        // Recorded, then turned back on outside SysManager: nothing left to put back for it.
        Assert.True(rig.Ledger.Remember("Fax", "Manual", Recorded));
        rig.ServicesOnPc["Fax"] = new ServiceEntry { Name = "Fax", DisplayName = "Fax", StartType = "Manual" };
        // Recorded, and the service is gone from the PC.
        Assert.True(rig.Ledger.Remember("RetailDemo", "Manual", Recorded));

        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        Assert.Equal("Turned off Print Spooler. It goes back to the startup type it had.", change.Detail);
        Assert.Equal(["Print Spooler: Disabled → Automatic"], change.Lines);
        Assert.Equal("Turned off 2 Oct 2026, 21:12", change.Caption);
        Assert.Equal("Turn back on…", change.ActionText);
        Assert.True(change.NeedsAdmin);
        Assert.Equal("Turn this service back on?\n\n• Print Spooler: Disabled → Automatic\n\nIt goes back to the startup type "
            + "it had before SysManager turned it off. If that is automatic, it starts again at the next restart; if it is "
            + "Manual, when something needs it.", change.Question);
    }

    [Fact]
    public async Task SeveralServices_AreNamed_InOrder_WithTheNewestTime()
    {
        using var rig = new Rig();
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", new(2026, 10, 2, 21, 12, 0, TimeSpan.Zero));
        rig.TurnedOff("Fax", "Fax", "Manual", new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero));

        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        Assert.Equal("Turned off 2 services: Fax and Print Spooler. Each goes back to the startup type it had.", change.Detail);
        Assert.Equal(["Fax: Disabled → Manual", "Print Spooler: Disabled → Automatic"], change.Lines);
        Assert.Equal("The last one turned off 3 Oct 2026, 08:00", change.Caption);
        Assert.EndsWith("Each goes back to the startup type it had before SysManager turned it off. One that starts "
            + "automatically starts again at the next restart, and one set to Manual when something needs it.",
            change.Question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyServices_AreSummed_WithTheFirstThreeNamed()
    {
        using var rig = new Rig();
        foreach (var name in new[] { "Delta", "Alpha", "Charlie", "Bravo" })
            rig.TurnedOff(name, name, "Manual", Recorded);

        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        Assert.Equal("Turned off 4 services, among them Alpha, Bravo and Charlie. Each goes back to the startup type it "
            + "had.", change.Detail);
        Assert.Equal(4, change.Lines.Count);
    }

    [Fact]
    public async Task AServiceRecordWithANameSysManagerWouldNotPassToScExe_IsLeftOut()
    {
        using var rig = new Rig();
        // Written by hand into the ledger: Disable refuses such a name, so it cannot have come from SysManager.
        File.WriteAllText(Path.Combine(rig.Dir, "service-startup-ledger.json"),
            """[{"ServiceName":"Bad&Name","PreviousStartType":"Manual","DisabledAtUtc":"2026-10-05T19:40:00+00:00"}]""");
        rig.ServicesOnPc["Bad&Name"] = new ServiceEntry { Name = "Bad&Name", DisplayName = "Bad", StartType = "Disabled" };

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
    }

    [Fact]
    public async Task AServicesRecordThatCannotBeRead_IsNamedAsUnreadable()
    {
        using var rig = new Rig();
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        // Held open by another writer: there, and unreadable just now.
        using var held = File.Open(Path.Combine(rig.Dir, "service-startup-ledger.json"), FileMode.Open, FileAccess.Read,
            FileShare.None);

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Equal([new UndoProblem(UndoChangeKind.Services, UndoProblemKind.Unreadable)], scan.Problems);
    }

    [Fact]
    public async Task TurningServicesBackOn_SetsEachToItsRecordedType_ForgetsTheRecords_AndTellsTheTab()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        rig.TurnedOff("Fax", "Fax", "Manual", Recorded);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        Assert.Equal("2 services are back on: each starts the way it did before SysManager turned it off.", outcome.Message);
        await rig.Runner.Received(1).RunProcessAsync("sc.exe", "config \"Spooler\" start= auto", Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
        await rig.Runner.Received(1).RunProcessAsync("sc.exe", "config \"Fax\" start= demand", Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
        Assert.Empty(rig.Ledger.Load()!);
        Assert.Equal([UndoChangeKind.Services], rig.Raised);
    }

    [Fact]
    public async Task AServiceWindowsRefuses_StaysRecorded_AndIsNamed()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        rig.TurnedOff("Fax", "Fax", "Manual", Recorded);
        rig.Runner.RunProcessAsync("sc.exe", "config \"Fax\" start= demand", Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(5);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("1 of 2 services is back on. Windows would not turn Fax back on; the log has the reason.", outcome.Message);
        Assert.Equal(["Fax"], rig.Ledger.Load()!.Keys);
        Assert.Equal([UndoChangeKind.Services], rig.Raised);
    }

    [Fact]
    public async Task ServicesWindowsRefusesEveryOneOf_AreAsTheyWere_AndTheTabIsNotTold()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        rig.Runner.RunProcessAsync("sc.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(5);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Windows would not turn Print Spooler back on. The log has the reason.", outcome.Message);
        Assert.Single(rig.Ledger.Load()!);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task ServicesAreNotTurnedBackOn_WithoutAdministratorRights()
    {
        using var rig = new Rig();
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Putting back the services needs administrator rights. Use Run as administrator at the top of the "
            + "page.", outcome.Message);
        await rig.Runner.DidNotReceive().RunProcessAsync("sc.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
        Assert.Single(rig.Ledger.Load()!);
    }

    [Fact]
    public async Task Services_AreNotTurnedBackOn_WhileAnotherSystemChangeHoldsTheLock()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Busy, outcome.Kind);
        await rig.Runner.DidNotReceive().RunProcessAsync("sc.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task Services_WhoseRecordCannotBeReadAtThePutBack_AreNotTurnedOn()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.Services);
        using var held = File.Open(Path.Combine(rig.Dir, "service-startup-ledger.json"), FileMode.Open, FileAccess.Read,
            FileShare.None);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("SysManager could not read what it kept for the services just now, so nothing was changed. Try again "
            + "in a moment.", outcome.Message);
        await rig.Runner.DidNotReceive().RunProcessAsync("sc.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    // ── Hosts file ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHostsFile_IsListedWhileItDiffersFromTheCopyBesideIt()
    {
        using var rig = new Rig();
        rig.HostsChangedBySysManager();
        File.SetCreationTimeUtc(rig.HostsPath + ".bak", new DateTime(2026, 9, 28, 10, 5, 0, DateTimeKind.Utc));

        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);

        Assert.Equal("Hosts file", change.Title);
        Assert.Equal("SysManager has changed the hosts file since the copy beside it was made.", change.Detail);
        Assert.Equal("Copy from 28 Sep 2026, 10:05", change.Caption);
        Assert.Equal("Restore the copy…", change.ActionText);
        Assert.True(change.NeedsAdmin);
        Assert.Equal("Restore the hosts file from the copy beside it, made on 28 Sep 2026, 10:05?\n\nEverything written to "
            + "it since — by SysManager or by any other program — is replaced by that copy.", change.Question);
    }

    [Fact]
    public async Task TheHostsFile_IsNotListed_OnceItIsTheSameAsItsCopy_OrWithNoCopy()
    {
        // The copy is never deleted, so "there is a copy" stays true after a restore; only a difference is a change.
        using var rig = new Rig();
        File.WriteAllText(rig.HostsPath, "127.0.0.1 localhost\n");
        Assert.Empty((await rig.Service.ScanAsync()).Changes);

        File.Copy(rig.HostsPath, rig.HostsPath + ".bak");
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task AHostsFileSysManagerWrote_ThatIsTheSameAsItsCopy_IsNotListed()
    {
        using var rig = new Rig();
        rig.HostsChangedBySysManager();
        File.Copy(rig.HostsPath, rig.HostsPath + ".bak", overwrite: true);

        Assert.True(rig.Hosts.ReadBackupState()!.WrittenBySysManager);
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task ACopyBesideAHostsFileSysManagerNeverWrote_IsNotListed()
    {
        // A hosts.bak another program or the user left there, beside a hosts file that has changed since: no change of
        // SysManager's, so nothing for it to put back.
        using var rig = new Rig();
        File.WriteAllText(rig.HostsPath + ".bak", "127.0.0.1 localhost\n");
        File.WriteAllText(rig.HostsPath, "127.0.0.1 localhost\n10.0.0.5 nas.local\n");

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Empty(scan.Problems);
        Assert.False(rig.Hosts.ReadBackupState()!.WrittenBySysManager);
    }

    [Fact]
    public async Task AHostsCopyThatCannotBeReadJustNow_IsNamedAsNotCompared()
    {
        // Either file may be the one that could not be read, and either way the two were not compared.
        using var rig = new Rig();
        rig.HostsChangedBySysManager();
        using var held = File.Open(rig.HostsPath + ".bak", FileMode.Open, FileAccess.Read, FileShare.None);

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.Equal([new UndoProblem(UndoChangeKind.HostsFile, UndoProblemKind.CannotCompare)], scan.Problems);
    }

    [Fact]
    public async Task RestoringTheHostsFile_PutsItsCopyBack_AndTellsTheTab()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.HostsChangedBySysManager();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        Assert.Equal("The hosts file is back to the copy beside it.", outcome.Message);
        Assert.Equal("127.0.0.1 localhost\n", File.ReadAllText(rig.HostsPath));
        Assert.Equal([UndoChangeKind.HostsFile], rig.Raised);
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task AHostsFileThatCannotBeReplaced_IsNotRestored_AndSaysWhy()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.HostsChangedBySysManager();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);
        var before = File.ReadAllBytes(rig.HostsPath);

        UndoOutcome outcome;
        // Held open by a reader that lets others read but not replace it: the copy is still compared, and the swap that
        // restores the file is refused.
        using (new FileStream(rig.HostsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            outcome = await rig.Service.PutBackAsync(change);

        // The swap is all or nothing, so the file is as it was, and DNS & Hosts is not told to read it again — which would
        // drop the entries being edited there.
        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.StartsWith("The hosts file could not be restored, so nothing was changed: ", outcome.Message,
            StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(rig.HostsPath));
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task AReadOnlyHostsFile_IsNotRestored_AndIsNotBlamedOnAdministratorRights()
    {
        // The restore runs only as administrator, so a refusal then is the file itself: read-only, or guarded.
        using var rig = new Rig();
        rig.Elevated = true;
        rig.HostsChangedBySysManager();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);
        var before = File.ReadAllBytes(rig.HostsPath);
        File.SetAttributes(rig.HostsPath, FileAttributes.ReadOnly);
        UndoOutcome outcome;
        try
        {
            outcome = await rig.Service.PutBackAsync(change);
        }
        finally
        {
            File.SetAttributes(rig.HostsPath, FileAttributes.Normal);
        }

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Windows would not let SysManager replace the hosts file, so nothing was changed. The file may be set "
            + "to read-only, or protected by security software.", outcome.Message);
        Assert.Equal(before, File.ReadAllBytes(rig.HostsPath));
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task AHostsFileThatCannotBeComparedAtThePutBack_IsNotRestored()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.HostsChangedBySysManager();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);
        var before = File.ReadAllBytes(rig.HostsPath);

        UndoOutcome outcome;
        using (File.Open(rig.HostsPath + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("SysManager could not compare the hosts file with what it kept just now, so nothing was changed. Try "
            + "again in a moment.", outcome.Message);
        Assert.Equal(before, File.ReadAllBytes(rig.HostsPath));
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task TheHostsFile_IsRestored_EvenWhileAnotherSystemChangeHoldsTheLock()
    {
        // No lock here, as the DNS & Hosts tab takes none (#2553): the restore swaps a finished file into place.
        using var rig = new Rig();
        rig.Elevated = true;
        rig.HostsChangedBySysManager();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.HostsFile);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
    }

    [Fact]
    public void HostsFile_ComparesItsCopyByContent_EvenAtTheSameLength()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.HostsPath, "0.0.0.0 aaaa.example\n");
        File.WriteAllText(rig.HostsPath + ".bak", "0.0.0.0 bbbb.example\n");

        Assert.True(rig.Hosts.ReadBackupState()!.DiffersFromHosts);

        File.WriteAllText(rig.HostsPath + ".bak", "0.0.0.0 aaaa.example\n");
        Assert.False(rig.Hosts.ReadBackupState()!.DiffersFromHosts);
    }

    [Fact]
    public void HostsFile_ThatIsGone_DiffersFromItsCopy_AndWasNotWrittenBySysManager()
    {
        using var rig = new Rig();
        File.WriteAllText(rig.HostsPath + ".bak", "127.0.0.1 localhost\n");

        var state = rig.Hosts.ReadBackupState()!;

        Assert.True(state.DiffersFromHosts);
        Assert.False(state.WrittenBySysManager);
    }

    [Fact]
    public void HostsFile_WrittenBySysManager_IsToldByTheHeaderItWrites()
    {
        using var rig = new Rig();
        rig.HostsChangedBySysManager();

        Assert.True(rig.Hosts.ReadBackupState()!.WrittenBySysManager);
    }

    // ── Environment variables ───────────────────────────────────────────────

    [Fact]
    public async Task EnvironmentVariables_ListWhatRestoringTheCopyWouldChange_ByName()
    {
        using var rig = new Rig();
        rig.Environment.SetUser("SAFE_USER", "original");
        rig.Environment.SetUser("GONE_USER", "kept");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
        rig.Environment.SetUser("SAFE_USER", "changed");
        rig.Environment.SetUser("ADDED_USER", "new");
        using (var key = rig.Environment.UserRoot.OpenSubKey(EnvironmentVariableService.UserEnvPath, writable: true))
            key!.DeleteValue("GONE_USER");

        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);

        Assert.Equal(
        [
            "ADDED_USER (yours): removed, added since",
            "GONE_USER (yours): put back",
            "SAFE_USER (yours): back to its earlier value",
        ], change.Lines);
        Assert.Equal("A copy of your variables from before SysManager first changed them. 3 differ from it now.", change.Detail);
        Assert.Equal("Only your own variables were copied", change.Caption);
        Assert.False(change.NeedsAdmin);
    }

    [Fact]
    public async Task EnvironmentVariables_WithAMachineWideCopy_NeedAdministratorRights()
    {
        // The restore writes every machine-wide variable the copy holds, and only an administrator can.
        using var rig = new Rig();
        rig.Environment.SetUser("SAFE_USER", "original");
        rig.Environment.SetMachine("SAFE_MACHINE", "original");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: true);
        rig.Environment.SetUser("SAFE_USER", "changed");

        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);

        Assert.True(change.NeedsAdmin);
        Assert.Equal("Your variables and the machine-wide ones are restored together", change.Caption);
        Assert.Equal(["SAFE_USER (yours): back to its earlier value"], change.Lines);
    }

    [Fact]
    public async Task EnvironmentVariables_ThatMatchTheirCopy_AreNotListed()
    {
        using var rig = new Rig();
        rig.Environment.SetUser("SAFE_USER", "original");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);

        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task AVariableWhoseNameTheRestoreCannotWrite_IsNotListed_AndDoesNotSpoilThePutBack()
    {
        // Windows allows a space in a name, and some installers use one. The restore can neither write nor remove such a
        // name, so listing it would promise a change that never happens, and keep the row there for good.
        using var rig = new Rig();
        rig.Environment.SetUser("SAFE_USER", "original");
        rig.Environment.SetUser("Kept Since Before", "C:\\Tools");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
        rig.Environment.SetUser("SAFE_USER", "changed");
        rig.Environment.SetUser("Kept Since Before", "C:\\Tools\\New");
        rig.Environment.SetUser("Added Since", "C:\\Other");

        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        Assert.Equal(["SAFE_USER (yours): back to its earlier value"], change.Lines);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        Assert.Equal("1 variable is back to the copy SysManager kept.", outcome.Message);
        Assert.Equal("original", rig.Environment.GetUser("SAFE_USER"));
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task AnEnvironmentCopyThatIsNotValid_IsNamedAsDamaged()
    {
        using var rig = new Rig();
        rig.Environment.WriteUserRegistryBackup("{ invalid json");

        var scan = await rig.Service.ScanAsync();

        Assert.Equal([new UndoProblem(UndoChangeKind.EnvironmentVariables, UndoProblemKind.Damaged)], scan.Problems);
    }

    [Fact]
    public async Task RestoringEnvironmentVariables_PutsTheCopyBack_Announces_AndTellsTheTab()
    {
        using var rig = new Rig();
        rig.Environment.SetUser("SAFE_USER", "original");
        rig.Environment.SetUser("UNCHANGED", "same");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
        rig.Environment.SetUser("SAFE_USER", "changed");
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);

        var outcome = await rig.Service.PutBackAsync(change);

        // One variable differed, so one is named, though the restore rewrote both.
        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        Assert.Equal("1 variable is back to the copy SysManager kept.", outcome.Message);
        Assert.Equal("original", rig.Environment.GetUser("SAFE_USER"));
        Assert.Equal(1, rig.Announced);
        Assert.Equal([UndoChangeKind.EnvironmentVariables], rig.Raised);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatWroteOnlySome_IsPartlyBack_AndCountsFromWhatIsLeft()
    {
        using var rig = new Rig();
        rig.Environment.SetUser("FIRST", "a");
        rig.Environment.SetUser("SECOND", "b");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
        rig.Environment.SetUser("FIRST", "changed");
        rig.Environment.SetUser("SECOND", "changed");
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () =>
        {
            rig.Environment.SetUser("FIRST", "a");
            return new EnvironmentVariableService.RestoreResult(true, 1, 0, 1);
        };

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("1 of 2 variables is back to the copy SysManager kept; 1 could not be written. The log has the reason.",
            outcome.Message);
        Assert.Equal(1, rig.Announced);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatWroteNothing_Fails_AndAnnouncesNothing()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () => new EnvironmentVariableService.RestoreResult(true, 0, 0, 1);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Windows would not write the variables back. The log has the reason.", outcome.Message);
        Assert.Equal(0, rig.Announced);
        Assert.Equal([UndoChangeKind.EnvironmentVariables], rig.Raised);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatThrowsBeforeWriting_Fails_WithTheReason()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () => throw new IOException("The registry key is marked for deletion.");

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("The variables could not be restored: The registry key is marked for deletion.", outcome.Message);
        Assert.Equal(0, rig.Announced);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatThrowsPartWay_IsPartlyBack_AndAnnounces()
    {
        using var rig = new Rig();
        rig.Environment.SetUser("FIRST", "a");
        rig.Environment.SetUser("SECOND", "b");
        rig.Environment.Service.EnsureBackup(includeUser: true, includeMachine: false);
        rig.Environment.SetUser("FIRST", "changed");
        rig.Environment.SetUser("SECOND", "changed");
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () =>
        {
            rig.Environment.SetUser("FIRST", "a");
            throw new IOException("The registry key is marked for deletion.");
        };

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("The variables are only partly back: The registry key is marked for deletion. The list shows what is "
            + "still different.", outcome.Message);
        Assert.Equal(1, rig.Announced);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatThrowsAfterEveryListedVariable_SaysTheyAreBack()
    {
        // Nothing listed is left, so the row goes; "the list shows what is still different" would point at nothing.
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () =>
        {
            rig.Environment.SetUser("SAFE_USER", "original");
            throw new IOException("The registry key is marked for deletion.");
        };

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("The variables are back to the copy SysManager kept, but Windows reported a problem on the way: The "
            + "registry key is marked for deletion.", outcome.Message);
        Assert.Equal(1, rig.Announced);
        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    [Fact]
    public async Task AnEnvironmentRestoreThatThrows_WithTheCopyThenUnreadable_DoesNotSayNothingChanged()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () =>
        {
            rig.Environment.SetUser("SAFE_USER", "original");
            rig.Environment.WriteUserRegistryBackup("{ invalid json");
            throw new IOException("The registry key is marked for deletion.");
        };

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("The variables may be only partly back: The registry key is marked for deletion. Look again in a "
            + "moment to see what is still different.", outcome.Message);
        // Not known to be unchanged, so told as changed: the broadcast and the tab's read cost nothing if they were not.
        Assert.Equal(1, rig.Announced);
        Assert.Equal([UndoChangeKind.EnvironmentVariables], rig.Raised);
    }

    [Fact]
    public async Task AnEnvironmentCopyThatTheRestoreFindsDamaged_ChangesNothing()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () => new EnvironmentVariableService.RestoreResult(true, 0, 0, 0) { InvalidBackup = true };

        var outcome = await rig.Service.PutBackAsync(change);

        // Found before the first write, so the Environment Variables tab is not told to read them again — which would
        // drop what is being edited there.
        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("What SysManager kept for the environment variables is damaged, so nothing was changed.", outcome.Message);
        Assert.Equal(0, rig.Announced);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task AnEnvironmentCopyThatTheRestoreFindsGone_HasNothingLeft()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.RestoreEnvironment = () => new EnvironmentVariableService.RestoreResult(false, 0, 0, 0);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.NothingLeft, outcome.Kind);
        Assert.Equal("There is no copy of the environment variables to restore.", outcome.Message);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task AnEnvironmentCopyDamagedSinceTheListWasRead_IsNotRestored()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        rig.Environment.WriteUserRegistryBackup("{ invalid json");

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("What SysManager kept for the environment variables is damaged, so nothing was changed.", outcome.Message);
        Assert.Equal("changed", rig.Environment.GetUser("SAFE_USER"));
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task EnvironmentVariables_AreNotRestored_WhileAnotherSystemChangeHoldsTheLock()
    {
        using var rig = new Rig();
        rig.VariablesChangedSinceTheirCopy();
        var change = await OnlyChangeAsync(rig, UndoChangeKind.EnvironmentVariables);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Busy, outcome.Kind);
        Assert.Equal("changed", rig.Environment.GetUser("SAFE_USER"));
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public void PreviewRestore_TellsAKindChangeFromNoChange()
    {
        // The restore writes the copy's kind back too, so a PATH flattened to REG_SZ with the same text is a change.
        using var env = new RedirectedEnvironment();
        env.SetUser("PATHISH", "%SystemRoot%\\bin", Microsoft.Win32.RegistryValueKind.ExpandString);
        env.Service.EnsureBackup(includeUser: true, includeMachine: false);
        Assert.Empty(env.Service.PreviewRestore()!.Differences);

        env.SetUser("PATHISH", "%SystemRoot%\\bin", Microsoft.Win32.RegistryValueKind.String);

        var difference = Assert.Single(env.Service.PreviewRestore()!.Differences);
        Assert.Equal(EnvironmentVariableService.DifferenceKind.ChangedBack, difference.Kind);
    }

    [Fact]
    public void PreviewRestore_WithNoCopy_IsNull()
    {
        using var env = new RedirectedEnvironment();
        Assert.Null(env.Service.PreviewRestore());
    }

    // ── Gaming Profile ──────────────────────────────────────────────────────

    [Fact]
    public async Task GameModeThatIsOn_IsListed_AndTurningItOffReverts()
    {
        using var rig = new Rig();
        rig.Gaming.IsActive.Returns(true);
        rig.Gaming.RevertAsync(Arg.Any<CancellationToken>()).Returns(GamingRevertResult.Complete);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.GamingProfile);
        Assert.Equal("Game mode is on.", change.Detail);
        Assert.Equal("On now", change.Caption);
        Assert.Equal("Turn it off…", change.ActionText);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal("Game mode is off, and what it changed is back.", outcome.Message);
        await rig.Gaming.Received(1).RevertAsync(Arg.Any<CancellationToken>());
        await rig.Gaming.DidNotReceive().RecoverPendingAsync(Arg.Any<CancellationToken>());
        Assert.Equal([UndoChangeKind.GamingProfile], rig.Raised);
    }

    [Fact]
    public async Task GameModeLeftOnByAnEarlierRun_IsRecovered_AndWhatDidNotComeBackIsNamed()
    {
        using var rig = new Rig();
        rig.Gaming.HasPendingRecovery.Returns(true);
        rig.Gaming.RecoverPendingAsync(Arg.Any<CancellationToken>()).Returns(new GamingRevertResult(["Search indexing"]));
        var change = await OnlyChangeAsync(rig, UndoChangeKind.GamingProfile);
        Assert.Equal("Still on from a session that did not end cleanly: SysManager closed while game mode was on.", change.Detail);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PartlyPutBack, outcome.Kind);
        Assert.Equal("Game mode is off, but \"Search indexing\" was not restored — check it yourself. The log has the reason.",
            outcome.Message);
        await rig.Gaming.Received(1).RecoverPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GameModeThatEndedOnItsOwn_SinceTheListWasRead_ChangesNothing()
    {
        // The bound game exited and game mode reverted itself while the list was on screen.
        using var rig = new Rig();
        rig.Gaming.IsActive.Returns(true);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.GamingProfile);
        rig.Gaming.IsActive.Returns(false);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.NothingLeft, outcome.Kind);
        Assert.Equal("Nothing is left to put back for game mode.", outcome.Message);
        await rig.Gaming.DidNotReceive().RevertAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GameMode_IsTurnedOff_EvenWhileAnotherSystemChangeHoldsTheLock()
    {
        // Its reverts take the lock themselves when it is free and never refuse: a refusal would leave a game's changes
        // on with nothing left to undo them.
        using var rig = new Rig();
        rig.Gaming.IsActive.Returns(true);
        rig.Gaming.RevertAsync(Arg.Any<CancellationToken>()).Returns(GamingRevertResult.Complete);
        var change = await OnlyChangeAsync(rig, UndoChangeKind.GamingProfile);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.PutBack, outcome.Kind);
        await rig.Gaming.Received(1).RevertAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GameModeWhoseRecordCannotBeRead_IsNamedAsUnreadable_AndHoldsPerformanceModeBack()
    {
        // Whether a session was left on is not known, and if one was, recovering it later would undo a Performance Mode
        // put back now. Saying nothing about game mode would also read as "nothing left on".
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.Gaming.HasPendingRecovery.Returns((bool?)null);

        var scan = await rig.Service.ScanAsync();

        Assert.Empty(scan.Changes);
        Assert.True(scan.PerformanceWaitsForGameMode);
        Assert.Equal([new UndoProblem(UndoChangeKind.GamingProfile, UndoProblemKind.Unreadable)], scan.Problems);
    }

    [Fact]
    public async Task PuttingBackPerformanceMode_WhenWhetherGameModeWasLeftOnCannotBeRead_ChangesNothing()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        var change = await OnlyChangeAsync(rig, UndoChangeKind.PerformanceMode);
        rig.Gaming.HasPendingRecovery.Returns((bool?)null);

        var outcome = await rig.Service.PutBackAsync(change);

        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Nothing was changed: SysManager could not read just now whether game mode was left on, and if it "
            + "was, turning it off later would put Performance Mode's changes back. Try again in a moment.", outcome.Message);
        Assert.Empty(rig.Restored);
        Assert.Empty(rig.Raised);
    }

    // ── Settings Watchdog ───────────────────────────────────────────────────

    private static SettingDrift Drift(string name, bool canRestore = true) =>
        new(new WatchedSetting(name.ToLowerInvariant(), name, "", "", "", "", new Dictionary<int, string>()), 1, 0, canRestore);

    [Fact]
    public async Task SettingsChangedSinceTheyWereSaved_AreALink_ToSettingsWatchdog()
    {
        using var rig = new Rig();
        var baseline = new BaselineSnapshot(new DateTime(2026, 10, 1), []);
        rig.Watchdog.LoadBaseline().Returns(baseline);
        rig.Watchdog.DetectDrift(baseline, Arg.Any<IReadOnlyDictionary<string, int?>>())
            .Returns([Drift("Advertising ID"), Drift("Default browser", canRestore: false), Drift("Web search")]);

        var change = await OnlyChangeAsync(rig, UndoChangeKind.SettingsWatchdog);

        Assert.Equal("Settings changed since you saved them", change.Title);
        Assert.Equal("2 settings no longer match the ones you saved in Settings Watchdog, for example Advertising ID.",
            change.Detail);
        Assert.Equal("nav-settings-watchdog", change.OpensTab);
        Assert.Equal("Review in Settings Watchdog", change.ActionText);
        Assert.Equal("", change.Question);

        var outcome = await rig.Service.PutBackAsync(change);
        Assert.Equal(UndoOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("Nothing was changed: these settings are put back in Settings Watchdog, one at a time.", outcome.Message);
        Assert.Empty(rig.Raised);
    }

    [Fact]
    public async Task SettingsTheWatchdogCannotWriteBack_AreNotListed()
    {
        using var rig = new Rig();
        var baseline = new BaselineSnapshot(new DateTime(2026, 10, 1), []);
        rig.Watchdog.LoadBaseline().Returns(baseline);
        rig.Watchdog.DetectDrift(baseline, Arg.Any<IReadOnlyDictionary<string, int?>>())
            .Returns([Drift("Default browser", canRestore: false)]);

        Assert.Empty((await rig.Service.ScanAsync()).Changes);
    }

    // ── Restore points ──────────────────────────────────────────────────────

    private static PSObject Point(int sequence, string description, string createdIso)
    {
        var point = new PSObject();
        point.Properties.Add(new PSNoteProperty("SequenceNumber", sequence));
        point.Properties.Add(new PSNoteProperty("Description", description));
        point.Properties.Add(new PSNoteProperty("CreationTimeIso", createdIso));
        point.Properties.Add(new PSNoteProperty("RestorePointType", "MODIFY_SETTINGS"));
        point.Properties.Add(new PSNoteProperty("EventType", "BEGIN_SYSTEM_CHANGE"));
        return point;
    }

    [Fact]
    public async Task TheNewestRestorePoint_IsAskedForOnlyAsAdministrator()
    {
        using var rig = new Rig();
        rig.Runner.RunAsync(RestorePointService.ListScript, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>([Point(7, "SysManager Privacy & Telemetry", "2026-10-05T19:39:00Z"),
                                               Point(9, "Windows Update", "2026-10-06T08:00:00Z")]));

        var standard = await rig.Service.LookForRestorePointAsync();
        Assert.False(standard.Listed);
        await rig.Runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());

        rig.Elevated = true;
        var elevated = await rig.Service.LookForRestorePointAsync();

        Assert.True(elevated.Listed);
        Assert.Equal(9, elevated.Newest!.SequenceNumber);
    }

    [Fact]
    public async Task TheScan_NeverAsksForTheRestorePoints()
    {
        // Creating a restore point holds the PowerShell runner for as long as that takes, and the changes must not wait
        // behind it.
        using var rig = new Rig();
        rig.Elevated = true;

        await rig.Service.ScanAsync();

        await rig.Runner.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARestorePointListWindowsRefuses_IsNotListed()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.Runner.RunAsync(RestorePointService.ListScript, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Throws(new RuntimeException("Access denied"));

        var look = await rig.Service.LookForRestorePointAsync();

        Assert.False(look.Listed);
        Assert.Null(look.Newest);
    }

    [Fact]
    public async Task ARestorePointQueryThatCannotStart_IsNotListed()
    {
        using var rig = new Rig();
        rig.Elevated = true;
        rig.Runner.RunAsync(RestorePointService.ListScript, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("The runspace pool is closed."));

        var look = await rig.Service.LookForRestorePointAsync();

        Assert.False(look.Listed);
    }

    // ── The order, and the words shared by every row ───────────────────────

    [Fact]
    public async Task EveryKind_IsListedInOneFixedOrder()
    {
        using var rig = new Rig();
        Assert.True(rig.Performance.SaveSnapshot(Original()));
        rig.Now = Tweaked;
        rig.TurnedOff("Spooler", "Print Spooler", "Automatic", Recorded);
        rig.HostsChangedBySysManager();
        rig.VariablesChangedSinceTheirCopy();
        var baseline = new BaselineSnapshot(new DateTime(2026, 10, 1), []);
        rig.Watchdog.LoadBaseline().Returns(baseline);
        rig.Watchdog.DetectDrift(baseline, Arg.Any<IReadOnlyDictionary<string, int?>>()).Returns([Drift("Web search")]);

        Assert.Equal(
        [
            UndoChangeKind.PerformanceMode, UndoChangeKind.Services, UndoChangeKind.HostsFile,
            UndoChangeKind.EnvironmentVariables, UndoChangeKind.SettingsWatchdog,
        ], (await rig.Service.ScanAsync()).Changes.Select(c => c.Kind));

        // Game mode on takes Performance Mode's place in the list until it is off.
        rig.Gaming.IsActive.Returns(true);
        Assert.Equal(
        [
            UndoChangeKind.Services, UndoChangeKind.HostsFile, UndoChangeKind.EnvironmentVariables,
            UndoChangeKind.GamingProfile, UndoChangeKind.SettingsWatchdog,
        ], (await rig.Service.ScanAsync()).Changes.Select(c => c.Kind));
    }

    [Fact]
    public void TheQuestion_ListsAtMostTwelveLines_AndCountsTheRest()
    {
        var lines = Enumerable.Range(1, 14).Select(i => $"line {i}").ToList();

        var bullets = UndoChangesService.Bullets(lines).Split('\n');

        Assert.Equal(UndoChangesService.MaxQuestionLines, bullets.Length);
        Assert.Equal("• line 11", bullets[^2]);
        Assert.Equal("• and 3 more", bullets[^1]);
        Assert.Equal(12, UndoChangesService.Bullets(lines.Take(12).ToList()).Split('\n').Length);
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a and b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b and c")]
    public void JoinAnd_ReadsAsASentence(string[] items, string expected) =>
        Assert.Equal(expected, UndoChangesService.JoinAnd(items));

    [Theory]
    [InlineData(UndoChangeKind.PerformanceMode, "Performance Mode")]
    [InlineData(UndoChangeKind.Services, "the services")]
    [InlineData(UndoChangeKind.HostsFile, "the hosts file")]
    [InlineData(UndoChangeKind.EnvironmentVariables, "the environment variables")]
    [InlineData(UndoChangeKind.GamingProfile, "game mode")]
    [InlineData(UndoChangeKind.SettingsWatchdog, "Settings Watchdog")]
    public void EachCopy_HasANameForASentence(UndoChangeKind kind, string expected) =>
        Assert.Equal(expected, UndoChangesService.Name(kind));

    [Fact]
    public void DescribesTheSame_ComparesTheWords_NotTheDates()
    {
        var listed = new UndoChange(UndoChangeKind.HostsFile, "Hosts file", "d", "Copy from 1 Oct", true, "Restore…", ["x"],
            "q", "t");

        Assert.True(UndoChangesService.DescribesTheSame(listed, listed with { Caption = "Copy from 2 Oct" }));
        Assert.False(UndoChangesService.DescribesTheSame(listed, listed with { Detail = "other" }));
        Assert.False(UndoChangesService.DescribesTheSame(listed, listed with { Lines = ["y"] }));
        Assert.False(UndoChangesService.DescribesTheSame(listed, listed with { NeedsAdmin = false }));
        Assert.False(UndoChangesService.DescribesTheSame(listed, listed with { Kind = UndoChangeKind.Services }));
    }

    [Fact]
    public void AChange_ReadsAsItsWords_ForAScreenReader()
    {
        var change = new UndoChange(UndoChangeKind.HostsFile, "Hosts file", "The hosts file was changed.", "Copy from 1 Oct",
            true, "Restore the copy…", [], "q", "t");

        Assert.Equal("Hosts file. The hosts file was changed. Copy from 1 Oct.", change.ToString());
        Assert.Equal("Hosts file. The hosts file was changed.", (change with { Caption = "" }).ToString());
    }
}
