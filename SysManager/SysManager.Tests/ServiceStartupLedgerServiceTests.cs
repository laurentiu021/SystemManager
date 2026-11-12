// SysManager · ServiceStartupLedgerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="ServiceStartupLedgerService"/> — the durable record of what a service's
/// startup type was before SysManager disabled it.
/// <para>The bug this pins: the previous type lived only in a property on <c>ServiceEntry</c>, and
/// every scan rebuilds those objects. So Disable → Refresh (or restart) → Enable restored an
/// Automatic service as Manual, because <c>StartTypeToScToken</c> maps an unknown value to "demand".
/// The status line reported success while the machine's configuration had silently changed.</para>
/// <para>Every test runs over a <see cref="RedirectedServiceLedger"/>: a throwaway key under HKCU standing in for
/// HKLM, and a temp folder for the file earlier versions kept, so the PC's real records are never read or
/// written.</para>
/// </summary>
public class ServiceStartupLedgerServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly RedirectedServiceLedger _store;
    private static readonly DateTimeOffset At = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    public ServiceStartupLedgerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerLedgerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new RedirectedServiceLedger(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private ServiceStartupLedgerService NewService() => _store.NewService();

    /// <summary>The file earlier versions kept the records in.</summary>
    private string LedgerFile => _store.LegacyPath;

    private static ServiceStartupRecord Record(string name, string type, DateTimeOffset when) => new(name, type, when);

    /// <summary>The ledger a fresh service instance reads, asserted to have been readable.</summary>
    private IReadOnlyDictionary<string, ServiceStartupRecord> Loaded()
    {
        var ledger = NewService().Load();
        Assert.NotNull(ledger);
        return ledger;
    }

    // ---------- the regression this exists for ----------

    [Fact]
    public void PreviousStartType_SurvivesANewServiceInstance()
    {
        // The whole point: a second instance models the next scan, or the next app launch. Before
        // this service existed, the answer here was null and Enable fell back to Manual.
        NewService().Remember("wuauserv", "Automatic", At);

        Assert.Equal("Automatic", NewService().PreviousStartTypeFor("wuauserv"));
    }

    [Fact]
    public void PreviousStartType_ForAServiceWeNeverDisabled_IsNull()
    {
        NewService().Remember("wuauserv", "Automatic", At);

        Assert.Null(NewService().PreviousStartTypeFor("Spooler"));
    }

    [Fact]
    public void Forget_RemovesTheRecordSoALaterEnableDoesNotReuseIt()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);

        svc.Forget("wuauserv");

        // Stale entries are worse than none: the service is Automatic again, so a later Enable must
        // not silently re-apply a type from a change that has already been undone.
        Assert.Null(NewService().PreviousStartTypeFor("wuauserv"));
    }

    [Fact]
    public void Forget_AnUnknownService_IsANoOpAndKeepsTheRest()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);

        svc.Forget("NotInTheLedger");

        Assert.Equal("Automatic", NewService().PreviousStartTypeFor("wuauserv"));
    }

    [Fact]
    public void ForgetAll_RemovesTheNamedRecords_AndKeepsTheRest()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("Fax", "Manual", At);
        svc.Remember("RetailDemo", "Manual", At);

        svc.ForgetAll(["fax", "RetailDemo", "NotInTheLedger"]);

        Assert.Equal(["wuauserv"], NewService().Load()!.Keys);
    }

    [Fact]
    public void ForgetAll_OnALedgerThatDoesNotParse_LeavesTheFileAsItIs()
    {
        // It holds how every other service SysManager turned off was set; nothing was read, so nothing is written.
        File.WriteAllText(LedgerFile, "{ not valid json");

        NewService().ForgetAll(["Fax"]);

        Assert.Equal("{ not valid json", File.ReadAllText(LedgerFile));
    }

    [Fact]
    public void Remember_TwiceForOneService_KeepsTheLatest()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("wuauserv", "Manual", At.AddMinutes(5));

        Assert.Equal("Manual", NewService().PreviousStartTypeFor("wuauserv"));
    }

    [Fact]
    public void Remember_SeveralServices_KeepsThemIndependent()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("Spooler", "Manual", At);
        svc.Remember("WSearch", "Automatic", At);

        var ledger = Loaded();

        Assert.Equal(3, ledger.Count);
        Assert.Equal("Automatic", ledger["wuauserv"].PreviousStartType);
        Assert.Equal("Manual", ledger["Spooler"].PreviousStartType);
    }

    // ---------- restorable types only ----------

    [Theory]
    [InlineData("Automatic")]
    [InlineData("Manual")]
    [InlineData("Automatic (Delayed Start)")]   // #2428: the delay has to survive a Disable and Enable
    public void Remember_ARestorableType_IsRecorded(string type)
    {
        NewService().Remember("svc", type, At);

        Assert.Equal(type, NewService().PreviousStartTypeFor("svc"));
    }

    [Theory]
    [InlineData("Disabled")]     // restoring to Disabled is what Enable exists to undo
    [InlineData("Unknown")]
    [InlineData("Delayed")]      // plausible-looking, but not the name services.msc uses
    // Driver start types. The Services tab lists no drivers and SetStartupTypeAsync refuses "boot" and
    // "system", so a record of either could only end in an exception when Enable tried to apply it.
    [InlineData("Boot")]
    [InlineData("System")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Remember_AnUnrestorableType_IsNotRecorded(string? type)
    {
        NewService().Remember("svc", type, At);

        // Falling back to the conservative default beats attempting a restore Windows would reject.
        Assert.Null(NewService().PreviousStartTypeFor("svc"));
    }

    [Fact]
    public void Remember_IsCaseInsensitiveOnTheTypeName()
    {
        NewService().Remember("svc", "automatic", At);

        Assert.Equal("automatic", NewService().PreviousStartTypeFor("svc"));
    }

    [Fact]
    public void Lookup_IsCaseInsensitiveOnTheServiceName()
    {
        // Windows treats service names case-insensitively, and sc.exe/WMI casing is not guaranteed
        // stable across the enumeration and the later lookup.
        NewService().Remember("WuauServ", "Automatic", At);

        Assert.Equal("Automatic", NewService().PreviousStartTypeFor("wuauserv"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Remember_ABlankServiceName_IsIgnored(string name)
    {
        NewService().Remember(name, "Automatic", At);

        Assert.Empty(Loaded());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PreviousStartType_ForABlankName_IsNull(string name)
    {
        Assert.Null(NewService().PreviousStartTypeFor(name));
    }

    // ---------- persistence robustness ----------

    [Fact]
    public void Load_WithNothingSaved_IsEmpty()
    {
        Assert.Empty(Loaded());
    }

    [Fact]
    public void Remember_CreatesTheProtectedKeyWhenItIsNotThere()
    {
        Assert.Null(_store.ProtectedRaw());

        Assert.Equal(ServiceRecordSave.Saved, NewService().Remember("svc", "Automatic", At));

        Assert.Equal(["svc"], _store.ProtectedNames());
        Assert.Equal("Automatic", NewService().PreviousStartTypeFor("svc"));
    }

    [Fact]
    public void Load_AMalformedFileOnDisk_ReturnsEmptyWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "service-startup-ledger.json"), "{ not valid json");

        Assert.Empty(Loaded());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{}")]          // an object where an array is expected
    [InlineData("[]")]
    public void Parse_InvalidInput_ReturnsEmpty(string? json)
    {
        Assert.Empty(ServiceStartupLedgerService.Parse(json));
    }

    [Fact]
    public void Parse_SkipsOnlyTheBadRecordsAndKeepsTheGoodOnes()
    {
        // One hand-edited or truncated entry must not cost the user the whole ledger.
        var json = """
        [
          { "ServiceName": "good", "PreviousStartType": "Automatic", "DisabledAtUtc": "2026-08-04T12:00:00+00:00" },
          { "ServiceName": "", "PreviousStartType": "Automatic", "DisabledAtUtc": "2026-08-04T12:00:00+00:00" },
          { "ServiceName": "badtype", "PreviousStartType": "Whatever", "DisabledAtUtc": "2026-08-04T12:00:00+00:00" },
          { "ServiceName": "drivertype", "PreviousStartType": "Boot", "DisabledAtUtc": "2026-08-04T12:00:00+00:00" },
          { "ServiceName": "alsogood", "PreviousStartType": "Manual", "DisabledAtUtc": "2026-08-04T12:00:00+00:00" }
        ]
        """;

        var ledger = ServiceStartupLedgerService.Parse(json);

        Assert.Equal(2, ledger.Count);
        Assert.True(ledger.ContainsKey("good"));
        Assert.True(ledger.ContainsKey("alsogood"));
    }

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("Spooler", "Manual", At.AddHours(1));
        var original = Loaded();

        var parsed = ServiceStartupLedgerService.Parse(ServiceStartupLedgerService.Serialize(original));

        Assert.Equal(original.Count, parsed.Count);
        Assert.Equal(original["wuauserv"], parsed["wuauserv"]);
        Assert.Equal(original["Spooler"], parsed["Spooler"]);
    }

    [Fact]
    public void Serialize_PreservesTheDisabledAtTimestamp()
    {
        NewService().Remember("svc", "Automatic", At);

        Assert.Equal(At, Loaded()["svc"].DisabledAtUtc);
    }

    // ---------- records that could not be read are not written over (#2521) ----------
    //
    // A failed read used to load as an empty ledger. Enable then restored every service it could not see as Manual,
    // and the next Remember wrote a ledger holding only the service it recorded over all the others. The key's read
    // is made to fail through the ledger's own seam, for as long as it must fail; the earlier version's file is held
    // open with delete sharing only.

    [Fact]
    public void Load_WhenTheKeyCannotBeRead_IsNull_NotEmpty()
    {
        NewService().Remember("wuauserv", "Automatic", At);

        _store.ReadFailure = new IOException("the key could not be read");
        Assert.Null(NewService().Load());
        _store.ReadFailure = null;

        Assert.Single(Loaded());   // readable again once the read works
    }

    [Fact]
    public void Load_WhenTheEarlierVersionsFileCannotBeRead_IsNull_NotEmpty()
    {
        _store.WriteLegacy(Record("wuauserv", "Automatic", At));

        using (new FileStream(LedgerFile, FileMode.Open, FileAccess.Read, FileShare.Delete))
            Assert.Null(NewService().Load());

        Assert.Single(Loaded());
    }

    [Fact]
    public void Remember_WhenTheKeyCannotBeRead_WritesNothing_AndSaysSo()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("Spooler", "Manual", At);

        _store.ReadFailure = new IOException("the key could not be read");
        Assert.Equal(ServiceRecordSave.NotRead, svc.Remember("WSearch", "Automatic", At));
        _store.ReadFailure = null;

        var ledger = Loaded();
        Assert.Equal(["Spooler", "wuauserv"], ledger.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Forget_WhenTheKeyCannotBeRead_LeavesItAsItIs()
    {
        var svc = NewService();
        svc.Remember("wuauserv", "Automatic", At);
        svc.Remember("Spooler", "Manual", At);

        _store.ReadFailure = new UnauthorizedAccessException("reading the key was refused");
        svc.Forget("Spooler");
        _store.ReadFailure = null;

        Assert.Equal(2, Loaded().Count);
    }

    [Fact]
    public void Remember_OverAProtectedValueThatDoesNotParse_KeepsItAside_AndRecords()
    {
        _store.WriteProtected("{ not valid json");

        Assert.Equal(ServiceRecordSave.Saved, NewService().Remember("wuauserv", "Automatic", At));

        Assert.Equal("{ not valid json", _store.ProtectedRaw("Records.unreadable"));
        Assert.Equal("Automatic", Assert.Single(Loaded()).Value.PreviousStartType);
    }

    [Fact]
    public void Remember_OverAnUnusableValue_NeverWritesOverACopyKeptEarlier()
    {
        // The first value set aside stays as it is; the next goes beside it, as a file set aside does.
        _store.WriteProtected("the first one", name: "Records.unreadable");
        _store.WriteProtected("{ the second one");

        Assert.Equal(ServiceRecordSave.Saved, NewService().Remember("wuauserv", "Automatic", At));

        Assert.Equal("the first one", _store.ProtectedRaw("Records.unreadable"));
        Assert.Equal("{ the second one", _store.ProtectedRaw("Records.unreadable-2"));
        Assert.Equal(["wuauserv"], _store.ProtectedNames());
    }

    [Fact]
    public void Remember_WhenWindowsRefusesTheWrite_SaysSo()
    {
        using (_store.DenyWrites())
            Assert.Equal(ServiceRecordSave.NotWritten, NewService().Remember("wuauserv", "Automatic", At));

        Assert.Empty(_store.ProtectedNames());
    }

    [Fact]
    public void Remember_WithNothingToRecord_IsNotAFailure()
    {
        // An unrestorable type is not recorded, and that must not stop the caller from disabling the service.
        Assert.Equal(ServiceRecordSave.Saved, NewService().Remember("svc", "Boot", At));
        Assert.Empty(Loaded());
    }

    // ---------- the protected key, and the file earlier versions kept ----------

    [Fact]
    public void Remember_WritesTheProtectedKey_AndNeverTheProfilesFile()
    {
        Assert.Equal(ServiceRecordSave.Saved, NewService().Remember("wuauserv", "Automatic", At));

        Assert.Equal(["wuauserv"], _store.ProtectedNames());
        Assert.False(File.Exists(LedgerFile));
        Assert.Equal("Automatic", NewService().ReadProtected().Records!["wuauserv"].PreviousStartType);
    }

    [Fact]
    public void Load_PrefersTheProtectedRecord_OverTheProfilesForTheSameService()
    {
        _store.WriteLegacy(Record("svc", "Manual", At));
        NewService().Remember("svc", "Automatic", At);

        Assert.Equal("Automatic", Loaded()["svc"].PreviousStartType);
        Assert.Equal("Automatic", NewService().PreviousStartTypeFor("svc"));
        Assert.Equal(new ServiceRecordLookup(Record("svc", "Automatic", At), false), NewService().Lookup("svc"));
    }

    [Fact]
    public void Lookup_OfAServiceOnlyTheProfilesFileNames_SaysSo()
    {
        _store.WriteLegacy(Record("svc", "Manual", At));

        Assert.Equal(new ServiceRecordLookup(Record("svc", "Manual", At), true), NewService().Lookup("svc"));
        Assert.Equal(new ServiceRecordLookup(null, false), NewService().Lookup("other"));
        Assert.Equal(["svc"], NewService().EarlierVersionOnly());
        Assert.Empty(NewService().ReadProtected().Records!);
    }

    [Fact]
    public void Forget_RemovesTheRecordFromTheProtectedKeyAndTheProfilesFile()
    {
        _store.WriteLegacy(Record("svc", "Manual", At), Record("kept", "Manual", At));
        var svc = NewService();
        svc.Remember("svc", "Automatic", At);
        svc.Remember("other", "Automatic", At);

        svc.Forget("svc");

        Assert.Equal(["other"], _store.ProtectedNames());
        Assert.Equal(["kept"], _store.LegacyNames());
        Assert.False(Loaded().ContainsKey("svc"));
    }

    [Fact]
    public void ForgetAll_WithoutRightsOnTheKey_StillTakesTheFilesRecordsOut_AndMarksTheKeysAsStale()
    {
        // A look without administrator rights: the key cannot be changed, so its record stays, marked as no longer
        // standing for a change, and the file's goes.
        _store.WriteLegacy(Record("Fax", "Manual", At));
        NewService().Remember("Fax", "Manual", At.AddHours(1));

        ServiceRecordsForgotten forgotten;
        using (_store.DenyWrites())
            forgotten = NewService().ForgetAll(["Fax"]);

        Assert.Equal(new ServiceRecordsForgotten(FromKey: 0, KeptInKey: 1, FromFile: 1), forgotten);
        Assert.Empty(_store.LegacyNames());
        Assert.Equal(["Fax"], _store.ProtectedNames());
        Assert.True(NewService().StaleMarks()(Record("Fax", "Manual", At.AddHours(1))));
    }

    [Fact]
    public void ForgetAll_WithRightsOnTheKey_TakesTheRecordAndItsMarkOut()
    {
        NewService().Remember("Fax", "Manual", At);
        using (_store.DenyWrites())
            NewService().ForgetAll(["Fax"]);

        var forgotten = NewService().ForgetAll(["Fax"]);

        Assert.Equal(new ServiceRecordsForgotten(FromKey: 1, KeptInKey: 0, FromFile: 0), forgotten);
        Assert.Empty(_store.ProtectedNames());
        Assert.Empty(ServiceStartupLedgerService.Parse(File.ReadAllText(_store.StalePath)));
    }

    [Fact]
    public void AStaleMark_DoesNotHideALaterRecordOfTheSameService()
    {
        // A Disable after the mark writes a record of its own, at its own time, and that one is SysManager's change.
        NewService().Remember("Fax", "Manual", At);
        using (_store.DenyWrites())
            NewService().ForgetAll(["Fax"]);

        NewService().Remember("Fax", "Automatic", At.AddDays(1));

        Assert.False(NewService().StaleMarks()(NewService().ReadProtected().Records!["Fax"]));
    }

    [Fact]
    public void StaleMarks_ThatDoNotParse_AreNotWrittenOver()
    {
        File.WriteAllText(_store.StalePath, "{ not valid json");
        NewService().Remember("Fax", "Manual", At);

        using (_store.DenyWrites())
            Assert.Equal(0, NewService().ForgetAll(["Fax"]).KeptInKey);

        Assert.Equal("{ not valid json", File.ReadAllText(_store.StalePath));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("size")]
    public void ReadProtected_OfAValueOfAnotherKindOrTooLarge_IsUnusable(string what)
    {
        var valid = ServiceStartupLedgerService.Serialize(
            new Dictionary<string, ServiceStartupRecord> { ["svc"] = Record("svc", "Manual", At) });
        if (what == "kind") _store.WriteProtected(valid, RegistryValueKind.ExpandString);
        else _store.WriteProtected("[" + new string(' ', ServiceStartupLedgerService.MaxProtectedBytes + 1) + "]");

        var (records, unusable) = NewService().ReadProtected();

        Assert.True(unusable);
        Assert.Empty(records!);
    }

    [Fact]
    public void Remember_IntoAKeyOthersCanChange_RecordsNothing()
    {
        // The stand-in's SOFTWARE key is the test account's own, as a key other programs could change would be.
        using var unprotected = new RedirectedServiceLedger(enforceProtection: true);

        Assert.Equal(ServiceRecordSave.NotProtected, unprotected.NewService().Remember("svc", "Automatic", At));
        Assert.Null(unprotected.ProtectedRaw());
    }

    [Fact]
    public void ReadProtected_OfAKeyOthersCanChange_IsUnusable()
    {
        using var unprotected = new RedirectedServiceLedger(enforceProtection: true);
        unprotected.WriteProtected(ServiceStartupLedgerService.Serialize(
            new Dictionary<string, ServiceStartupRecord> { ["svc"] = Record("svc", "Manual", At) }));

        var (records, unusable) = unprotected.NewService().ReadProtected();

        Assert.True(unusable);
        Assert.Empty(records!);
    }

    [Fact]
    public void WithNoMachineRoot_NothingIsKept_AndNothingReadsAsKept()
    {
        var ledger = new ServiceStartupLedgerService(_dir, machineRoot: null);

        Assert.Equal(ServiceRecordSave.NotProtected, ledger.Remember("svc", "Automatic", At));
        Assert.Equal((false, 0), (ledger.ReadProtected().Unusable, ledger.ReadProtected().Records!.Count));
        Assert.False(File.Exists(LedgerFile));
    }

    [Fact]
    public void ALedgerBuiltTheWayTheAppBuildsIt_KeepsNothingInATestRun()
    {
        // The floor under every construction a seam scan cannot see, such as the Services tab's own fallback: the test
        // run points MachineRegistry.Root away from HKLM, so the real records are never read or written. Built through
        // reflection so the configDir scan, which rightly flags the plain form, has nothing to read here.
        Assert.Null(MachineRegistry.Root);
        var ledger = Activator.CreateInstance<ServiceStartupLedgerService>();

        Assert.Equal(ServiceRecordSave.NotProtected, ledger.Remember("SysManagerTestFloorSvc", "Automatic", At));
        Assert.Null(ledger.PreviousStartTypeFor("SysManagerTestFloorSvc"));
    }

    // ---------- agreement with the token mapping it feeds ----------

    [Theory]
    [InlineData("Automatic", "auto")]
    [InlineData("Manual", "demand")]
    [InlineData("Automatic (Delayed Start)", "delayed-auto")]
    public async Task EveryRestorableType_ReachesScExeAsTheTokenThatRestoresIt(string type, string expectedToken)
    {
        // The ledger is only useful if what it stores becomes the sc.exe command that restores it. If a type
        // were storable but mapped to the "demand" fallback, Enable would still be wrong while the ledger
        // looked correct — so the chain is asserted end to end, not one link at a time.
        //
        // End to end means as far as SetStartupTypeAsync, the allowlist in front of sc.exe. Stopping at the
        // token let this pass for Boot and System, which mapped to "boot" and "system" — tokens that
        // allowlist refuses — so a recorded Boot could only ever end in an ArgumentException. The runner
        // is a substitute: nothing is launched and no service changes.
        NewService().Remember("svc", type, At);
        var restored = NewService().PreviousStartTypeFor("svc");
        var runner = RunnerReturning(0);

        await ServiceManagerService.SetStartupTypeAsync(
            "svc", ServiceManagerService.StartTypeToScToken(restored), runner);

        await runner.Received(1).RunProcessAsync(
            "sc.exe", $"config \"svc\" start= {expectedToken}",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task EveryEntryOfTheRestorableList_PassesTheAllowlistInFrontOfScExe()
    {
        // The theory above pins what each known type restores to. This walks the list itself, so a type added
        // to it later cannot be storable without also being accepted by SetStartupTypeAsync — the drift that
        // left Boot and System recordable but never restorable.
        var restorable = ServiceManagerService.RestorableStartTypes;
        Assert.True(restorable.Count >= 3,
            $"only {restorable.Count} restorable startup types — Automatic, its delayed form and Manual were there "
            + "when this was written, so the list has lost one.");

        foreach (var (type, token) in restorable)
        {
            var runner = RunnerReturning(0);

            // No catch: an ArgumentException here IS the failure, and it names the refused token.
            await ServiceManagerService.SetStartupTypeAsync("svc", token, runner);

            await runner.Received(1).RunProcessAsync(
                "sc.exe", $"config \"svc\" start= {token}",
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            Assert.Equal(token, ServiceManagerService.StartTypeToScToken(type));
        }

        // Positive control: the allowlist really does refuse a token outside it, so the loop above passing is
        // evidence rather than an allowlist that accepts anything.
        await Assert.ThrowsAsync<ArgumentException>(
            () => ServiceManagerService.SetStartupTypeAsync("svc", "boot", RunnerReturning(0)));
    }

    private static IPowerShellRunner RunnerReturning(int exitCode)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(exitCode);
        return runner;
    }

    [Fact]
    public void AnUnrecordedService_FallsBackToManual()
    {
        // The conservative default for a service we have no record of.
        Assert.Equal("demand",
            ServiceManagerService.StartTypeToScToken(NewService().PreviousStartTypeFor("never-seen")));
    }

    // ---------- the two mutators racing each other ----------

    /// <summary>
    /// How many times each race below is run. One attempt is a directory plus two small atomic
    /// writes, so the whole cost is a fraction of a second. The count exists because the
    /// unsynchronized code only loses a record on the interleaving where the second writer's save
    /// lands after reading a snapshot that predates the first — about half of them — so a single
    /// attempt could pass straight over the bug. Once the read-modify-write pairs are serialized,
    /// EVERY attempt passes by construction (both orders end with both records), so the repetition
    /// cannot make these flaky.
    /// </summary>
    private const int RaceAttempts = 64;

    /// <summary>
    /// Runs <paramref name="first"/> and <paramref name="second"/> against one service instance from
    /// a shared start line, then hands the persisted ledger to <paramref name="assert"/>. Bounded at
    /// both ends: a writer that never reaches the line, or never returns, fails the run instead of
    /// hanging it.
    /// </summary>
    private async Task RaceTwoMutators(
        Action<ServiceStartupLedgerService> first,
        Action<ServiceStartupLedgerService> second,
        Action<int, IReadOnlyDictionary<string, ServiceStartupRecord>> assert)
    {
        for (var attempt = 0; attempt < RaceAttempts; attempt++)
        {
            var dir = Path.Combine(_dir, $"race-{attempt}");
            Directory.CreateDirectory(dir);
            using var store = new RedirectedServiceLedger(dir);
            var service = store.NewService();

            await StartLine.RaceAsync(() => first(service), () => second(service));

            var ledger = store.NewService().Load();
            Assert.NotNull(ledger);
            assert(attempt, ledger);
        }
    }

    [Fact]
    public async Task TwoServicesDisabledAtOnce_BothKeepTheirRecord()
    {
        // Remember is a Load followed by a Persist over one file holding EVERY service, so it writes
        // back a whole-dictionary snapshot. AtomicFile makes each write atomic but not the pair:
        // unsynchronized, the second writer can persist a dictionary it built before the first
        // landed, and the first service's record is gone. What that costs is the bug this class
        // exists to prevent — Enable restores an Automatic service as Manual through
        // StartTypeToScToken's "demand" default, and reports success.
        await RaceTwoMutators(
            s => s.Remember("alpha", "Automatic", At),
            s => s.Remember("beta", "Manual", At),
            (attempt, ledger) =>
            {
                // No path in the message: a failure here is printed in public CI output.
                Assert.True(ledger.ContainsKey("alpha"),
                    $"attempt {attempt}: alpha's record was dropped — a writer persisted a snapshot "
                        + "it had taken before the other landed");
                Assert.True(ledger.ContainsKey("beta"),
                    $"attempt {attempt}: beta's record was dropped — a writer persisted a snapshot "
                        + "it had taken before the other landed");
                // The values, not just the keys: a lost write that happened to leave both keys
                // present would still restore the wrong startup type.
                Assert.Equal("Automatic", ledger["alpha"].PreviousStartType);
                Assert.Equal("Manual", ledger["beta"].PreviousStartType);
            });
    }

    [Fact]
    public async Task EnablingOneServiceWhileAnotherIsDisabled_DoesNotResurrectTheEnabledOne()
    {
        // The other pairing, so Forget's half of the lock is exercised too: Enable removes alpha's
        // record while Disable adds beta's. Unsynchronized, Remember can write back a snapshot that
        // still contains alpha — and a stale record is worse than none, because Enable would then
        // set a startup type the user had already restored.
        await RaceTwoMutators(
            s => { s.Remember("alpha", "Automatic", At); s.Forget("alpha"); },
            s => s.Remember("beta", "Manual", At),
            (attempt, ledger) =>
            {
                Assert.False(ledger.ContainsKey("alpha"),
                    $"attempt {attempt}: alpha's record came back after Forget removed it");
                Assert.True(ledger.ContainsKey("beta"),
                    $"attempt {attempt}: beta's record was dropped by the overlapping Forget");
            });
    }
}
