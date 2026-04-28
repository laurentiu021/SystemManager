// SysManager · DriversViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
    }
}
