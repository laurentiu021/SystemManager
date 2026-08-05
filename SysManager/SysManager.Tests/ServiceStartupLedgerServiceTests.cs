// SysManager · ServiceStartupLedgerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
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
    [InlineData("Boot")]
    [InlineData("System")]
    public void Remember_ARestorableType_IsRecorded(string type)
    {
        NewService().Remember("svc", type, At);

        Assert.Equal(type, NewService().PreviousStartTypeFor("svc"));
    }

    [Theory]
    [InlineData("Disabled")]     // restoring to Disabled is what Enable exists to undo
    [InlineData("Unknown")]
    [InlineData("Delayed")]      // plausible-looking but not a ServiceStartMode name
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
    [InlineData("Boot", "boot")]
    [InlineData("System", "system")]
    public void EveryRestorableType_MapsToARealScToken(string type, string expectedToken)
    {
        // The ledger is only useful if what it stores round-trips into a token sc.exe accepts. If a
        // type were storable but mapped to the "demand" fallback, Enable would still be wrong while
        // the ledger looked correct — so the two are asserted together, not in isolation.
        NewService().Remember("svc", type, At);
        var restored = NewService().PreviousStartTypeFor("svc");

        Assert.Equal(expectedToken, ServiceManagerService.StartTypeToScToken(restored));
    }

    [Fact]
    public void AnUnrecordedService_FallsBackToManual()
    {
        // The conservative default for a service we have no record of.
        Assert.Equal("demand",
            ServiceManagerService.StartTypeToScToken(NewService().PreviousStartTypeFor("never-seen")));
    }
}
