// SysManager · EdgeOneDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EdgeOneDriveService"/>. The registry-mutating logic (Edge machine
/// policy, OneDrive nav-pane pin) runs against a disposable HKCU subkey standing in for both
/// hives, so writes are verified against a real registry without administrator rights or
/// touching real machine state (mirrors <see cref="AppBlockerServiceRegistryTests"/>). All
/// PowerShell is routed through a substituted <see cref="IPowerShellRunner"/>, so no process
/// or scheduled-task ever runs.
/// </summary>
public sealed class EdgeOneDriveServiceTests : IDisposable
{
    private const string EdgePolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string OneDriveClsidPath = @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";

    private readonly string _rootName = @"Software\SysManagerTests\EdgeOneDrive_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly IPowerShellRunner _runner;
    private readonly EdgeOneDriveService _svc;
    private readonly string _setupDir = Path.Combine(Path.GetTempPath(), "SysManagerTests", "EdgeOneDrive_" + Guid.NewGuid().ToString("N"));

    public EdgeOneDriveServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _runner = Substitute.For<IPowerShellRunner>();
        // Every script answers as the task script does when it found both of Edge's update tasks and none refused.
        // The status query finds no AnyEnabled in that, so it resolves to "no tasks enabled" deterministically.
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
               .Returns(TaskReport(found: 2, refused: false));
        // The same redirected root stands in for BOTH hives — Edge uses SOFTWARE\Policies\…,
        // OneDrive uses Software\Classes\CLSID\…, so there is no key collision.
        _svc = new EdgeOneDriveService(_runner, hkcuRoot: _root, hklmRoot: _root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
        if (Directory.Exists(_setupDir)) Directory.Delete(_setupDir, recursive: true);
    }

    private object? ReadEdgePolicy(string name)
    {
        using var key = _root.OpenSubKey(EdgePolicyPath);
        return key?.GetValue(name);
    }

    private object? ReadOneDrivePin()
    {
        using var key = _root.OpenSubKey(OneDriveClsidPath);
        return key?.GetValue("System.IsPinnedToNameSpaceTree");
    }

    private void SeedEdgePolicy(string name, int value)
    {
        using var key = _root.CreateSubKey(EdgePolicyPath, writable: true)!;
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>What the task script reports: how many of Edge's update tasks it found, and if one refused.</summary>
    private static Collection<PSObject> TaskReport(int found, bool refused)
    {
        var report = new PSObject();
        report.Properties.Add(new PSNoteProperty("Found", found));
        report.Properties.Add(new PSNoteProperty("Refused", refused));
        return new Collection<PSObject> { report };
    }

    private void TaskScriptsReport(Collection<PSObject> report) =>
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
               .Returns(report);

    /// <summary>
    /// A service whose OneDrive is the stand-ins in the test's folder: a setup answering with
    /// <paramref name="exitCode"/> when <paramref name="setupPresent"/>, and OneDrive.exe when
    /// <paramref name="installed"/>. They only have to exist: the runner is substituted, so nothing is launched.
    /// </summary>
    private EdgeOneDriveService WithOneDrive(bool installed, bool setupPresent = true, int exitCode = 0)
    {
        Directory.CreateDirectory(_setupDir);
        var setup = Path.Combine(_setupDir, "OneDriveSetup.exe");
        var client = Path.Combine(_setupDir, "OneDrive.exe");
        if (setupPresent) File.WriteAllBytes(setup, []);
        if (installed) File.WriteAllBytes(client, []);
        _runner.RunProcessAsync(setup, Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
               .Returns(exitCode);
        return new EdgeOneDriveService(_runner, hkcuRoot: _root, hklmRoot: _root, oneDriveSetupPath: setup,
            oneDriveClientPath: client, isOneDriveRunning: () => false);
    }

    /// <summary>A service whose installed OneDrive's setup answers with <paramref name="exitCode"/>.</summary>
    private EdgeOneDriveService WithOneDriveSetupExiting(int exitCode) =>
        WithOneDrive(installed: true, exitCode: exitCode);

    // ── Task-name injection guard ───────────────────────────────────────────

    [Theory]
    [InlineData("MicrosoftEdgeUpdateTaskMachineCore")]
    [InlineData("MicrosoftEdgeUpdateTaskMachineUA")]
    [InlineData("SomeAlphaNumeric123")]
    public void IsSafeTaskName_TrueForAlphanumeric(string name)
        => Assert.True(EdgeOneDriveService.IsSafeTaskName(name));

    [Theory]
    [InlineData("Task'; Remove-Item C:\\ #")]   // quote break-out + command
    [InlineData("Task Name With Spaces")]         // whitespace
    [InlineData("Task$(whoami)")]                 // subexpression
    [InlineData("Task`n")]                        // backtick
    [InlineData("Task\\Path")]                    // backslash (path traversal)
    [InlineData("")]                               // empty
    public void IsSafeTaskName_FalseForUnsafe(string name)
        => Assert.False(EdgeOneDriveService.IsSafeTaskName(name));

    // Regression guard: the fixed allowlist is interpolated into the enable/disable script, so
    // every entry MUST be embedding-safe. This fails the build if a future edit adds an unsafe name.
    [Fact]
    public void EdgeUpdateTaskNames_AreAllInjectionSafe()
        => Assert.All(EdgeOneDriveService.EdgeUpdateTaskNames, n => Assert.True(EdgeOneDriveService.IsSafeTaskName(n)));

    // ── Edge policy round-trip (needs-admin path proven writable via redirect) ─

    [Fact]
    public async Task DisableEdgeAsync_WritesBackgroundAndStartupBoostToZero_AndSucceeds()
    {
        var outcome = await _svc.DisableEdgeAsync();

        Assert.Equal(EdgeOneDriveOutcome.Success, outcome);
        Assert.Equal(0, ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Equal(0, ReadEdgePolicy("StartupBoostEnabled"));
    }

    [Fact]
    public async Task RestoreEdgeAsync_ClearsThePolicyValues_AndSucceeds()
    {
        await _svc.DisableEdgeAsync();               // seed the disabled state
        Assert.NotNull(ReadEdgePolicy("BackgroundModeEnabled"));

        var outcome = await _svc.RestoreEdgeAsync();

        Assert.Equal(EdgeOneDriveOutcome.Success, outcome);
        Assert.Null(ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Null(ReadEdgePolicy("StartupBoostEnabled"));
    }

    [Fact]
    public async Task DisableEdgeAsync_DisablesUpdateTasks_ViaHardCodedAllowlistScript()
    {
        await _svc.DisableEdgeAsync();

        // The task toggle must go through the runner with the Disable script, which finds tasks ONLY by the
        // fixed allowlist entries — never an arbitrary/interpolated task name. What it does with them is pinned
        // in real Windows PowerShell by EdgeOneDriveScriptTests.
        var script = EdgeOneDriveService.BuildSetEdgeTasksScript(enabled: false);
        Assert.Contains("-TaskName 'MicrosoftEdgeUpdateTaskMachineCore*','MicrosoftEdgeUpdateTaskMachineUA*'", script,
            StringComparison.Ordinal);
        await _runner.Received().RunAsync(
            script, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreEdgeAsync_EnablesUpdateTasks_ViaHardCodedAllowlistScript()
    {
        await _svc.RestoreEdgeAsync();

        var script = EdgeOneDriveService.BuildSetEdgeTasksScript(enabled: true);
        Assert.Contains("-TaskName 'MicrosoftEdgeUpdateTaskMachineCore*','MicrosoftEdgeUpdateTaskMachineUA*'", script,
            StringComparison.Ordinal);
        await _runner.Received().RunAsync(
            script, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisableEdgeAsync_TaskMutationFails_ReturnsFailed_AndPutsThePolicyBack()
    {
        // The policy stayed written when the tasks failed, so a change reported as failed still switched off Edge's
        // background mode and startup boost (#2810).
        _runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Collection<PSObject>>>(
                _ => throw new RuntimeException("Windows PowerShell is unavailable."));

        var outcome = await _svc.DisableEdgeAsync();

        Assert.Equal(EdgeOneDriveOutcome.Failed, outcome);
        Assert.Null(ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Null(ReadEdgePolicy("StartupBoostEnabled"));
    }

    [Theory]
    [InlineData(0, false)]   // no update task found: the exact names found none, and this reported success (#2810)
    [InlineData(2, true)]    // one refused, and the script put back the ones it had changed
    public async Task DisableEdgeAsync_WhenTheTasksAreNotChanged_ReturnsFailed_AndPutsThePolicyBack(
        int found, bool refused)
    {
        TaskScriptsReport(TaskReport(found, refused));

        var outcome = await _svc.DisableEdgeAsync();

        Assert.Equal(EdgeOneDriveOutcome.Failed, outcome);
        Assert.Null(ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Null(ReadEdgePolicy("StartupBoostEnabled"));
    }

    [Fact]
    public async Task DisableEdgeAsync_WhenTheTaskScriptReportsNothing_ReturnsFailed()
    {
        TaskScriptsReport(new Collection<PSObject>());

        Assert.Equal(EdgeOneDriveOutcome.Failed, await _svc.DisableEdgeAsync());
        Assert.Null(ReadEdgePolicy("BackgroundModeEnabled"));
    }

    [Fact]
    public async Task DisableEdgeAsync_WhenItFails_PutsBackWhatWasSetBefore_RatherThanDeletingIt()
    {
        // Set before the change, as whoever manages the PC may have: a failed change leaves it, not deletes it.
        SeedEdgePolicy("BackgroundModeEnabled", 1);
        TaskScriptsReport(TaskReport(found: 0, refused: false));

        Assert.Equal(EdgeOneDriveOutcome.Failed, await _svc.DisableEdgeAsync());

        Assert.Equal(1, ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Null(ReadEdgePolicy("StartupBoostEnabled"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task RestoreEdgeAsync_WhenTheTasksAreNotChanged_ReturnsFailed(int found, bool refused)
    {
        TaskScriptsReport(TaskReport(found, refused));

        Assert.Equal(EdgeOneDriveOutcome.Failed, await _svc.RestoreEdgeAsync());
    }

    [Fact]
    public async Task RestoreEdgeAsync_TaskMutationFails_ReturnsFailedInsteadOfPartialSuccess()
    {
        using (var key = _root.CreateSubKey(EdgePolicyPath, writable: true)!)
        {
            key.SetValue("BackgroundModeEnabled", 0, RegistryValueKind.DWord);
            key.SetValue("StartupBoostEnabled", 0, RegistryValueKind.DWord);
        }
        _runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Collection<PSObject>>>(
                _ => throw new RuntimeException("Windows PowerShell is unavailable."));

        var outcome = await _svc.RestoreEdgeAsync();

        Assert.Equal(EdgeOneDriveOutcome.Failed, outcome);
        Assert.Null(ReadEdgePolicy("BackgroundModeEnabled"));
        Assert.Null(ReadEdgePolicy("StartupBoostEnabled"));
    }

    // ── GetStatusAsync reads the redirected registry back ───────────────────

    [Fact]
    public async Task GetStatusAsync_ReportsEdgeBackgroundDisabled_AfterDisable()
    {
        Assert.False((await _svc.GetStatusAsync()).EdgeBackgroundDisabled);   // clean baseline

        await _svc.DisableEdgeAsync();
        Assert.True((await _svc.GetStatusAsync()).EdgeBackgroundDisabled);

        await _svc.RestoreEdgeAsync();
        Assert.False((await _svc.GetStatusAsync()).EdgeBackgroundDisabled);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task GetStatusAsync_ReadsOneDrivePinFromRegistry(int pinValue, bool expectedPinned)
    {
        using (var key = _root.CreateSubKey(OneDriveClsidPath, writable: true)!)
            key.SetValue("System.IsPinnedToNameSpaceTree", pinValue, RegistryValueKind.DWord);

        var status = await _svc.GetStatusAsync();

        Assert.Equal(expectedPinned, status.OneDrivePinned);
    }

    // ── OneDrive remove/restore report the setup's own result (#2443) ─────────

    [Fact]
    public async Task RestoreOneDrive_WhenTheSetupFails_ReportsFailed_AndPinsNothing()
    {
        // The exit code was discarded: a failed setup reported "OneDrive restored." and pinned a File Explorer
        // entry for a client that was not installed.
        var outcome = await WithOneDriveSetupExiting(1).RestoreOneDriveAsync();

        Assert.Equal(EdgeOneDriveOutcome.Failed, outcome);
        Assert.Null(ReadOneDrivePin());
    }

    [Fact]
    public async Task RestoreOneDrive_WhenTheSetupSucceeds_ReportsSuccess_AndPinsTheEntry()
    {
        var outcome = await WithOneDriveSetupExiting(0).RestoreOneDriveAsync();

        Assert.Equal(EdgeOneDriveOutcome.Success, outcome);
        Assert.Equal(1, ReadOneDrivePin());
    }

    [Fact]
    public async Task RemoveOneDrive_WhenTheUninstallFails_ReportsFailed_AndLeavesTheEntry()
    {
        // The path restore now matches, pinned so the two cannot drift apart again. The entry used to go whatever the
        // exit code, so a failed uninstall left an installed OneDrive missing from File Explorer's sidebar (#2810).
        using (var key = _root.CreateSubKey(OneDriveClsidPath, writable: true)!)
            key.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);

        var outcome = await WithOneDriveSetupExiting(1).RemoveOneDriveAsync();

        Assert.Equal(EdgeOneDriveOutcome.Failed, outcome);
        Assert.Equal(1, ReadOneDrivePin());
    }

    [Fact]
    public async Task RemoveOneDrive_WhenTheUninstallSucceeds_ReportsSuccess_AndUnpinsTheEntry()
    {
        var outcome = await WithOneDriveSetupExiting(0).RemoveOneDriveAsync();

        Assert.Equal(EdgeOneDriveOutcome.Success, outcome);
        Assert.Equal(0, ReadOneDrivePin());
    }

    [Fact]
    public async Task GetStatusAsync_WithTheSetupButNoOneDrive_SaysNotInstalled()
    {
        // Windows keeps the setup in System32 after an uninstall, and deciding by it, the card said "OneDrive is
        // installed." right after a removal that worked (#2810).
        var status = await WithOneDrive(installed: false).GetStatusAsync();

        Assert.False(status.OneDriveInstalled);
    }

    [Fact]
    public async Task GetStatusAsync_WithOneDrive_SaysInstalled()
        => Assert.True((await WithOneDrive(installed: true).GetStatusAsync()).OneDriveInstalled);

    [Fact]
    public async Task RemoveOneDrive_WithNoOneDrive_IsNotApplicable_AndLaunchesNothing()
    {
        Assert.Equal(EdgeOneDriveOutcome.NotApplicable, await WithOneDrive(installed: false).RemoveOneDriveAsync());
        await _runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RemoveOneDrive_WithOneDriveButNoSetup_ReportsFailed_AndLaunchesNothing()
    {
        // Not "not installed": the card says it is, and only the program to remove it is missing.
        var svc = WithOneDrive(installed: true, setupPresent: false);

        Assert.Equal(EdgeOneDriveOutcome.Failed, await svc.RemoveOneDriveAsync());
        await _runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RestoreOneDrive_WithNoSetupOnDisk_IsNotApplicable_AndLaunchesNothing()
    {
        var svc = new EdgeOneDriveService(_runner, hkcuRoot: _root, hklmRoot: _root,
            oneDriveSetupPath: Path.Combine(_setupDir, "OneDriveSetup.exe"));   // never created

        Assert.Equal(EdgeOneDriveOutcome.NotApplicable, await svc.RestoreOneDriveAsync());
        await _runner.DidNotReceive().RunProcessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task GetStatusAsync_NeverThrows_WithEmptyRedirectedRoots()
    {
        // No pre-seeded keys — every read falls back to its safe default without throwing.
        var status = await _svc.GetStatusAsync();
        Assert.False(status.EdgeBackgroundDisabled);
        Assert.False(status.EdgeUpdateTasksEnabled);
    }
}
