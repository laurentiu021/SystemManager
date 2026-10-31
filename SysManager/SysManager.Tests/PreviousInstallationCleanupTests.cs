// SysManager · PreviousInstallationCleanupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// How Windows' "Previous Installations" cleanup is run for Deep Cleanup's Windows.old bucket (#2720).
/// </summary>
/// <remarks>
/// Every test runs over a throwaway key under HKEY_CURRENT_USER standing in for the Disk Cleanup handlers, never the
/// machine's own, and the runner is a substitute, so Disk Cleanup itself never runs.
/// </remarks>
public sealed class PreviousInstallationCleanupTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\PreviousInstallation_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly RegistryKey _handlers;

    public PreviousInstallationCleanupTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _handlers = _root.CreateSubKey(PreviousInstallationCleanup.VolumeCachesPath, writable: true)!;
        _handlers.CreateSubKey(PreviousInstallationCleanup.HandlerName)!.Dispose();
        _handlers.CreateSubKey("Temporary Files")!.Dispose();
    }

    public void Dispose()
    {
        _handlers.Dispose();
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private PreviousInstallationCleanup NewCleanup(IPowerShellRunner runner) => new(runner, _root);

    private object? ValueOf(string handler, string name)
    {
        using var key = _handlers.OpenSubKey(handler);
        return key?.GetValue(name);
    }

    private void SetValue(string handler, string name, int value)
    {
        using var key = _handlers.OpenSubKey(handler, writable: true)!;
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>
    /// A runner whose Disk Cleanup run records which handlers were selected for it while it ran, and ends with
    /// <paramref name="exitCode"/>.
    /// </summary>
    private IPowerShellRunner RunnerRecording(List<(string Handler, object? Value)> selected, int exitCode = 0)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  var flag = "StateFlags" + ((string)call[1])["/sagerun:".Length..];
                  foreach (var handler in _handlers.GetSubKeyNames())
                      selected.Add((handler, ValueOf(handler, flag)));
                  return exitCode;
              });
        return runner;
    }

    [Fact]
    public async Task Remove_RunsDiskCleanupForThePreviousInstallationAlone_AndTakesItsSelectionOffAfter()
    {
        List<(string Handler, object? Value)> selected = [];
        var runner = RunnerRecording(selected);

        var exitCode = await NewCleanup(runner).RemoveAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        await runner.Received(1).RunProcessAsync("cleanmgr.exe",
            $"/sagerun:{PreviousInstallationCleanup.FirstRun}", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        // While it ran: selected for this handler, and for no other.
        Assert.Equal(2, Assert.Single(selected, s => s.Handler == PreviousInstallationCleanup.HandlerName).Value);
        Assert.Null(Assert.Single(selected, s => s.Handler == "Temporary Files").Value);
        // After it: taken off again.
        Assert.Null(ValueOf(PreviousInstallationCleanup.HandlerName, PreviousInstallationCleanup.StateFlag(PreviousInstallationCleanup.FirstRun)));
    }

    [Fact]
    public async Task Remove_LeavesARunNumberTheUsersOwnSetUpUses_AndItsValueAsItWas()
    {
        // The user's own "cleanmgr /sageset" for that number, on another handler: running it would clean that one too.
        var usersOwn = PreviousInstallationCleanup.StateFlag(PreviousInstallationCleanup.FirstRun);
        SetValue("Temporary Files", usersOwn, 2);
        List<(string Handler, object? Value)> selected = [];
        var runner = RunnerRecording(selected);

        await NewCleanup(runner).RemoveAsync(CancellationToken.None);

        await runner.Received(1).RunProcessAsync(Arg.Any<string>(), $"/sagerun:{PreviousInstallationCleanup.FirstRun + 1}",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.Null(Assert.Single(selected, s => s.Handler == "Temporary Files").Value);
        Assert.Equal(2, ValueOf("Temporary Files", usersOwn));
        Assert.Null(ValueOf(PreviousInstallationCleanup.HandlerName, usersOwn));
    }

    [Fact]
    public async Task Remove_TakesTheSelectionOff_WhenDiskCleanupFails()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .ThrowsAsync(new System.ComponentModel.Win32Exception(2));

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => NewCleanup(runner).RemoveAsync(CancellationToken.None));

        Assert.Empty(ValueNames(PreviousInstallationCleanup.HandlerName));
    }

    [Fact]
    public async Task Remove_OnAWindowsWithoutTheHandler_RunsNothing_AndSetsNothing()
    {
        _handlers.DeleteSubKeyTree(PreviousInstallationCleanup.HandlerName);
        var runner = Substitute.For<IPowerShellRunner>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewCleanup(runner).RemoveAsync(CancellationToken.None));

        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
        Assert.Empty(ValueNames("Temporary Files"));
    }

    [Fact]
    public async Task Remove_WithEveryRunNumberTaken_RunsNothing_AndSetsNothing()
    {
        for (var run = PreviousInstallationCleanup.FirstRun; run <= PreviousInstallationCleanup.LastRun; run++)
            SetValue("Temporary Files", PreviousInstallationCleanup.StateFlag(run), 2);
        var runner = Substitute.For<IPowerShellRunner>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewCleanup(runner).RemoveAsync(CancellationToken.None));

        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
        Assert.Empty(ValueNames(PreviousInstallationCleanup.HandlerName));
    }

    [Fact]
    public void StateFlag_IsTheValueNameDiskCleanupReads() =>
        Assert.Equal("StateFlags6400", PreviousInstallationCleanup.StateFlag(6400));

    private string[] ValueNames(string handler)
    {
        using var key = _handlers.OpenSubKey(handler)!;
        return key.GetValueNames();
    }
}
