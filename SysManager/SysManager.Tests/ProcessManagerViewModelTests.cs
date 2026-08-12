// SysManager · ProcessManagerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessManagerViewModel"/>. Verifies initial state,
/// commands, and filter logic. Sorting is handled by DataGrid column headers.
/// </summary>
// Serialized: the kill-guard tests swap the static DialogService.Instance, which is
// process-wide shared state.
[Collection("ProcessWideStatics")]
public class ProcessManagerViewModelTests
{
    [Fact]
    public void Constructor_Commands_Exist()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.KillProcessCommand);
        Assert.NotNull(vm.OpenFileLocationCommand);
    }

    [Fact]
    public void Constructor_Collections_NotNull()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.NotNull(vm.Processes);
        Assert.NotNull(vm.FilteredProcesses);
    }

    [Fact]
    public void FilterText_DefaultEmpty()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void FilterText_CanBeChanged()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        vm.FilterText = "chrome";
        Assert.Equal("chrome", vm.FilterText);
    }

    [Fact]
    public void Summary_HasDefaultValue()
    {
        var vm = new ProcessManagerViewModel(new Services.ProcessManagerService());
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    // ── ReconcileInto (regression: 1 Hz refresh preserves instances/selection) ──

    private static ProcessEntry Proc(int pid, long mem = 0, double cpu = 0) =>
        new() { Pid = pid, Name = $"p{pid}", MemoryBytes = mem, CpuPercent = cpu };

    [Fact]
    public void ReconcileInto_SurvivingPid_KeepsSameInstanceAndUpdatesMetrics()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var original = Proc(100, mem: 10, cpu: 1);
        original.Icon = null; // identity field set once; must not be touched on update
        target.Add(original);

        // A fresh snapshot for the same PID with new metrics.
        var snapshot = new List<ProcessEntry> { Proc(100, mem: 999, cpu: 42) };

        ProcessManagerViewModel.ReconcileInto(target, snapshot);

        Assert.Single(target);
        // Same instance is reused — this is what lets the DataGrid keep selection.
        Assert.Same(original, target[0]);
        // Volatile metrics updated in place.
        Assert.Equal(999, target[0].MemoryBytes);
        Assert.Equal(42, target[0].CpuPercent);
    }

    [Fact]
    public void ReconcileInto_AddsNewAndRemovesDeadPids()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var keep = Proc(1);
        var dead = Proc(2);
        target.Add(keep);
        target.Add(dead);

        // PID 2 exited, PID 3 is new.
        var snapshot = new List<ProcessEntry> { Proc(1), Proc(3) };

        ProcessManagerViewModel.ReconcileInto(target, snapshot);

        var pids = target.Select(p => p.Pid).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 3 }, pids);
        Assert.Same(keep, target.First(p => p.Pid == 1)); // survivor instance preserved
        Assert.DoesNotContain(dead, target);
    }

    [Fact]
    public void ReconcileInto_ReusedPid_ReplacesStaleIdentity()
    {
        // Regression: a PID alone is not a stable identity — Windows reuses PIDs, so the same
        // number can belong to a DIFFERENT process between 1 Hz polls. Reconcile must not keep the
        // old process's identity (name/icon) on that row, or the Kill confirm would name the old
        // process while KillProcess(entry.Pid) terminates the new one (a mis-kill).
        var target = new BulkObservableCollection<ProcessEntry>();
        var old = Proc(100, mem: 10, cpu: 1);
        old.Name = "old-process";
        old.StartTime = new DateTime(2020, 1, 1);
        target.Add(old);

        // Same PID 100, but a different start time → the OS reused the PID for a new process.
        var fresh = Proc(100, mem: 50, cpu: 5);
        fresh.Name = "new-process";
        fresh.StartTime = new DateTime(2021, 6, 1);

        ProcessManagerViewModel.ReconcileInto(target, new List<ProcessEntry> { fresh });

        Assert.Single(target);                              // no duplicate PID row
        Assert.Same(fresh, target[0]);                      // stale instance dropped, fresh kept
        Assert.Equal("new-process", target[0].Name);        // correct identity shown
        Assert.Equal(new DateTime(2021, 6, 1), target[0].StartTime);
        Assert.DoesNotContain(old, target);
    }

    // ── SyncOrdered (regression: filtered view reorders without a Reset) ──

    [Fact]
    public void SyncOrdered_ReordersInPlacePreservingInstances()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var a = Proc(1);
        var b = Proc(2);
        var c = Proc(3);
        target.Add(a);
        target.Add(b);
        target.Add(c);

        // Desired order c, a (b dropped by filter).
        ProcessManagerViewModel.SyncOrdered(target, new List<ProcessEntry> { c, a });

        Assert.Equal(2, target.Count);
        Assert.Same(c, target[0]);
        Assert.Same(a, target[1]);
        Assert.DoesNotContain(b, target);
    }

    [Fact]
    public void SyncOrdered_AddsMissingAtDesiredPosition()
    {
        var target = new BulkObservableCollection<ProcessEntry>();
        var a = Proc(1);
        target.Add(a);
        var b = Proc(2);

        // b should be inserted before a.
        ProcessManagerViewModel.SyncOrdered(target, new List<ProcessEntry> { b, a });

        Assert.Same(b, target[0]);
        Assert.Same(a, target[1]);
    }

    // ── Kill guard (regression: provenance was treated as criticality) ──
    //
    // IsKernelCritical used to return true for ANY entry whose SafetyLevel was "System". That value
    // is PROVENANCE from the description database ("known Windows component"), and 59 of its 108
    // entries carry it — including notepad, calc, mspaint, Taskmgr, regedit and explorer. So the app
    // refused to end Notepad and told the user it "would cause a system crash (BSOD)", which is
    // false. These tests pin both halves: the genuinely unkillable set still refuses, and a
    // Windows-shipped-but-killable process reaches the confirm instead.
    //
    // A refusal returns BEFORE DialogService.Confirm, so "was Confirm reached?" is the observable
    // difference between refused and allowed — no real process is ever touched because the confirm
    // is declined.

    private static ProcessEntry Named(string name, string safety) =>
        new() { Pid = 4242, Name = name, SafetyLevel = safety };

    private static bool WasRefused(ProcessEntry entry, out string status)
    {
        var vm = new ProcessManagerViewModel(new ProcessManagerService());
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // decline: never kills
        DialogService.Instance = dialog;
        try
        {
            vm.KillProcessCommand.Execute(entry);
            status = vm.StatusMessage;
            return dialog.ReceivedCalls().Count() == 0;
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Theory]
    [InlineData("winlogon.exe")]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("services.exe")]
    [InlineData("lsass.exe")]
    [InlineData("wininit.exe")]
    public void KillProcess_BootCritical_IsStillRefused(string name)
    {
        // The true kernel set must keep refusing — this is the half of the old guard that was right.
        var refused = WasRefused(Named(name, nameof(ProcessSafety.System)), out var status);

        Assert.True(refused);
        Assert.Contains("cannot be ended", status);
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("calc.exe")]
    [InlineData("mspaint.exe")]
    [InlineData("Taskmgr.exe")]
    [InlineData("regedit.exe")]
    [InlineData("explorer.exe")]
    public void KillProcess_WindowsComponentButNotBootCritical_IsNoLongerRefused(string name)
    {
        // Each of these is tagged "System" in the database, so each was refused with a false BSOD
        // claim. Ending Notepad cannot crash Windows; the user must be allowed to decide.
        var refused = WasRefused(Named(name, nameof(ProcessSafety.System)), out var status);

        Assert.False(refused);
        Assert.DoesNotContain("cannot be ended", status);
        Assert.DoesNotContain("BSOD", status);
    }

    [Fact]
    public void KillProcess_WindowsComponent_ConfirmExplainsTheRealConsequence()
    {
        // The warning has to be honest and specific: not "this will crash your PC", but "a feature
        // may stop working until you restart" — the difference between a refusal and informed consent.
        var vm = new ProcessManagerViewModel(new ProcessManagerService());
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.KillProcessCommand.Execute(Named("explorer.exe", nameof(ProcessSafety.System)));

            dialog.Received(1).Confirm(
                Arg.Is<string>(m => m.Contains("part of Windows") && m.Contains("will not crash")),
                Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── Third tier: security / servicing processes ──────────────────────
    //
    // Dropping the provenance arm from IsKernelCritical made 49 database entries killable, which was
    // the point. But the single warning that replaced the refusal promises "will not crash Windows …
    // a feature may stop working", and that is untrue for two groups inside those 49: Defender's
    // engine (ending it is an AV-disable step) and Windows' servicing/installer processes (KillProcess
    // uses entireProcessTree, so a mid-write kill can leave a corrupt component store — damage the
    // "restart and it comes back" remedy does not repair). These pin the honest third message.

    [Theory]
    [InlineData("MsMpEng.exe")]
    [InlineData("NisSrv.exe")]
    [InlineData("SecurityHealthService.exe")]
    [InlineData("TrustedInstaller.exe")]
    [InlineData("msiexec.exe")]
    [InlineData("WmiPrvSE.exe")]
    public void KillProcess_SecurityOrServicing_WarnsAboutTheRealDamage(string name)
    {
        var vm = new ProcessManagerViewModel(new ProcessManagerService());
        using var dialog = new DialogAnswer(confirm: false);

        vm.KillProcessCommand.Execute(Named(name, nameof(ProcessSafety.System)));

        // The reassurance from the ordinary Windows-component tier must NOT appear here.
        DialogService.Instance.Received(1).Confirm(
            Arg.Is<string>(m =>
                m.Contains("security or servicing")
                && m.Contains("not undone by restarting")
                && !m.Contains("will not crash")),
            Arg.Any<string>());
    }

    [Fact]
    public void KillProcess_SecurityOrServicing_IsStillAskedNotRefused()
    {
        // The tier is a warning, not a second refusal list. These processes really can be ended, and
        // it is the user's machine — what changed is that the prompt tells the truth about the cost.
        var refused = WasRefused(Named("MsMpEng.exe", nameof(ProcessSafety.System)), out var status);

        Assert.False(refused);
        Assert.DoesNotContain("cannot be ended", status);
    }

    [Fact]
    public void HighConsequenceAndBootCritical_DoNotOverlap()
    {
        // The tiers are checked in order, so a name in both sets would be refused and its warning
        // never seen — making the entry look present while being dead. Asserted rather than assumed,
        // because both sets are hand-maintained string lists.
        var boot = PrivateNames("BootCriticalProcesses");
        var high = PrivateNames("HighConsequenceProcesses");

        Assert.NotEmpty(boot);
        Assert.NotEmpty(high);
        Assert.Empty(boot.Intersect(high, StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> PrivateNames(string fieldName)
    {
        var field = typeof(ProcessManagerViewModel).GetField(fieldName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (HashSet<string>)field.GetValue(null)!;
    }

    [Theory]
    [InlineData(nameof(ProcessSafety.Trusted))]
    [InlineData(nameof(ProcessSafety.Unknown))]
    public void KillProcess_OrdinaryProcess_GetsTheStandardConfirm(string safety)
    {
        // Non-Windows processes keep the original wording — the new branch must not leak into them.
        var vm = new ProcessManagerViewModel(new ProcessManagerService());
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.KillProcessCommand.Execute(Named("SomeApp.exe", safety));

            dialog.Received(1).Confirm(
                Arg.Is<string>(m => m.Contains("unsaved data loss") && !m.Contains("part of Windows")),
                Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void KillProcess_BootCriticalWithUnknownProvenance_IsStillRefused()
    {
        // The refusal must not depend on the database: a boot-critical name that is NOT in it
        // (logonui, lsaiso and userinit are all absent) has to be caught by name alone.
        var refused = WasRefused(Named("logonui.exe", nameof(ProcessSafety.Unknown)), out var status);

        Assert.True(refused);
        Assert.Contains("cannot be ended", status);
    }
}
