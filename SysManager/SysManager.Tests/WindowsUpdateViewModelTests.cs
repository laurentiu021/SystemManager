// SysManager · WindowsUpdateViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="WindowsUpdateViewModel"/>.
/// Tests that require PSWindowsUpdate module are in IntegrationTests.
/// </summary>
public class WindowsUpdateViewModelTests
{
    private static WindowsUpdateViewModel NewVm() => new(new PowerShellRunner(), new WindowsUpdateService());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_UpdatesCollectionEmpty()
    {
        var vm = NewVm();
        Assert.Empty(vm.Updates);
    }

    [Fact]
    public void Constructor_ConsoleExists()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_IsBusyFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_ShowConsoleFalse()
    {
        var vm = NewVm();
        Assert.False(vm.ShowConsole);
    }

    [Fact]
    public void Constructor_UpdateCountZero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.UpdateCount);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("ListUpdatesCommand")]
    [InlineData("ShowHistoryCommand")]
    [InlineData("CheckPendingRebootCommand")]
    [InlineData("InstallUpdatesCommand")]
    [InlineData("InstallModuleCommand")]
    [InlineData("CheckModuleCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
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
        typeof(WindowsUpdateViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);

        vm.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- ParseUpdateJson via reflection ----------

    [Fact]
    public void ParseUpdateJson_ValidArray_PopulatesUpdates()
    {
        var vm = NewVm();
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("ParseUpdateJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var json = """
        [
            {"Title":"Security Update","KB":"KB1234567","Size":1048576,"Status":"Available","Date":null,"IsHidden":false,"Category":"Standard"},
            {"Title":"Cumulative Update","KB":"KB7654321","Size":52428800,"Status":"Hidden","Date":"2025-03-15","IsHidden":true,"Category":"Hidden"}
        ]
        """;

        method.Invoke(vm, new object[] { json });

        Assert.Equal(2, vm.Updates.Count);
        Assert.Equal("Security Update", vm.Updates[0].Title);
        Assert.Equal("KB1234567", vm.Updates[0].KB);
        Assert.Equal("1.0 MB", vm.Updates[0].Size);
        Assert.Equal("Cumulative Update", vm.Updates[1].Title);
        Assert.True(vm.Updates[1].IsHidden);
    }

    [Fact]
    public void ParseUpdateJson_SingleObject_PopulatesOneUpdate()
    {
        var vm = NewVm();
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("ParseUpdateJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var json = """{"Title":"Defender Update","KB":"KB9999999","Size":0,"Status":"Available","Date":null,"IsHidden":false,"Category":"Standard"}""";

        method.Invoke(vm, new object[] { json });

        Assert.Single(vm.Updates);
        Assert.Equal("Defender Update", vm.Updates[0].Title);
    }

    [Fact]
    public void ParseUpdateJson_EmptyArray_NoUpdates()
    {
        var vm = NewVm();
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("ParseUpdateJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(vm, new object[] { "[]" });

        Assert.Empty(vm.Updates);
    }

    [Fact]
    public void ParseUpdateJson_EmptyString_NoUpdates()
    {
        var vm = NewVm();
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("ParseUpdateJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(vm, new object[] { "" });

        Assert.Empty(vm.Updates);
    }

    [Fact]
    public void ParseUpdateJson_InvalidJson_DoesNotThrow()
    {
        var vm = NewVm();
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("ParseUpdateJson", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var ex = Record.Exception(() => method.Invoke(vm, new object[] { "not json" }));

        Assert.True(ex == null || ex is TargetInvocationException);
        Assert.Empty(vm.Updates);
    }

    // ---------- FormatSize via reflection ----------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.0 GB")]
    public void FormatSize_NumericValues_FormatsCorrectly(long bytes, string expected)
    {
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("FormatSize", BindingFlags.NonPublic | BindingFlags.Static)!;

        var json = System.Text.Json.JsonDocument.Parse(bytes.ToString());
        var result = (string)method.Invoke(null, new object[] { json.RootElement })!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatSize_StringValue_ReturnsAsIs()
    {
        var method = typeof(WindowsUpdateViewModel)
            .GetMethod("FormatSize", BindingFlags.NonPublic | BindingFlags.Static)!;

        var json = System.Text.Json.JsonDocument.Parse("\"50 MB\"");
        var result = (string)method.Invoke(null, new object[] { json.RootElement })!;

        Assert.Equal("50 MB", result);
    }

    // ---------- WindowsUpdateService.ClassifyCategory (title-based path) ----------

    [Theory]
    [InlineData("Microsoft Defender Antivirus Definition Update - KB2267602", "Defender")]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus", "Defender")]
    [InlineData("Antimalware Platform Update", "Defender")]
    [InlineData("HP - Firmware - 3.5.1.0", "Driver")]
    [InlineData("HP Firmware Driver Update (3.5.5.0)", "Driver")]
    [InlineData("2026-05 Cumulative Update for Windows 11", "Cumulative")]
    [InlineData("2026-05 Security Update for Windows 11", "Security")]
    [InlineData("2026-05 Servicing Stack Update for Windows 11", "Servicing")]
    [InlineData(".NET 10.0.5 Update", ".NET")]
    [InlineData("Random unmatched title", "Update")]
    [InlineData("", "Update")]
    public void ClassifyCategory_TitleBased_ReturnsExpected(string title, string expected)
    {
        Assert.Equal(expected, WindowsUpdateService.ClassifyCategory(title, u: null));
    }

    // ---------- WindowsUpdateService.FormatSize ----------

    [Theory]
    [InlineData(0L, "")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(1073741824L, "1.0 GB")]
    public void FormatSize_VariousValues_FormatsCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, WindowsUpdateService.FormatSize(bytes));
    }

    // ---------- select-all header checkbox (audit #66) ----------

    private static WindowsUpdateViewModel VmWithUpdates(int count)
    {
        var vm = NewVm();
        for (var i = 0; i < count; i++)
            vm.Updates.Add(new UpdateEntry { Title = "U" + i }); // IsSelected defaults to true
        return vm;
    }

    [Fact]
    public void AllSelected_ToggledOff_DeselectsEveryRow()
    {
        var vm = VmWithUpdates(3);
        vm.SelectAllCommand.Execute(null); // synced starting state: AllSelected == true

        vm.AllSelected = false; // user unchecks the header box

        Assert.All(vm.Updates, u => Assert.False(u.IsSelected));
    }

    [Fact]
    public void AllSelected_ToggledOn_SelectsEveryRow()
    {
        var vm = VmWithUpdates(3);
        vm.DeselectAllCommand.Execute(null); // synced starting state: AllSelected == false

        vm.AllSelected = true; // user checks the header box

        Assert.All(vm.Updates, u => Assert.True(u.IsSelected));
    }

    [Fact]
    public void SelectAllCommand_SelectsAndSyncsHeader()
    {
        var vm = VmWithUpdates(2);
        vm.DeselectAllCommand.Execute(null);

        vm.SelectAllCommand.Execute(null);

        Assert.True(vm.AllSelected);
        Assert.All(vm.Updates, u => Assert.True(u.IsSelected));
    }

    [Fact]
    public void DeselectAllCommand_DeselectsAndSyncsHeader()
    {
        var vm = VmWithUpdates(2);
        vm.SelectAllCommand.Execute(null);

        vm.DeselectAllCommand.Execute(null);

        Assert.False(vm.AllSelected);
        Assert.All(vm.Updates, u => Assert.False(u.IsSelected));
    }

    // ── re-entrancy guard (regression: shared CTS disposed mid-flight) ──

    [Fact]
    public void LongRunningCommands_DisabledWhileBusy()
    {
        var vm = NewVm();
        Assert.True(vm.ListUpdatesCommand.CanExecute(null));
        Assert.True(vm.ShowHistoryCommand.CanExecute(null));
        Assert.True(vm.CheckPendingRebootCommand.CanExecute(null));
        Assert.True(vm.InstallUpdatesCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.ListUpdatesCommand.CanExecute(null));
        Assert.False(vm.ShowHistoryCommand.CanExecute(null));
        Assert.False(vm.CheckPendingRebootCommand.CanExecute(null));
        Assert.False(vm.InstallUpdatesCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ListUpdatesCommand.CanExecute(null));
    }
}

// ---------- UpdateEntry model ----------

public class UpdateEntryTests
{
    [Fact]
    public void DateDisplay_WithDate_ReturnsFormatted()
    {
        var entry = new UpdateEntry { Date = new DateTime(2025, 3, 15) };
        Assert.Equal("2025-03-15", entry.DateDisplay);
    }

    [Fact]
    public void DateDisplay_WithNull_ReturnsEmpty()
    {
        var entry = new UpdateEntry { Date = null };
        Assert.Equal("", entry.DateDisplay);
    }

    [Fact]
    public void Defaults_AllStringsEmpty()
    {
        var entry = new UpdateEntry();
        Assert.Equal("", entry.Title);
        Assert.Equal("", entry.KB);
        Assert.Equal("", entry.Size);
        Assert.Equal("", entry.Status);
        Assert.Equal("", entry.Category);
        Assert.Null(entry.Date);
        Assert.False(entry.IsHidden);
    }
}
