// SysManager · DriversViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DriversViewModel"/>.
/// Async commands that spawn real PowerShell are tested in IntegrationTests.
/// </summary>
public class DriversViewModelTests
{
    private static DriversViewModel NewVm() => new(new PowerShellRunner());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_DriversCollectionEmpty()
    {
        var vm = NewVm();
        Assert.Empty(vm.Drivers);
    }

    [Fact]
    public void Constructor_IsBusyFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_StatusMessageEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void Constructor_IsProgressIndeterminateFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void Constructor_DriverCountZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.DriverCount);
    }

    [Fact]
    public void Constructor_SummaryHasDefaultText()
    {
        var vm = NewVm();
        Assert.Contains("List drivers", vm.Summary);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("ListDriversCommand")]
    [InlineData("CancelCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_WithLiveCts_RequestsCancellation()
    {
        var vm = NewVm();
        var cts = new CancellationTokenSource();
        typeof(DriversViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);

        vm.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- ParseDrivers ----------

    [Fact]
    public void ParseDrivers_ValidArray_ReadsEveryDriver()
    {
        var json = """
        [
            {"DeviceName":"Intel HD","Manufacturer":"Intel","DriverVersion":"10.0.1","DriverDate":"/Date(1609459200000)/"},
            {"DeviceName":"NVIDIA GPU","Manufacturer":"NVIDIA","DriverVersion":"31.0.2","DriverDate":null}
        ]
        """;

        var drivers = DriversViewModel.ParseDrivers(json);

        Assert.NotNull(drivers);
        Assert.Equal(2, drivers.Count);
        Assert.Equal("Intel HD", drivers[0].DeviceName);
        Assert.Equal("NVIDIA GPU", drivers[1].DeviceName);
    }

    [Fact]
    public void ParseDrivers_CarriesTheSignatureStateThrough()
    {
        // The third entry omits IsSigned entirely, which is the case that must stay blank rather than
        // become "Unsigned" — a query change or a Windows edition that stops reporting it lands here.
        var json = """
        [
            {"DeviceName":"Signed one","Manufacturer":"Intel","DriverVersion":"1","IsSigned":true},
            {"DeviceName":"Unsigned one","Manufacturer":"Somebody","DriverVersion":"2","IsSigned":false},
            {"DeviceName":"Unknown one","Manufacturer":"Somebody","DriverVersion":"3"}
        ]
        """;

        var drivers = DriversViewModel.ParseDrivers(json);

        Assert.NotNull(drivers);
        Assert.Equal(3, drivers.Count);
        Assert.Equal("Signed", drivers[0].SignatureDisplay);
        Assert.Equal("Unsigned", drivers[1].SignatureDisplay);
        Assert.Equal("", drivers[2].SignatureDisplay);
        Assert.Null(drivers[2].IsSigned);
    }

    [Fact]
    public void ParseDrivers_SingleObject_ReadsOneDriver()
    {
        var json = """{"DeviceName":"Realtek Audio","Manufacturer":"Realtek","DriverVersion":"6.0.1","DriverDate":null}""";

        var drivers = DriversViewModel.ParseDrivers(json);

        Assert.NotNull(drivers);
        Assert.Equal("Realtek Audio", Assert.Single(drivers).DeviceName);
    }

    [Fact]
    public void ParseDrivers_NoOutput_IsAnEmptyList()
    {
        var drivers = DriversViewModel.ParseDrivers("");

        Assert.NotNull(drivers);
        Assert.Empty(drivers);
    }

    [Fact]
    public void ParseDrivers_OutputThatIsNotJson_IsNull_NotAnEmptyList()
        => Assert.Null(DriversViewModel.ParseDrivers("not json at all"));

    // ---------- ReadScan: the exit code counts (#2503) ----------
    //
    // The exit code was discarded, so a query that failed outright printed nothing, parsed to no drivers, and the
    // tab reported "0 drivers found", "Done" and a completion toast.

    private const string OneDriver = """[{"DeviceName":"NVIDIA GPU","Manufacturer":"NVIDIA","DriverVersion":"31.0.2"}]""";

    [Theory]
    [InlineData(1, "")]                 // the query failed outright: Windows PowerShell printed nothing
    [InlineData(0, "not json at all")]  // output that cannot be read
    [InlineData(1, "not json at all")]
    public void ReadScan_AScanThatFailed_HasNoDrivers(int exitCode, string output)
        => Assert.Null(DriversViewModel.ReadScan(exitCode, output).Drivers);

    [Fact]
    public void ReadScan_AnErrorWithDriversListed_KeepsThem_MarkedIncomplete()
    {
        // Windows PowerShell exits 1 when any command in the pipeline wrote an error, and still prints what the
        // pipeline produced. Probed with a two-path Get-Item where one path does not exist.
        var scan = DriversViewModel.ReadScan(1, OneDriver);

        Assert.NotNull(scan.Drivers);
        Assert.Single(scan.Drivers);
        Assert.False(scan.Complete);
    }

    [Fact]
    public void ReadScan_ACleanScan_IsComplete()
    {
        var scan = DriversViewModel.ReadScan(0, OneDriver);

        Assert.NotNull(scan.Drivers);
        Assert.Single(scan.Drivers);
        Assert.True(scan.Complete);
    }

    [Fact]
    public void ReadScan_ACleanExitWithNothingListed_IsWindowsAnsweringNone()
    {
        // ConvertTo-Json prints nothing for an empty pipeline, and exits 0.
        var scan = DriversViewModel.ReadScan(0, "");

        Assert.NotNull(scan.Drivers);
        Assert.Empty(scan.Drivers);
        Assert.True(scan.Complete);
    }

    // ---------- ParseCimDate via reflection ----------

    [Fact]
    public void ParseCimDate_ValidDateTicks_ReturnsDateTime()
    {
        var method = typeof(DriversViewModel)
            .GetMethod("ParseCimDate", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Create a JsonElement with "/Date(1609459200000)/" (2021-01-01 UTC)
        var json = System.Text.Json.JsonDocument.Parse("\"/Date(1609459200000)/\"");
        var result = (DateTime?)method.Invoke(null, new object[] { json.RootElement });

        Assert.NotNull(result);
        Assert.Equal(2021, result!.Value.Year);
    }

    [Fact]
    public void ParseCimDate_NullElement_ReturnsNull()
    {
        var method = typeof(DriversViewModel)
            .GetMethod("ParseCimDate", BindingFlags.NonPublic | BindingFlags.Static)!;

        var json = System.Text.Json.JsonDocument.Parse("null");
        var result = (DateTime?)method.Invoke(null, new object[] { json.RootElement });

        Assert.Null(result);
    }

    // ---------- HideSystemDrivers filter (regression) ----------
    // The filter existed with a change handler, filtering logic and status text, but had ZERO
    // bindings in DriversView.xaml — so no user could ever reach it. These pin the behaviour now
    // that the checkbox exists.

    /// <summary>A runner whose driver query answers each call in turn: what it prints, and its exit code.</summary>
    private static IPowerShellRunner RunnerAnswering(params (string Output, int ExitCode)[] answers)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var call = 0;
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var (output, exitCode) = answers[Math.Min(call++, answers.Length - 1)];
                if (output.Length > 0)
                    runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(output));
                return Task.FromResult(exitCode);
            });
        return runner;
    }

    /// <summary>A view model that has run one scan answered with <paramref name="output"/>.</summary>
    private static async Task<DriversViewModel> ScannedVm(string output, int exitCode = 0, bool hideSystemDrivers = false)
    {
        var vm = new DriversViewModel(RunnerAnswering((output, exitCode))) { HideSystemDrivers = hideSystemDrivers };
        await vm.ListDriversCommand.ExecuteAsync(null);
        return vm;
    }

    private const string MixedDrivers = """
    [
        {"DeviceName":"Generic Monitor","Manufacturer":"Microsoft","DriverVersion":"10.0.1","DriverDate":null},
        {"DeviceName":"Root Hub","Manufacturer":"Windows","DriverVersion":"10.0.1","DriverDate":null},
        {"DeviceName":"NVIDIA GPU","Manufacturer":"NVIDIA","DriverVersion":"31.0.2","DriverDate":null},
        {"DeviceName":"Realtek Audio","Manufacturer":"Realtek","DriverVersion":"6.0.1","DriverDate":null}
    ]
    """;

    [Fact]
    public async Task HideSystemDrivers_Off_ShowsEveryDriver()
    {
        var vm = await ScannedVm(MixedDrivers);

        Assert.False(vm.HideSystemDrivers);
        Assert.Equal(4, vm.Drivers.Count);
    }

    [Fact]
    public async Task HideSystemDrivers_On_KeepsOnlyThirdPartyDrivers()
    {
        var vm = await ScannedVm(MixedDrivers);

        vm.HideSystemDrivers = true;

        Assert.Equal(2, vm.Drivers.Count);
        Assert.All(vm.Drivers, d => Assert.False(DriversViewModel.IsSystemDriver(d)));
    }

    [Fact]
    public async Task HideSystemDrivers_Toggling_RestoresTheFullList()
    {
        var vm = await ScannedVm(MixedDrivers);

        vm.HideSystemDrivers = true;
        vm.HideSystemDrivers = false;

        Assert.Equal(4, vm.Drivers.Count);
    }

    [Fact]
    public async Task HideSystemDrivers_On_SummaryReportsBothCounts()
    {
        var vm = await ScannedVm(MixedDrivers);

        vm.HideSystemDrivers = true;

        // "4 found (2 shown)" — the user must not think two drivers vanished.
        Assert.Contains("4 drivers found", vm.Summary);
        Assert.Contains("2 shown", vm.Summary);
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("microsoft")]                       // casing varies in Win32_PnPSignedDriver
    [InlineData("Microsoft Corporation")]
    [InlineData("Windows")]
    [InlineData("(Standard system devices)Windows")] // substring match, as the real data has
    public void IsSystemDriver_MatchesWindowsSuppliedPublishers(string manufacturer)
        => Assert.True(DriversViewModel.IsSystemDriver(new DriverEntry { Manufacturer = manufacturer }));

    [Theory]
    [InlineData("NVIDIA")]
    [InlineData("Intel")]
    [InlineData("Realtek Semiconductor Corp.")]
    [InlineData("")]
    public void IsSystemDriver_DoesNotMatchThirdPartyPublishers(string manufacturer)
        => Assert.False(DriversViewModel.IsSystemDriver(new DriverEntry { Manufacturer = manufacturer }));

    // ---------- empty states: not-scanned vs filtered-to-nothing ----------

    [Fact]
    public void BeforeAnyScan_TheNotScannedStateIsShown()
    {
        var vm = NewVm();

        Assert.True(vm.HasNotScanned);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public async Task WhenTheFilterHidesEveryDriver_TheFilteredStateIsShownNotTheScanPrompt()
    {
        // On a machine where every driver is Microsoft-supplied, the single shared empty state told
        // the user to click a button they had already clicked. Same defect as the Logs tab's.
        var vm = await ScannedVm("""
        [
            {"DeviceName":"Generic Monitor","Manufacturer":"Microsoft","DriverVersion":"10.0.1","DriverDate":null}
        ]
        """);

        vm.HideSystemDrivers = true;

        Assert.Empty(vm.Drivers);
        Assert.True(vm.HasNoResults);
    }

    [Fact]
    public async Task WithDriversShown_NeitherEmptyStateIsActive()
    {
        var vm = await ScannedVm(MixedDrivers);

        Assert.False(vm.HasNoResults);
        Assert.NotEmpty(vm.Drivers);
    }

    [Theory]
    [InlineData("[]", 0)]   // a scan that listed none
    [InlineData("", 1)]     // a scan that failed
    public async Task AScanThatFoundNothing_DoesNotClaimTheFilterHidThings(string output, int exitCode)
    {
        // Zero drivers is not "filtered to nothing" — HasNoResults must stay false so the
        // wrong advice ("untick the checkbox") is never shown.
        var vm = await ScannedVm(output, exitCode, hideSystemDrivers: true);

        Assert.False(vm.HasNoResults);
    }

    // ---------- a scan that failed says so (#2503) ----------

    [Fact]
    public async Task AScanThatFailed_SaysSo_NotZeroDriversFound()
    {
        var vm = await ScannedVm("", exitCode: 1);

        Assert.True(vm.ListFailed);
        Assert.True(vm.HasNotScanned);   // the empty state stays up, now saying the scan failed
        Assert.Equal("Drivers could not be read", vm.EmptyTitle);
        Assert.StartsWith("Could not read the installed drivers.", vm.StatusMessage);
        Assert.DoesNotContain("drivers found", vm.Summary);
    }

    [Fact]
    public async Task OutputThatCannotBeRead_StaysReportedAsAFailure()
    {
        // The parse error used to be overwritten by "Done" on the next line, so it was never seen.
        var vm = await ScannedVm("not json at all");

        Assert.True(vm.ListFailed);
        Assert.StartsWith("Could not read the installed drivers.", vm.StatusMessage);
    }

    [Fact]
    public async Task AFailedRescan_KeepsTheList_AndSaysItIsFromTheLastScan()
    {
        var vm = new DriversViewModel(RunnerAnswering((MixedDrivers, 0), ("", 1)));
        await vm.ListDriversCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Drivers.Count);   // the premise: the first scan listed them

        await vm.ListDriversCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Drivers.Count);
        Assert.True(vm.ListFailed);
        Assert.Equal("Could not read the installed drivers, so the list below is from the last scan.", vm.StatusMessage);
    }

    [Fact]
    public async Task ACancelledRescan_KeepsTheList_AndSaysSo()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var call = 0;
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (call++ > 0) throw new OperationCanceledException();
                runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(MixedDrivers));
                return Task.FromResult(0);
            });
        var vm = new DriversViewModel(runner);
        await vm.ListDriversCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Drivers.Count);   // the premise: the first scan listed them

        await vm.ListDriversCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Drivers.Count);
        Assert.Equal("Cancelled, so the list below is from the last scan.", vm.StatusMessage);
    }

    [Fact]
    public async Task AScanWithAnError_ShowsWhatItListed_AndSaysSomeMayBeMissing()
    {
        var vm = await ScannedVm(MixedDrivers, exitCode: 1);

        Assert.Equal(4, vm.Drivers.Count);
        Assert.False(vm.ListFailed);
        Assert.Contains("some may be missing", vm.StatusMessage);
    }

    [Fact]
    public async Task ACleanScan_IsDone()
    {
        var vm = await ScannedVm(MixedDrivers);

        Assert.False(vm.ListFailed);
        Assert.False(vm.HasNotScanned);
        Assert.Equal("Done", vm.StatusMessage);
        Assert.Equal("4 drivers found.", vm.Summary);
    }

    // ---------- F5 and Escape (#2597, #2598) ----------

    [Fact]
    public void F5_ListsTheDrivers()
    {
        var vm = NewVm();

        Assert.Same(vm.ListDriversCommand, vm.RefreshOnF5);
    }

    [Fact]
    public void Escape_CancelsTheList_OnlyWhileItLoads()
    {
        var vm = NewVm();
        Assert.Null(vm.EscapeCancel);

        vm.IsBusy = true;

        Assert.Same(vm.CancelCommand, vm.EscapeCancel);
    }
}

// ---------- DriverEntry model ----------

public class DriverEntryTests
{
    [Fact]
    public void DriverDateDisplay_WithDate_ReturnsFormatted()
    {
        var entry = new DriverEntry { DriverDate = new DateTime(2023, 6, 15) };
        Assert.Equal("2023-06-15", entry.DriverDateDisplay);
    }

    [Fact]
    public void DriverDateDisplay_WithNull_ReturnsEmpty()
    {
        var entry = new DriverEntry { DriverDate = null };
        Assert.Equal("", entry.DriverDateDisplay);
    }

    [Fact]
    public void Defaults_AllStringsEmpty()
    {
        var entry = new DriverEntry();
        Assert.Equal("", entry.DeviceName);
        Assert.Equal("", entry.Manufacturer);
        Assert.Equal("", entry.DriverVersion);
        Assert.Null(entry.DriverDate);
        Assert.Null(entry.IsSigned);
        Assert.Equal("", entry.SignatureDisplay);
    }

    // ── Signature state (#1581) ──────────────────────────────────────────────
    //
    // Win32_PnPSignedDriver is named for exactly this and the query dropped it, so the tab that could
    // answer "is this from who it claims?" showed only Manufacturer — a string the driver package supplies
    // about itself. The whole value of these tests is the THIRD state: an absent value must not render as
    // "Unsigned", because on this tab that is an accusation a user may act on.

    [Theory]
    [InlineData(true, "Signed")]
    [InlineData(false, "Unsigned")]
    [InlineData(null, "")]
    public void SignatureDisplay_SaysSignedOrUnsignedAndNothingWhenUnknown(bool? signed, string expected)
        => Assert.Equal(expected, new DriverEntry { IsSigned = signed }.SignatureDisplay);

    /// <summary>
    /// It says "Signed", never "Safe" — a signature identifies the publisher and nothing more, and Windows
    /// loads a signed driver from anyone holding a valid certificate.
    /// </summary>
    [Fact]
    public void SignatureDisplay_NeverClaimsTheDriverIsSafe()
    {
        foreach (var signed in new bool?[] { true, false, null })
        {
            var text = new DriverEntry { IsSigned = signed }.SignatureDisplay;
            Assert.DoesNotContain("safe", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("trusted", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"True\"", true)]      // ConvertTo-Json can surface the value as a string
    [InlineData("\"False\"", false)]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]          // present but unparseable is unknown, not false
    [InlineData("42", null)]
    public void ParseCimBool_KeepsAbsentDistinctFromFalse(string json, bool? expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, DriversViewModel.ParseCimBool(doc.RootElement));
    }

    [Fact]
    public void ParseCimBool_WithAnAbsentProperty_IsUnknown()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"DeviceName":"x"}""");
        var absent = doc.RootElement.TryGetProperty("IsSigned", out var el) ? el : default;
        Assert.Null(DriversViewModel.ParseCimBool(absent));
    }
}
