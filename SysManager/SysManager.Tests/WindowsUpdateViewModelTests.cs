// SysManager · WindowsUpdateViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="WindowsUpdateViewModel"/>.
/// Tests that require PSWindowsUpdate module are in IntegrationTests.
/// </summary>
// Serialized: the confirm-gate test swaps the static DialogService.Instance.
[Collection("ProcessWideStatics")]
public sealed class WindowsUpdateViewModelTests : IDisposable
{
    // Every view model here reads and writes its update policy under a throwaway key. Over the default,
    // HKEY_LOCAL_MACHINE, a policy command whose gate regressed would have changed this PC's own Windows Update
    // policy on an elevated run, and the CI runner is elevated (#2740).
    private readonly string _policyRootName = @"Software\SysManagerTests\UpdatePolicyVm_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _policyRoot;

    public WindowsUpdateViewModelTests() =>
        _policyRoot = Registry.CurrentUser.CreateSubKey(_policyRootName, writable: true);

    public void Dispose()
    {
        _policyRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_policyRootName, throwOnMissingSubKey: false);
    }

    private WindowsUpdatePolicyService Policy() => new(_policyRoot);

    private WindowsUpdateViewModel NewVm() => new(new PowerShellRunner(), new WindowsUpdateService(), Policy());

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
    public void Constructor_DefersModuleAvailabilityCheckUntilHistory()
    {
        var vm = NewVm();

        Assert.True(vm.ModuleAvailable);
        Assert.Contains("History", vm.ModuleStatus, StringComparison.Ordinal);
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

    [Fact]
    public void PsWindowsUpdateInstallScript_PinsGalleryAndUsesCurrentUserScope()
    {
        Assert.Contains(
            "https://www.powershellgallery.com/api/v2",
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ErrorActionPreference = 'Stop'",
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install-PackageProvider -Name NuGet -Force -Scope CurrentUser",
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Install-Module -Name PSWindowsUpdate -Force -Scope CurrentUser -Repository PSGallery",
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AllUsers",
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallModule_WhenElevated_RefusesWithoutRunningPowerShell()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => true);

        await vm.InstallModuleCommand.ExecuteAsync(null);

        await runner.DidNotReceiveWithAnyArgs()
            .RunScriptViaPwshAsync(default!, default);
        Assert.Contains("non-administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallModule_WhenNotElevated_RunsPinnedCurrentUserInstall()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);

        await vm.InstallModuleCommand.ExecuteAsync(null);

        await runner.Received(1).RunScriptViaPwshAsync(
            Arg.Is<string>(script =>
                script != null &&
                script.Contains("-Scope CurrentUser", StringComparison.Ordinal) &&
                script.Contains("-Repository PSGallery", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallModule_WhenPowerShellFails_ExposesFailureAndModuleRemediation()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);

        await vm.InstallModuleCommand.ExecuteAsync(null);

        Assert.False(vm.ModuleAvailable);
        Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await runner.Received(1).RunScriptViaPwshAsync(
            WindowsUpdateViewModel.PsWindowsUpdateInstallScript,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowHistory_WhenModuleImportFails_DoesNotReportEmptySuccess()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WindowsUpdateViewModel.HistoryModuleImportFailedExitCode);
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);
        vm.Updates.Add(new UpdateEntry { Title = "Previous result" });
        vm.UpdateCount = 1;
        vm.TableSummary = "1 history entries.";

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.ModuleAvailable);
        Assert.Empty(vm.Updates);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("Update history unavailable.", vm.TableSummary);
        Assert.Contains("not installed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowConsole);
        Assert.NotEqual("Done", vm.StatusMessage);
    }

    [Fact]
    public async Task ShowHistory_WhenQueryFails_PreservesModuleAvailabilityAndClearsPriorState()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WindowsUpdateViewModel.HistoryQueryFailedExitCode);
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);
        vm.Updates.Add(new UpdateEntry { Title = "Previous result" });
        vm.UpdateCount = 1;
        vm.TableSummary = "1 history entries.";

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.ModuleAvailable);
        Assert.Empty(vm.Updates);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("Update history unavailable.", vm.TableSummary);
        Assert.Contains("query failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not installed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowConsole);
        Assert.NotEqual("Done", vm.StatusMessage);
    }

    [Fact]
    public void PsWindowsUpdateHistoryScript_UsesDistinctImportAndQueryExitCodes()
    {
        Assert.Contains(
            $"exit {WindowsUpdateViewModel.HistoryModuleImportFailedExitCode}",
            WindowsUpdateViewModel.PsWindowsUpdateHistoryScript,
            StringComparison.Ordinal);
        Assert.Contains(
            $"exit {WindowsUpdateViewModel.HistoryQueryFailedExitCode}",
            WindowsUpdateViewModel.PsWindowsUpdateHistoryScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-WUHistory -Last 30 -ErrorAction Stop",
            WindowsUpdateViewModel.PsWindowsUpdateHistoryScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Console]::Error.WriteLine",
            WindowsUpdateViewModel.PsWindowsUpdateHistoryScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Write-Error",
            WindowsUpdateViewModel.PsWindowsUpdateHistoryScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowHistory_WhenOutputIsInvalid_DoesNotReportSuccess()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                runner.LineReceived += Raise.Event<Action<PowerShellLine>>(
                    PowerShellLine.Output("not json"));
                return 0;
            });
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);
        vm.Updates.Add(new UpdateEntry { Title = "Previous result" });
        vm.UpdateCount = 1;
        vm.TableSummary = "1 history entries.";

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.ModuleAvailable);
        Assert.Empty(vm.Updates);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("Update history unavailable.", vm.TableSummary);
        Assert.Contains("invalid data", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowConsole);
        Assert.NotEqual("Done", vm.StatusMessage);
    }

    [Fact]
    public async Task ShowHistory_WhenProcessFails_ReportsUnknownAvailabilityAndClearsPriorState()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);
        vm.Updates.Add(new UpdateEntry { Title = "Previous result" });
        vm.UpdateCount = 1;

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.ModuleAvailable);
        Assert.Empty(vm.Updates);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("Update history unavailable.", vm.TableSummary);
        Assert.Contains("could not be confirmed", vm.ModuleStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be loaded", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowConsole);
        Assert.NotEqual("Done", vm.StatusMessage);
    }

    [Fact]
    public async Task ShowHistory_WhenRunnerThrows_ClearsAvailabilityAndPriorState()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("process failed"));
        var vm = new WindowsUpdateViewModel(
            runner,
            new WindowsUpdateService(),
            Policy(),
            static () => false);
        vm.Updates.Add(new UpdateEntry { Title = "Previous result" });
        vm.UpdateCount = 1;

        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.ModuleAvailable);
        Assert.Empty(vm.Updates);
        Assert.Equal(0, vm.UpdateCount);
        Assert.Equal("Update history unavailable.", vm.TableSummary);
        Assert.Contains(
            "could not be confirmed",
            vm.ModuleStatus,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.ShowConsole);
        Assert.NotEqual("Done", vm.StatusMessage);
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
        Assert.Equal("1.0 MB", vm.Updates[0].SizeDisplay);
        Assert.Equal(1_048_576, vm.Updates[0].SizeBytes);
        Assert.Equal(52_428_800, vm.Updates[1].SizeBytes);
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

    // ---------- a rescan keeps what the user unticked (#2874) ----------

    /// <summary>
    /// A view model whose scans return the given updates in turn, each time as new rows, as Windows Update does,
    /// and whose history read fails, which still clears the grid as a look at the history does.
    /// </summary>
    private WindowsUpdateViewModel VmOverScans(params string[][] scans)
    {
        var wu = Substitute.For<IWindowsUpdateService>();
        var call = 0;
        wu.ScanAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var ids = scans[Math.Min(call++, scans.Length - 1)];
            return Task.FromResult<IReadOnlyList<UpdateEntry>>(
                [.. ids.Select(id => new UpdateEntry { Title = "Update " + id, UpdateId = id })]);
        });
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(WindowsUpdateViewModel.HistoryModuleImportFailedExitCode);
        return new WindowsUpdateViewModel(runner, wu, Policy(), () => false);
    }

    private static bool Ticked(WindowsUpdateViewModel vm, string id) =>
        Assert.Single(vm.Updates, u => u.UpdateId == id).IsSelected;

    [Fact]
    public async Task ListingAgain_KeepsTheUpdatesTheUserUnticked()
    {
        var vm = VmOverScans(["a", "b", "c"]);
        await vm.ListUpdatesCommand.ExecuteAsync(null);
        Assert.Single(vm.Updates, u => u.UpdateId == "b").IsSelected = false;

        await vm.ListUpdatesCommand.ExecuteAsync(null);   // F5 runs this same command

        Assert.True(Ticked(vm, "a"));
        Assert.False(Ticked(vm, "b"));
        Assert.True(Ticked(vm, "c"));
        Assert.False(vm.AllSelected);   // the header follows the rows
    }

    [Fact]
    public async Task ListingAfterALookAtTheHistory_KeepsTheUpdatesTheUserUnticked()
    {
        var vm = VmOverScans(["a", "b"]);
        await vm.ListUpdatesCommand.ExecuteAsync(null);
        Assert.Single(vm.Updates, u => u.UpdateId == "a").IsSelected = false;

        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Empty(vm.Updates);   // the history view took the grid, so the ticks were not left there
        await vm.ListUpdatesCommand.ExecuteAsync(null);

        Assert.False(Ticked(vm, "a"));
        Assert.True(Ticked(vm, "b"));
    }

    [Fact]
    public async Task AfterAnInstall_TheUpdatesLeftOutStayUnticked_AndANewOneIsTicked()
    {
        // What was ticked got installed and is gone from the next scan. What was left out must not come back ticked,
        // one press of Install selected away; an update the user has never seen takes the default.
        var vm = VmOverScans(["a", "b"], ["b", "new"]);
        await vm.ListUpdatesCommand.ExecuteAsync(null);
        Assert.Single(vm.Updates, u => u.UpdateId == "b").IsSelected = false;

        await vm.ListUpdatesCommand.ExecuteAsync(null);

        Assert.False(Ticked(vm, "b"));
        Assert.True(Ticked(vm, "new"));
    }

    [Fact]
    public async Task UntickingEverything_IsKeptToo()
    {
        // Nothing ticked is a decision, not a first look: reading it as one would tick everything again.
        var vm = VmOverScans(["a", "b"]);
        await vm.ListUpdatesCommand.ExecuteAsync(null);
        vm.DeselectAllCommand.Execute(null);

        await vm.ListUpdatesCommand.ExecuteAsync(null);

        Assert.All(vm.Updates, u => Assert.False(u.IsSelected));
        Assert.False(vm.AllSelected);
    }

    [Fact]
    public async Task TheFirstList_TicksEverything()
    {
        var vm = VmOverScans(["a", "b"]);

        await vm.ListUpdatesCommand.ExecuteAsync(null);

        Assert.All(vm.Updates, u => Assert.True(u.IsSelected));
        Assert.True(vm.AllSelected);
    }

    // ---------- select-all header checkbox (audit #66) ----------

    private WindowsUpdateViewModel VmWithUpdates(int count)
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

    // Check/Install Module stream through the same shared runner and console, so they are
    // gated on NotBusy too — otherwise starting one while another update operation runs
    // would interleave output on the shared console (and race on the shared CTS).
    [Fact]
    public void ModuleCommands_DisabledWhileBusy()
    {
        var vm = NewVm();
        Assert.True(vm.CheckModuleCommand.CanExecute(null));
        Assert.True(vm.InstallModuleCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.CheckModuleCommand.CanExecute(null));
        Assert.False(vm.InstallModuleCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.CheckModuleCommand.CanExecute(null));
        Assert.True(vm.InstallModuleCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckModule_MovesTheBar_WhileItRuns()
    {
        // The bar beside the status line binds the download's value, so a check that set IsBusy alone showed whatever
        // the last download had left there, standing still (#2812).
        var probe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunScriptViaPwshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(probe.Task);
        var vm = new WindowsUpdateViewModel(runner, Substitute.For<IWindowsUpdateService>(),
            Policy(), () => false);

        var check = vm.CheckModuleCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsProgressIndeterminate);
        }
        finally
        {
            // Ended either way, so a failure here leaves no check running.
            probe.SetResult(0);
            await check;
        }

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    // ── Confirmation-gate test (installing updates must route through Confirm) ──

    [Fact]
    public void InstallUpdates_WhenUserDeclinesConfirm_DoesNotInstall()
    {
        var vm = NewVm();
        vm.Updates.Add(new UpdateEntry { Title = "KB123", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            vm.InstallUpdatesCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining returns before the elevation/install path, so nothing started.
            Assert.False(vm.IsBusy);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── "Restore default" is the destructive one of the three policy buttons ──────────────────────
    //
    // DeferFeatureUpdates and PauseUpdates confirmed from the start; Restore — which DISCARDS whatever
    // deferral or pause those two produced — was the one that did not ask. All three are pinned here so
    // the asymmetry cannot come back.
    //
    // Driven with isElevated: true and a DECLINED confirm, deliberately: declining is the assertion that
    // matters (the gate exists and it blocks), and it reaches the Confirm without ever reaching the registry.
    // The policy service writes under the class's throwaway key, so even a confirmed Restore could not reach
    // this PC's own update policy.

    [Theory]
    [InlineData("RestoreUpdatePolicyCommand")]
    [InlineData("DeferFeatureUpdatesCommand")]
    [InlineData("PauseUpdatesCommand")]
    public void EveryPolicyButton_AsksBeforeItChangesAnything(string commandName)
    {
        var vm = new WindowsUpdateViewModel(
            Substitute.For<IPowerShellRunner>(),
            new WindowsUpdateService(),
            Policy(),
            static () => true);

        var before = vm.PolicySummary;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var command = (System.Windows.Input.ICommand)vm.GetType().GetProperty(commandName)!.GetValue(vm)!;
            command.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining returns before the policy write, so the summary is untouched.
            Assert.Equal(before, vm.PolicySummary);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void RestoreUpdatePolicy_TellsTheUserWhatStateIsBeingDiscarded()
    {
        // A confirmation that says only "are you sure?" leaves the user guessing what they are giving
        // up. The prompt quotes the current policy summary — the very thing Restore erases.
        var vm = new WindowsUpdateViewModel(
            Substitute.For<IPowerShellRunner>(),
            new WindowsUpdateService(),
            Policy(),
            static () => true);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        string? shown = null;
        dialog.Confirm(Arg.Do<string>(m => shown = m), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.RestoreUpdatePolicyCommand.Execute(null);

            Assert.NotNull(shown);
            Assert.Contains("default", shown!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(vm.PolicySummary, shown!, StringComparison.Ordinal);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ---------- a policy that could not be read is not "Default" (#2504) ----------

    [Fact]
    public void DescribePolicy_SaysWhenThePolicyCouldNotBeRead()
    {
        Assert.Equal("The update policy could not be read, so any deferral or pause is not known.",
            WindowsUpdateViewModel.DescribePolicy(null));
        Assert.Equal("Default — Windows manages update timing.",
            WindowsUpdateViewModel.DescribePolicy(new WindowsUpdatePolicy(false, 0, false, null)));
    }

    [Fact]
    public void AnUnreadablePolicy_IsNotShownAsDefault_AndRestoreQuotesThat()
    {
        // The policy key is made unreadable for real, on a redirected root, with a deny entry for this user that is
        // removed again afterwards. The confirm is declined, so nothing is written.
        var rootName = @"Software\SysManagerTests\WUPolicyVm_" + Guid.NewGuid().ToString("N");
        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        var policyService = new WindowsUpdatePolicyService(root);
        Assert.True(policyService.DeferFeatureUpdates(30));   // the premise: there is a deferral to hide

        using var policy = root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
            Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
            System.Security.AccessControl.RegistryRights.ReadKey | System.Security.AccessControl.RegistryRights.ChangePermissions)!;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var deny = new System.Security.AccessControl.RegistryAccessRule(identity.User!,
            System.Security.AccessControl.RegistryRights.QueryValues | System.Security.AccessControl.RegistryRights.EnumerateSubKeys,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = policy.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
        security.AddAccessRule(deny);
        policy.SetAccessControl(security);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        string? shown = null;
        dialog.Confirm(Arg.Do<string>(m => shown = m), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            var vm = new WindowsUpdateViewModel(
                Substitute.For<IPowerShellRunner>(), new WindowsUpdateService(), policyService, static () => true);

            Assert.Equal(WindowsUpdateViewModel.DescribePolicy(null), vm.PolicySummary);

            vm.RestoreUpdatePolicyCommand.Execute(null);

            Assert.NotNull(shown);
            Assert.Contains("could not be read", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("Windows manages update timing", shown, StringComparison.Ordinal);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            security.RemoveAccessRuleSpecific(deny);
            policy.SetAccessControl(security);
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { /* best-effort: a leftover test key under HKCU */ }
        }
    }

    // ---------- progress reporting ----------

    [Fact]
    public void RunnerProgress_SwitchesTheBarOutOfIndeterminateMode()
    {
        // WPF ignores ProgressBar.Value entirely while IsIndeterminate is true, so assigning Progress
        // without clearing the flag leaves the bar sweeping and never filling — which is what made the
        // Value binding added in v1.58.2 inert on this tab. Every command sets the flag true on entry
        // and clears it in its own finally, so the handler only narrows the indeterminate window to
        // "before the first real percentage arrives".
        using var vm = NewVm();
        vm.IsProgressIndeterminate = true;

        InvokeRunnerProgress(vm, 42);

        Assert.Equal(42, vm.Progress);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void RunnerProgress_KeepsReportingLaterPercentages()
    {
        // Once determinate it must stay determinate for the rest of the operation, and the value has to
        // track each report rather than sticking at the first one.
        using var vm = NewVm();
        vm.IsProgressIndeterminate = true;

        InvokeRunnerProgress(vm, 10);
        InvokeRunnerProgress(vm, 75);
        InvokeRunnerProgress(vm, 100);

        Assert.Equal(100, vm.Progress);
        Assert.False(vm.IsProgressIndeterminate);
    }

    /// <summary>
    /// Drives the private runner-progress handler. The event is raised by <see cref="PowerShellRunner"/>
    /// from a live PowerShell progress stream, which a unit test cannot produce, so the handler is
    /// invoked directly — the same reflection approach already used elsewhere in this file.
    /// </summary>
    private static void InvokeRunnerProgress(WindowsUpdateViewModel vm, int percent)
        => typeof(WindowsUpdateViewModel)
            .GetMethod("OnRunnerProgressChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [percent]);

    // ── Elevation gates ──────────────────────────────────────────────────────
    //
    // Four gated paths in three shapes, and none was asserted (#2171). Elevation was read from whatever
    // host ran the suite: CI's runner IS elevated, a developer's shell is not, so each of these exercised
    // a different branch depending on where it ran while looking identical either way.
    //
    // Three of them use the view model's OWN seam — the internal constructor takes a Func<bool>, so the
    // answer is decided per instance with no process-wide state and no serialized collection needed. The
    // fourth, InstallUpdates, reads AdminHelper.IsElevated() directly and bypasses that seam, so its test
    // is the only one here that has to replace the static probe. That inconsistency is filed separately.
    //
    // The runner is substituted throughout: past these gates the real one installs a PowerShell module or
    // Windows updates, and a test must not be one gate away from doing that.

    private WindowsUpdateViewModel NewVm(bool elevated, out IPowerShellRunner runner)
    {
        runner = Substitute.For<IPowerShellRunner>();
        return new WindowsUpdateViewModel(runner, new WindowsUpdateService(),
                                         Policy(),
                                         isElevated: () => elevated);
    }

    private static void ExecutePolicy(WindowsUpdateViewModel vm, string which)
    {
        switch (which)
        {
            case "Defer": vm.DeferFeatureUpdatesCommand.Execute(null); break;
            case "Pause": vm.PauseUpdatesCommand.Execute(null); break;
            case "Restore": vm.RestoreUpdatePolicyCommand.Execute(null); break;
            default: throw new ArgumentOutOfRangeException(nameof(which), which, "unknown policy command");
        }
    }

    [Theory]
    [InlineData("Defer")]
    [InlineData("Pause")]
    [InlineData("Restore")]
    public void PolicyCommand_WhenNotElevated_SaysSoAndNeverPromptsConfirm(string which)
    {
        var vm = NewVm(elevated: false, out _);
        Assert.False(vm.IsElevated, "the injected answer must reach the view model");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // would say yes if asked
        DialogService.Instance = dialog;
        try
        {
            ExecutePolicy(vm, which);

            Assert.Contains("administrator", vm.PolicySummary, StringComparison.OrdinalIgnoreCase);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    /// <summary>
    /// Elevated, the policy commands get as far as asking — the half a refusal test cannot show. They
    /// decline, because past the dialog these write real Windows Update policy keys.
    /// </summary>
    [Theory]
    [InlineData("Defer")]
    [InlineData("Pause")]
    [InlineData("Restore")]
    public void PolicyCommand_WhenElevated_AsksBeforeChangingPolicy(string which)
    {
        var vm = NewVm(elevated: true, out _);
        Assert.True(vm.IsElevated, "the injected answer must reach the view model");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // decline, so nothing is written
        DialogService.Instance = dialog;
        try
        {
            ExecutePolicy(vm, which);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.DoesNotContain("Changing update policy requires", vm.PolicySummary);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    /// <summary>
    /// Installing PSWindowsUpdate is refused when SysManager IS elevated — the one gate in the app whose
    /// refusal is on the ELEVATED side.
    /// </summary>
    /// <remarks>
    /// The module installs per user, so an elevated session would put it under the administrator's profile
    /// where the user's normal session cannot see it. Hence the inverted check, and hence a test that
    /// decides elevation is ON rather than off.
    /// </remarks>
    [Fact]
    public async Task InstallModule_WhenElevated_RefusesBecauseTheModuleIsPerUser()
    {
        var vm = NewVm(elevated: true, out var runner);

        await vm.InstallModuleCommand.ExecuteAsync(null);

        Assert.Contains("non-administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsBusy, "the refusal must not leave the tab looking busy");
        await runner.DidNotReceiveWithAnyArgs().RunScriptViaPwshAsync(default!);
    }

    /// <summary>
    /// Not elevated, installing PSWindowsUpdate is allowed and the install script reaches the runner —
    /// the branch the inverted gate exists to permit.
    /// </summary>
    /// <remarks>
    /// Asserted on the script rather than on a call count. A count of one was the first attempt and it
    /// failed at TWO: the command installs the module and then probes for it, so the runner is used twice.
    /// Naming the install script says what has to happen and does not break when the probe changes.
    /// </remarks>
    [Fact]
    public async Task InstallModule_WhenNotElevated_ReachesTheRunner()
    {
        var vm = NewVm(elevated: false, out var runner);

        await vm.InstallModuleCommand.ExecuteAsync(null);

        Assert.DoesNotContain("non-administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        await runner.Received(1).RunScriptViaPwshAsync(
            Arg.Is<string>(s => s.Contains("Install-Module -Name PSWindowsUpdate", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Installing updates without elevation says what is needed before asking anything, and installs nothing.
    /// </summary>
    /// <remarks>
    /// The gate sits before the confirmation, like the refusals elsewhere in the app. It used to sit after it
    /// and relaunch SysManager as administrator, on the reasoning that the relaunch carried the user's intent
    /// across. It did not: the new window had no selection, so the user approved an install that never ran
    /// (#2505). Now nothing is asked until an install can actually follow the answer.
    /// <para>This used to open with <c>AdminHelper.ForceElevation(false)</c>, because the gate read
    /// <c>AdminHelper.IsElevated()</c> at call time and the injected value could not reach it — one test
    /// pinning elevation a different way from its eight neighbours, and a reason for the whole class to
    /// swap a process-wide static. #2181 routed that gate through the same seam, so this now says what it
    /// means with the constructor argument.</para>
    /// </remarks>
    [Fact]
    public async Task InstallUpdates_WhenNotElevated_SaysSoBeforeAsking_AndInstallsNothing()
    {
        var vm = NewVm(elevated: false, out var runner);
        vm.Updates.Add(new UpdateEntry { Title = "KB0000001", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // the user would agree to install
        DialogService.Instance = dialog;
        try
        {
            await vm.InstallUpdatesCommand.ExecuteAsync(null);

            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.StartsWith("Installing updates needs administrator rights.", vm.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("select them again", vm.StatusMessage, StringComparison.Ordinal);
            Assert.False(vm.IsBusy, "the install never started, so the tab must not look busy");
            Assert.Equal("", vm.Updates[0].Status);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    /// <summary>
    /// The install gate ASKS the injected probe, rather than reading the process behind its back.
    /// </summary>
    /// <remarks>
    /// A mutation cannot prove this by outcome. On a non-elevated host the injected value and
    /// <c>AdminHelper.IsElevated()</c> both answer false, so reverting the gate to the static call leaves
    /// every assertion above green — and the elevated direction is not open to a test, because past the
    /// gate the command hands real updates to a real <c>WindowsUpdateService</c>.
    /// <para>Counting the calls decides it instead. The constructor reads the probe once to seed
    /// <see cref="WindowsUpdateViewModel.IsElevated"/>; the gate reading it too makes two. One means the
    /// gate went around the seam, which is the state #2181 describes.</para>
    /// </remarks>
    [Fact]
    public async Task InstallUpdates_AsksTheInjectedProbeRatherThanTheProcess()
    {
        var asked = 0;
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = new WindowsUpdateViewModel(runner, new WindowsUpdateService(),
                                            Policy(),
                                            isElevated: () => { asked++; return false; });
        Assert.Equal(1, asked);   // the constructor's read, seeding the property

        vm.Updates.Add(new UpdateEntry { Title = "KB0000001", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.InstallUpdatesCommand.ExecuteAsync(null);

            Assert.Equal(2, asked);
            Assert.StartsWith("Installing updates needs administrator rights.", vm.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void Constructor_WithoutAnElevationProbe_Throws()
        => Assert.Throws<ArgumentNullException>(() => new WindowsUpdateViewModel(
            Substitute.For<IPowerShellRunner>(), new WindowsUpdateService(),
            Policy(), isElevated: null!));

    [Fact]
    public async Task InstallUpdates_WhileAnotherSystemChangeRuns_InstallsNothing()
    {
        // #2484. An install services the running image, and it could start in the middle of an SFC or DISM
        // repair, or of a Reset Windows Update that stops the services it installs through.
        var wu = Substitute.For<IWindowsUpdateService>();
        var vm = new WindowsUpdateViewModel(
            Substitute.For<IPowerShellRunner>(), wu, Policy(), static () => true);
        vm.Updates.Add(new UpdateEntry { Title = "2026-09 Cumulative Update", IsSelected = true });
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Windows Update reset");
        Assert.NotNull(held);

        await vm.InstallUpdatesCommand.ExecuteAsync(null);

        await wu.DidNotReceiveWithAnyArgs().InstallAsync(default!, default);
        Assert.Equal("Cannot start — Windows Update reset is already running.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    // ---------- F5 and Escape (#2597, #2598) ----------

    [Fact]
    public void F5_ListsTheUpdates()
    {
        var vm = NewVm();

        Assert.Same(vm.ListUpdatesCommand, vm.RefreshOnF5);
    }

    [Fact]
    public void Escape_CancelsWhatRuns_OnlyWhileSomethingRuns()
    {
        var vm = NewVm();
        Assert.Null(vm.EscapeCancel);

        vm.IsBusy = true;

        Assert.Same(vm.CancelCommand, vm.EscapeCancel);
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
        Assert.Equal("", entry.SizeDisplay);
        Assert.Equal(0, entry.SizeBytes);
        Assert.Equal("", entry.Status);
        Assert.Equal("", entry.Category);
        Assert.Null(entry.Date);
        Assert.False(entry.IsHidden);
    }
}
