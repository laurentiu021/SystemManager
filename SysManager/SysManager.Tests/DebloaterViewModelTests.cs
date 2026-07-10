// SysManager · DebloaterViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DebloaterViewModel"/> empty-state copy — the centered empty
/// state must switch from "press Refresh" (never scanned) to "none found" (scanned,
/// zero results) so it never contradicts the status bar. Constructed with a mocked
/// <see cref="IPowerShellRunner"/> so no real PowerShell runs.
/// </summary>
public class DebloaterViewModelTests
{
    private static DebloaterViewModel NewVm() =>
        new(new DebloaterService(Substitute.For<IPowerShellRunner>()));

    private static StoreApp Removable(string name) => new()
    {
        Name = name,
        DisplayName = name,
        PackageFullName = $"{name}_1.0.0.0_x64__8wekyb3d8bbwe",
        PackageFamilyName = $"{name}_8wekyb3d8bbwe",
        Publisher = "CN=Test",
        Version = "1.0.0.0",
        IsProtected = false,
        IsSelected = true,
    };

    [Fact]
    public void EmptyState_BeforeScan_PromptsRefresh()
    {
        var vm = NewVm();
        Assert.False(vm.HasScanned);
        Assert.Equal("No apps loaded", vm.EmptyTitle);
        Assert.Contains("Refresh", vm.EmptyMessage);
    }

    [Fact]
    public void EmptyState_AfterScan_SwitchesToNoneFound()
    {
        var vm = NewVm();

        // Simulate a completed scan (the flag the RefreshAsync path sets on completion).
        vm.HasScanned = true;

        Assert.Equal("No Store apps found", vm.EmptyTitle);
        Assert.DoesNotContain("Refresh", vm.EmptyMessage);
    }

    // ---------- removal-batch resilience (regression P2 #44) ----------

    [Fact]
    public async Task RemoveSelected_RunspaceFault_FailsRowsAndCompletes_NoEscape()
    {
        // Regression (P2 #44): RemoveAsync runs PowerShell; a runspace-level fault
        // (InvalidOperationException — e.g. PSInvalidOperationException from a failed
        // runspace open) is NOT the RuntimeException the service catches. Before the fix
        // the whole RemoveSelected loop had a single OperationCanceledException catch, so
        // such a fault escaped to the global dispatcher MessageBox, aborted the batch, and
        // left later rows frozen at "Removing…". Now each row is guarded: the command must
        // complete without throwing and mark BOTH selected apps "Failed".
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Collection<PSObject>>>(_ => throw new InvalidOperationException("runspace is not open"));

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;
        try
        {
            var vm = new DebloaterViewModel(new DebloaterService(runner));
            var a = Removable("Contoso.AppA");
            var b = Removable("Contoso.AppB");
            vm.Apps.Add(a);
            vm.Apps.Add(b);

            var ex = await Record.ExceptionAsync(() => vm.RemoveSelectedCommand.ExecuteAsync(null));

            Assert.Null(ex);                       // must not fault the command
            Assert.Equal("Failed", a.Status);      // first row failed, not frozen at "Removing…"
            Assert.Equal("Failed", b.Status);      // batch continued to the second row
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }
}
