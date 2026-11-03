// SysManager · PrivacyLedgerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="PrivacyLedgerService"/> keeps what each privacy switch's values held before SysManager first wrote them
/// (#2778). These write the ledger to a folder of their own and the values to an HKCU key of their own.
/// </summary>
public sealed class PrivacyLedgerServiceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 2, 0, 0, TimeSpan.Zero);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "SysManagerPrivacyLedgerTests", Guid.NewGuid().ToString("N"));

    private readonly string _keyName = @"Software\SysManagerTests\PrivacyLedger_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _key;

    public PrivacyLedgerServiceTests() => _key = Registry.CurrentUser.CreateSubKey(_keyName, writable: true)!;

    public void Dispose()
    {
        _key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyName, throwOnMissingSubKey: false);
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private string LedgerFile => Path.Combine(_dir, PrivacyLedgerService.FileName);

    private static PrivacyPriorValue Prior(string valueName, string? kind = "DWord", string? data = "1") =>
        new(@"HKLM\SOFTWARE\Policies\SysManagerTests", valueName, kind, data, null, At);

    // ── Capturing a value and putting it back ─────────────────────────────

    public static TheoryData<RegistryValueKind, object> EveryKindItCanPutBack => new()
    {
        { RegistryValueKind.DWord, 1 },
        { RegistryValueKind.DWord, -2 },
        { RegistryValueKind.QWord, 5_000_000_000L },
        { RegistryValueKind.String, "1" },
        { RegistryValueKind.ExpandString, @"%SystemRoot%\System32" },
        { RegistryValueKind.MultiString, new[] { "first", "", "third" } },
        { RegistryValueKind.Binary, new byte[] { 0, 1, 254, 255 } },
        { RegistryValueKind.None, new byte[] { 7 } },
    };

    [Theory]
    [MemberData(nameof(EveryKindItCanPutBack))]
    public void AValueOfEveryKind_IsPutBackWithItsKindAndData_AfterARoundTripThroughTheFile(
        RegistryValueKind kind, object data)
    {
        _key.SetValue("V", data, kind);
        var captured = PrivacyLedgerService.Capture(_key, "HKCU\\Test", "V", At);
        Assert.NotNull(captured);
        var stored = PrivacyLedgerService.TryParse(
            PrivacyLedgerService.Serialize(new Dictionary<string, PrivacyPriorValue> { [captured.Key] = captured }))!;
        _key.SetValue("V", 0, RegistryValueKind.DWord);

        PrivacyLedgerService.PutBack(_key, Assert.Single(stored.Values));

        Assert.Equal(kind, _key.GetValueKind("V"));
        Assert.Equal(data, _key.GetValue("V", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void AnExpandableString_IsRecordedAsItWasWritten_NotAsItReads()
    {
        _key.SetValue("V", @"%SystemRoot%\System32", RegistryValueKind.ExpandString);

        Assert.Equal(@"%SystemRoot%\System32", PrivacyLedgerService.Capture(_key, "HKCU\\Test", "V", At)!.Data);
    }

    [Fact]
    public void AValueThatWasNotThere_IsRecordedAsAbsent_AndPuttingItBackRemovesIt()
    {
        var captured = PrivacyLedgerService.Capture(_key, "HKCU\\Test", "V", At)!;
        Assert.Null(captured.Kind);
        _key.SetValue("V", 0, RegistryValueKind.DWord);

        PrivacyLedgerService.PutBack(_key, captured);

        Assert.Null(_key.GetValue("V"));
    }

    // ── The file ──────────────────────────────────────────────────────────

    [Fact]
    public void Remember_KeepsTheFirstRecordOfAValue()
    {
        var ledger = new PrivacyLedgerService(_dir);

        Assert.True(ledger.Remember([Prior("A", data: "1")]));
        Assert.True(ledger.Remember([Prior("A", data: "3"), Prior("B", data: "2")]));

        var records = ledger.Read().Records!;
        Assert.Equal("1", records[PrivacyPriorValue.KeyOf(Prior("A").Path, "A")].Data);
        Assert.Equal("2", records[PrivacyPriorValue.KeyOf(Prior("B").Path, "B")].Data);
    }

    [Fact]
    public void Forget_RemovesOnlyTheRecordsPutBack()
    {
        var ledger = new PrivacyLedgerService(_dir);
        ledger.Remember([Prior("A"), Prior("B")]);

        ledger.Forget([Prior("A")]);

        Assert.Equal([PrivacyPriorValue.KeyOf(Prior("B").Path, "B")], ledger.Read().Records!.Keys);
    }

    [Fact]
    public void ALedgerThatCannotBeRead_IsNeitherReadAsEmpty_NorWrittenOver()
    {
        var ledger = new PrivacyLedgerService(_dir);
        ledger.Remember([Prior("A")]);
        var before = File.ReadAllBytes(LedgerFile);

        using (new FileStream(LedgerFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(ledger.Read().Records);
            Assert.False(ledger.Remember([Prior("B")]));
            ledger.Forget([Prior("A")]);
        }

        Assert.Equal(before, File.ReadAllBytes(LedgerFile));
    }

    [Fact]
    public void ALedgerThatDoesNotParse_IsSetAside_BeforeANewRecordIsWritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LedgerFile, "{ not a ledger");
        var ledger = new PrivacyLedgerService(_dir);
        Assert.True(ledger.Read().Unparsable);

        Assert.True(ledger.Remember([Prior("A")]));

        Assert.Single(ledger.Read().Records!);
        Assert.Contains(Directory.GetFiles(_dir),
            f => File.ReadAllText(f) == "{ not a ledger" && !string.Equals(f, LedgerFile, StringComparison.Ordinal));
    }

    [Fact]
    public void ALedgerThatDoesNotParse_IsNotForgottenOver()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LedgerFile, "{ not a ledger");

        new PrivacyLedgerService(_dir).Forget([Prior("A")]);

        Assert.Equal("{ not a ledger", File.ReadAllText(LedgerFile));
    }

    [Theory]
    [InlineData("4", "1")]                     // a kind's number, not its name
    [InlineData("String, ExpandString", "x")]  // two names, which Enum.TryParse reads as their combined value
    [InlineData("Unknown", "1")]
    [InlineData("Nonsense", "1")]
    [InlineData("DWord", "one")]
    [InlineData("DWord", null)]
    [InlineData("QWord", "1.5")]
    [InlineData("String", null)]
    [InlineData("Binary", "not base64!")]
    public void ARecordThatCouldNotBePutBack_IsSkipped_AndTheRestKept(string kind, string? data)
    {
        var json = PrivacyLedgerService.Serialize(new Dictionary<string, PrivacyPriorValue>
        {
            ["bad"] = Prior("Bad", kind, data),
            ["good"] = Prior("Good"),
        });

        var records = PrivacyLedgerService.TryParse(json)!;

        Assert.Equal([PrivacyPriorValue.KeyOf(Prior("Good").Path, "Good")], records.Keys);
    }

    [Fact]
    public void AMultiStringWithNoLines_IsSkipped()
    {
        var json = PrivacyLedgerService.Serialize(new Dictionary<string, PrivacyPriorValue>
        {
            ["bad"] = Prior("Bad", "MultiString", null),
        });

        Assert.Empty(PrivacyLedgerService.TryParse(json)!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void NothingToRead_IsAnEmptyLedger(string? json) => Assert.Empty(PrivacyLedgerService.TryParse(json)!);

    [Fact]
    public void InputThatIsNotALedger_IsNotAnEmptyOne() => Assert.Null(PrivacyLedgerService.TryParse("{ not a ledger"));
}
