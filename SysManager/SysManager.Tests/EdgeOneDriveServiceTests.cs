// SysManager · EdgeOneDriveServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public EdgeOneDriveServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _runner = Substitute.For<IPowerShellRunner>();
        // GetStatusAsync accesses results.Count on the task query — return an empty (not null)
        // collection so the query resolves to "no tasks enabled" deterministically.
        _runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
               .Returns(new Collection<PSObject>());
        // The same redirected root stands in for BOTH hives — Edge uses SOFTWARE\Policies\…,
        // OneDrive uses Software\Classes\CLSID\…, so there is no key collision.
        _svc = new EdgeOneDriveService(_runner, hkcuRoot: _root, hklmRoot: _root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    private object? ReadEdgePolicy(string name)
    {
        using var key = _root.OpenSubKey(EdgePolicyPath);
        return key?.GetValue(name);
    }

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

        // The task toggle must go through the runner with a Disable script naming ONLY the
        // fixed allowlist entries — never an arbitrary/interpolated task name.
        await _runner.Received().RunAsync(
            Arg.Is<string>(s => s != null
                && s.Contains("Disable-ScheduledTask")
                && s.Contains("MicrosoftEdgeUpdateTaskMachineCore")
                && s.Contains("MicrosoftEdgeUpdateTaskMachineUA")),
            Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreEdgeAsync_EnablesUpdateTasks_ViaHardCodedAllowlistScript()
    {
        await _svc.RestoreEdgeAsync();

        await _runner.Received().RunAsync(
            Arg.Is<string>(s => s != null
                && s.Contains("Enable-ScheduledTask")
                && s.Contains("MicrosoftEdgeUpdateTaskMachineCore")),
            Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task GetStatusAsync_NeverThrows_WithEmptyRedirectedRoots()
    {
        // No pre-seeded keys — every read falls back to its safe default without throwing.
        var status = await _svc.GetStatusAsync();
        Assert.False(status.EdgeBackgroundDisabled);
        Assert.False(status.EdgeUpdateTasksEnabled);
    }
}
