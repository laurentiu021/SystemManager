// SysManager · WindowsUpdateViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
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
public class WindowsUpdateViewModelTests
{
    private static WindowsUpdateViewModel NewVm() => new(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());

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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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
    // Driven with isElevated: true and a DECLINED confirm, deliberately. WindowsUpdatePolicyService is
    // sealed with no interface, so it cannot be substituted; on an elevated machine a confirmed Restore
    // would really delete six values under HKLM\…\WindowsUpdate — the developer's own update policy.
    // Declining is the assertion that matters anyway (the gate exists and it blocks), and it reaches the
    // Confirm without ever reaching the registry.

    [Theory]
    [InlineData("RestoreUpdatePolicyCommand")]
    [InlineData("DeferFeatureUpdatesCommand")]
    [InlineData("PauseUpdatesCommand")]
    public void EveryPolicyButton_AsksBeforeItChangesAnything(string commandName)
    {
        var vm = new WindowsUpdateViewModel(
            Substitute.For<IPowerShellRunner>(),
            new WindowsUpdateService(),
            new WindowsUpdatePolicyService(),
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
            new WindowsUpdatePolicyService(),
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

    private static WindowsUpdateViewModel NewVm(bool elevated, out IPowerShellRunner runner)
    {
        runner = Substitute.For<IPowerShellRunner>();
        return new WindowsUpdateViewModel(runner, new WindowsUpdateService(),
                                         new WindowsUpdatePolicyService(),
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
    /// Installing updates without elevation offers to relaunch rather than failing, and installs nothing.
    /// </summary>
    /// <remarks>
    /// The only test here that has to replace the process-wide probe, because <c>InstallUpdatesAsync</c>
    /// calls <c>AdminHelper.IsElevated()</c> directly instead of reading the injected answer its three
    /// siblings use. Filed as an inconsistency rather than changed inside a test-only change.
    /// <para>This gate also sits AFTER the confirmation, unlike the refusals elsewhere in the app, and
    /// that is coherent rather than an oversight: the non-elevated path is not a refusal but a
    /// continuation — it relaunches elevated and carries the user's intent across — so asking first is
    /// what makes the relaunch worth doing. <c>RelaunchAsAdmin</c> returns false when
    /// <c>Application.Current</c> is null, which it is under the test runner, so nothing is spawned.</para>
    /// </remarks>
    /// <remarks>
    /// This used to open with <c>AdminHelper.ForceElevation(false)</c>, because the gate read
    /// <c>AdminHelper.IsElevated()</c> at call time and the injected value could not reach it — one test
    /// pinning elevation a different way from its eight neighbours, and a reason for the whole class to
    /// swap a process-wide static. #2181 routed that gate through the same seam, so this now says what it
    /// means with the constructor argument.
    /// </remarks>
    [Fact]
    public async Task InstallUpdates_WhenNotElevated_OffersToRelaunchAndInstallsNothing()
    {
        var vm = NewVm(elevated: false, out var runner);
        vm.Updates.Add(new UpdateEntry { Title = "KB0000001", IsSelected = true });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // the user agrees to install
        DialogService.Instance = dialog;
        try
        {
            await vm.InstallUpdatesCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("Admin required", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
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
                                            new WindowsUpdatePolicyService(),
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
            Assert.Contains("Admin required", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
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
            new WindowsUpdatePolicyService(), isElevated: null!));

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
