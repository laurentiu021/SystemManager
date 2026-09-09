// SysManager · DriversViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
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

    // ---------- ParseDriverJson via reflection ----------

    [Fact]
    public void ParseDriverJson_ValidArray_PopulatesDrivers()
    {
        var vm = NewVm();
        var method = typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var json = """
        [
            {"DeviceName":"Intel HD","Manufacturer":"Intel","DriverVersion":"10.0.1","DriverDate":"/Date(1609459200000)/"},
            {"DeviceName":"NVIDIA GPU","Manufacturer":"NVIDIA","DriverVersion":"31.0.2","DriverDate":null}
        ]
        """;

        method.Invoke(vm, new object[] { json });

        Assert.Equal(2, vm.Drivers.Count);
        Assert.Equal("Intel HD", vm.Drivers[0].DeviceName);
        Assert.Equal("NVIDIA GPU", vm.Drivers[1].DeviceName);
    }

    [Fact]
    public void ParseDriverJson_CarriesTheSignatureStateThrough()
    {
        var vm = NewVm();
        var method = typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // The third entry omits IsSigned entirely, which is the case that must stay blank rather than
        // become "Unsigned" — a query change or a Windows edition that stops reporting it lands here.
        var json = """
        [
            {"DeviceName":"Signed one","Manufacturer":"Intel","DriverVersion":"1","IsSigned":true},
            {"DeviceName":"Unsigned one","Manufacturer":"Somebody","DriverVersion":"2","IsSigned":false},
            {"DeviceName":"Unknown one","Manufacturer":"Somebody","DriverVersion":"3"}
        ]
        """;

        method.Invoke(vm, new object[] { json });

        Assert.Equal(3, vm.Drivers.Count);
        Assert.Equal("Signed", vm.Drivers[0].SignatureDisplay);
        Assert.Equal("Unsigned", vm.Drivers[1].SignatureDisplay);
        Assert.Equal("", vm.Drivers[2].SignatureDisplay);
        Assert.Null(vm.Drivers[2].IsSigned);
    }

    [Fact]
    public void ParseDriverJson_SingleObject_PopulatesOneDriver()
    {
        var vm = NewVm();
        var method = typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var json = """{"DeviceName":"Realtek Audio","Manufacturer":"Realtek","DriverVersion":"6.0.1","DriverDate":null}""";

        method.Invoke(vm, new object[] { json });

        Assert.Single(vm.Drivers);
        Assert.Equal("Realtek Audio", vm.Drivers[0].DeviceName);
    }

    [Fact]
    public void ParseDriverJson_EmptyString_NoDrivers()
    {
        var vm = NewVm();
        var method = typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(vm, new object[] { "" });

        Assert.Empty(vm.Drivers);
    }

    [Fact]
    public void ParseDriverJson_InvalidJson_DoesNotThrow()
    {
        var vm = NewVm();
        var method = typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var ex = Record.Exception(() => method.Invoke(vm, new object[] { "not json at all" }));

        // TargetInvocationException wraps internal exceptions; parse errors are caught internally
        Assert.True(ex == null || ex is TargetInvocationException);
        Assert.Empty(vm.Drivers);
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

    private static void Parse(DriversViewModel vm, string json) =>
        typeof(DriversViewModel)
            .GetMethod("ParseDriverJson", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [json]);

    private const string MixedDrivers = """
    [
        {"DeviceName":"Generic Monitor","Manufacturer":"Microsoft","DriverVersion":"10.0.1","DriverDate":null},
        {"DeviceName":"Root Hub","Manufacturer":"Windows","DriverVersion":"10.0.1","DriverDate":null},
        {"DeviceName":"NVIDIA GPU","Manufacturer":"NVIDIA","DriverVersion":"31.0.2","DriverDate":null},
        {"DeviceName":"Realtek Audio","Manufacturer":"Realtek","DriverVersion":"6.0.1","DriverDate":null}
    ]
    """;

    [Fact]
    public void HideSystemDrivers_Off_ShowsEveryDriver()
    {
        var vm = NewVm();
        Parse(vm, MixedDrivers);

        Assert.False(vm.HideSystemDrivers);
        Assert.Equal(4, vm.Drivers.Count);
    }

    [Fact]
    public void HideSystemDrivers_On_KeepsOnlyThirdPartyDrivers()
    {
        var vm = NewVm();
        Parse(vm, MixedDrivers);

        vm.HideSystemDrivers = true;

        Assert.Equal(2, vm.Drivers.Count);
        Assert.All(vm.Drivers, d => Assert.False(DriversViewModel.IsSystemDriver(d)));
    }

    [Fact]
    public void HideSystemDrivers_Toggling_RestoresTheFullList()
    {
        var vm = NewVm();
        Parse(vm, MixedDrivers);

        vm.HideSystemDrivers = true;
        vm.HideSystemDrivers = false;

        Assert.Equal(4, vm.Drivers.Count);
    }

    [Fact]
    public void HideSystemDrivers_On_SummaryReportsBothCounts()
    {
        var vm = NewVm();
        Parse(vm, MixedDrivers);

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
    public void WhenTheFilterHidesEveryDriver_TheFilteredStateIsShownNotTheScanPrompt()
    {
        // On a machine where every driver is Microsoft-supplied, the single shared empty state told
        // the user to click a button they had already clicked. Same defect as the Logs tab's.
        var vm = NewVm();
        Parse(vm, """
        [
            {"DeviceName":"Generic Monitor","Manufacturer":"Microsoft","DriverVersion":"10.0.1","DriverDate":null}
        ]
        """);

        vm.HideSystemDrivers = true;

        Assert.Empty(vm.Drivers);
        Assert.True(vm.HasNoResults);
    }

    [Fact]
    public void WithDriversShown_NeitherEmptyStateIsActive()
    {
        var vm = NewVm();
        Parse(vm, MixedDrivers);

        Assert.False(vm.HasNoResults);
        Assert.NotEmpty(vm.Drivers);
    }

    [Fact]
    public void AFailedScanThatFoundNothing_DoesNotClaimTheFilterHidThings()
    {
        // Zero drivers parsed is not "filtered to nothing" — HasNoResults must stay false so the
        // wrong advice ("untick the checkbox") is never shown.
        var vm = NewVm();
        vm.HideSystemDrivers = true;

        Parse(vm, "[]");

        Assert.False(vm.HasNoResults);
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
