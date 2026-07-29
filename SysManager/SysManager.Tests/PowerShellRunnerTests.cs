// SysManager · PowerShellRunnerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Reflection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PowerShellRunner"/> — pure-logic helper methods.
/// Actual process spawning is an integration test.
/// </summary>
public class PowerShellRunnerTests
{
    private static bool InvokeIsClixmlNoise(string line)
    {
        var m = typeof(PowerShellRunner).GetMethod("IsClixmlNoise", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object[] { line })!;
    }

    [Theory]
    [InlineData("#< CLIXML")]
    [InlineData("  #< CLIXML")]
    [InlineData("<Objs Version=\"1.1\">")]
    [InlineData("  <Objs Version=\"1.1\">")]
    [InlineData("<Obj RefId=\"0\">")]
    [InlineData("</Objs>")]
    public void IsClixmlNoise_DetectsNoise(string line)
        => Assert.True(InvokeIsClixmlNoise(line));

    [Theory]
    [InlineData("Normal output line")]
    [InlineData("Error: something went wrong")]
    [InlineData("")]
    [InlineData("   some indented text")]
    [InlineData("CLIXML is mentioned but not at start")]
    public void IsClixmlNoise_PassesNormalLines(string line)
        => Assert.False(InvokeIsClixmlNoise(line));

    [Fact]
    public void Constructs()
    {
        var runner = new PowerShellRunner();
        Assert.NotNull(runner);
    }

    [Fact]
    public void BuildTrustedPowerShellModulePath_UsesOnlyMachineOwnedRoots()
    {
        var path = PowerShellRunner.BuildTrustedPowerShellModulePath(
            @"C:\Program Files",
            @"C:\Windows\System32");

        var roots = path.Split(Path.PathSeparator);
        Assert.Equal(
            new[]
            {
                @"C:\Program Files\WindowsPowerShell\Modules",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
                @"C:\Program Files\PowerShell\Modules"
            },
            roots);
        Assert.DoesNotContain(roots, root =>
            root.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDistinctFromMachinePowerShellModulePath_WhenEqual_AppendsMachineOwnedGuard()
    {
        const string machinePath =
            @"C:\Program Files\WindowsPowerShell\Modules;" +
            @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules";

        var path = PowerShellRunner.EnsureDistinctFromMachinePowerShellModulePath(
            machinePath,
            machinePath,
            @"C:\Program Files");

        Assert.NotEqual(machinePath, path);
        Assert.Equal(
            new[]
            {
                @"C:\Program Files\WindowsPowerShell\Modules",
                @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
                @"C:\Program Files\SysManager\PowerShellModules"
            },
            path.Split(Path.PathSeparator));
        Assert.DoesNotContain(
            path.Split(Path.PathSeparator),
            root => root.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureDistinctFromMachinePowerShellModulePath_WhenAlreadyDistinct_PreservesPath()
    {
        const string trustedPath = @"C:\Program Files\WindowsPowerShell\Modules";

        var path = PowerShellRunner.EnsureDistinctFromMachinePowerShellModulePath(
            trustedPath,
            @"C:\Windows\System32\WindowsPowerShell\v1.0\Modules",
            @"C:\Program Files");

        Assert.Equal(trustedPath, path);
    }

    [Fact]
    public void BuildPowerShellModulePathAssignment_EscapesSingleQuotes()
    {
        var assignment = PowerShellRunner.BuildPowerShellModulePathAssignment(
            @"C:\Program Files\Owner's Modules");

        Assert.Equal(
            "$env:PSModulePath='C:\\Program Files\\Owner''s Modules';",
            assignment);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("pwsh")]
    [InlineData("pwsh.exe")]
    public void ApplyTrustedPowerShellModulePath_ElevatedPowerShellChild_ReplacesInheritedValue(
        string executable)
    {
        const string inheritedPath = @"C:\Users\Example\Documents\WindowsPowerShell\Modules";
        const string trustedPath = @"C:\Program Files\WindowsPowerShell\Modules";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.Environment["PSModulePath"] = inheritedPath;

        PowerShellRunner.ApplyTrustedPowerShellModulePath(
            startInfo,
            isElevated: true,
            trustedPath);

        Assert.Equal(trustedPath, startInfo.Environment["PSModulePath"]);
    }

    [Theory]
    [InlineData(false, "powershell.exe")]
    [InlineData(true, "cmd.exe")]
    public void ApplyTrustedPowerShellModulePath_NonPrivilegedBoundary_PreservesInheritedValue(
        bool isElevated,
        string executable)
    {
        const string inheritedPath = @"C:\Users\Example\Documents\WindowsPowerShell\Modules";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.Environment["PSModulePath"] = inheritedPath;

        PowerShellRunner.ApplyTrustedPowerShellModulePath(
            startInfo,
            isElevated,
            @"C:\Program Files\WindowsPowerShell\Modules");

        Assert.Equal(inheritedPath, startInfo.Environment["PSModulePath"]);
    }

    [Fact]
    public async Task RunProcessAsync_PreCancelled_DoesNotStartProcess()
    {
        var runner = new PowerShellRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunProcessAsync(
                "must-not-start.exe",
                string.Empty,
                cancellation.Token));
    }

    [Fact]
    public async Task RunProcessAsync_CancelledWhileStartQueued_DoesNotStartProcess()
    {
        var startQueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PowerShellRunner(async startProcess =>
        {
            startQueued.TrySetResult(true);
            await releaseStart.Task;
            startProcess();
        });
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.RunProcessAsync(
            "must-not-start.exe",
            string.Empty,
            cancellation.Token);

        await startQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseStart.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunProcessWithShellAsync_PreCancelled_DoesNotStartProcess()
    {
        var runner = new PowerShellRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunProcessWithShellAsync(
                "must-not-start.exe",
                string.Empty,
                cancellation.Token));
    }

    [Fact]
    public async Task RunProcessWithShellAsync_CancelledWhileStartQueued_DoesNotStartProcess()
    {
        var startQueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PowerShellRunner(async startProcess =>
        {
            startQueued.TrySetResult(true);
            await releaseStart.Task;
            startProcess();
        });
        using var cancellation = new CancellationTokenSource();

        var runTask = runner.RunProcessWithShellAsync(
            "must-not-start.exe",
            string.Empty,
            cancellation.Token);

        await startQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseStart.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public void LineReceived_CanSubscribeAndUnsubscribe()
    {
        var runner = new PowerShellRunner();
        var received = false;
        void Handler(Models.PowerShellLine _) => received = true;
        runner.LineReceived += Handler;
        runner.LineReceived -= Handler;
        Assert.False(received);
    }

    [Fact]
    public void ProgressChanged_CanSubscribeAndUnsubscribe()
    {
        var runner = new PowerShellRunner();
        var received = false;
        void Handler(int _) => received = true;
        runner.ProgressChanged += Handler;
        runner.ProgressChanged -= Handler;
        Assert.False(received);
    }
}
