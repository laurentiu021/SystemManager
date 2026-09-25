// SysManager · ServiceStartupLedgerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="ServiceStartupLedgerService"/> — the durable record of what a service's
/// startup type was before SysManager disabled it.
/// <para>The bug this pins: the previous type lived only in a property on <c>ServiceEntry</c>, and
/// every scan rebuilds those objects. So Disable → Refresh (or restart) → Enable restored an
/// Automatic service as Manual, because <c>StartTypeToScToken</c> maps an unknown value to "demand".
/// The status line reported success while the machine's configuration had silently changed.</para>
/// <para>Every test injects a temp directory, so the developer's real ledger in %LOCALAPPDATA% is
/// never read or written.</para>
/// </summary>
public class ServiceStartupLedgerServiceTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTimeOffset At = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    public ServiceStartupLedgerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerLedgerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private ServiceStartupLedgerService NewService() => new(_dir);

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

        var ledger = NewService().Load();

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

        Assert.Empty(NewService().Load());
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
        Assert.Empty(NewService().Load());
    }

    [Fact]
    public void Remember_CreatesTheConfigDirectoryIfMissing()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist", "yet");

        new ServiceStartupLedgerService(nested).Remember("svc", "Automatic", At);

        Assert.Equal("Automatic", new ServiceStartupLedgerService(nested).PreviousStartTypeFor("svc"));
    }

    [Fact]
    public void Load_AMalformedFileOnDisk_ReturnsEmptyWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "service-startup-ledger.json"), "{ not valid json");

        Assert.Empty(NewService().Load());
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
        var original = svc.Load();

        var parsed = ServiceStartupLedgerService.Parse(ServiceStartupLedgerService.Serialize(original));

        Assert.Equal(original.Count, parsed.Count);
        Assert.Equal(original["wuauserv"], parsed["wuauserv"]);
        Assert.Equal(original["Spooler"], parsed["Spooler"]);
    }

    [Fact]
    public void Serialize_PreservesTheDisabledAtTimestamp()
    {
        NewService().Remember("svc", "Automatic", At);

        Assert.Equal(At, NewService().Load()["svc"].DisabledAtUtc);
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
            var service = new ServiceStartupLedgerService(dir);

            await StartLine.RaceAsync(() => first(service), () => second(service));

            assert(attempt, new ServiceStartupLedgerService(dir).Load());
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
