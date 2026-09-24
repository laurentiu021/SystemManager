// SysManager · UninstallerRemovalCheckTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// "Removed" is reported only once Windows stops listing the app (#2448). An uninstaller's exit code belongs to the
/// process SysManager launched, and an NSIS uninstaller hands over to a copy of itself and exits at once. The
/// uninstall list here is a disposable HKCU subkey standing in for both hives, and the runner is substituted, so no
/// uninstaller runs and the machine's own list is never read.
/// </summary>
/// <remarks>In the serialized collection because the Uninstaller tab tests answer its confirmation dialog.</remarks>
[Collection("ProcessWideStatics")]
public sealed class UninstallerRemovalCheckTests : IDisposable
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // A real, trusted executable, so the path checks in UninstallLocalAsync pass. The runner is substituted, so it is
    // never launched — the same stand-in UninstallerServiceTests uses.
    private static readonly string Uninstaller = $"\"{Path.Combine(Environment.SystemDirectory, "where.exe")}\"";

    private readonly string _rootName = @"Software\SysManagerTests\UninstallList_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public UninstallerRemovalCheckTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); }
        catch (System.Security.SecurityException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private void Register(string displayName, string? uninstall = null, string? quiet = null)
    {
        using var key = _root.CreateSubKey($@"{UninstallPath}\{Guid.NewGuid():N}", writable: true)!;
        key.SetValue("DisplayName", displayName);
        if (uninstall is not null) key.SetValue("UninstallString", uninstall);
        if (quiet is not null) key.SetValue("QuietUninstallString", quiet);
    }

    private UninstallerService Service(IPowerShellRunner? runner = null)
        => new(runner ?? Substitute.For<IPowerShellRunner>(), () => false, machineRoot: _root, userRoot: _root);

    private static InstalledApp TestApp() => new() { Name = "Test App", Id = @"ARP\Test", UninstallString = Uninstaller };

    // ── IsStillRegistered ────────────────────────────────────────────────

    [Fact]
    public void IsStillRegistered_WhileTheEntryIsListed_IsTrue()
    {
        Register("Test App", uninstall: Uninstaller);

        Assert.True(Service().IsStillRegistered(TestApp()));
    }

    [Fact]
    public void IsStillRegistered_OnceTheEntryIsGone_IsFalse()
        => Assert.False(Service().IsStillRegistered(TestApp()));

    [Fact]
    public void IsStillRegistered_AnotherVersionWithTheSameName_DoesNotCount()
    {
        Register("Test App", uninstall: @"""C:\Program Files\Test App 2\uninst.exe""");

        Assert.False(Service().IsStillRegistered(TestApp()));
    }

    [Fact]
    public void IsStillRegistered_MatchesTheQuietCommandToo()
    {
        const string quiet = @"""C:\Program Files\Test App\uninst.exe"" /S";
        Register("Test App", quiet: quiet);

        Assert.True(Service().IsStillRegistered(new InstalledApp { Name = "Test App", QuietUninstallString = quiet }));
    }

    // ── The Uninstaller tab ──────────────────────────────────────────────

    /// <summary>Selects the test app and uninstalls it through a launcher that returns 0 at once.</summary>
    private async Task<UninstallerViewModel> UninstallTestAppAsync()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var vm = new UninstallerViewModel(Service(runner)) { IsElevated = false };
        vm.AllApps.Add(TestApp());
        vm.FilterText = "Test";   // repopulates FilteredApps through the public filter path
        vm.FilterText = "";
        vm.FilteredApps.Single().IsSelected = true;

        using (new DialogAnswer(confirm: true))
            await vm.UninstallSelectedCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        return vm;
    }

    [Fact]
    public async Task Uninstall_WhenTheLauncherReturnsButWindowsStillListsTheApp_DoesNotCallItRemoved()
    {
        // The NSIS shape: the launched uninstaller exited 0 while its copy is still showing the wizard.
        Register("Test App", uninstall: Uninstaller);

        var vm = await UninstallTestAppAsync();

        var row = Assert.Single(vm.AllApps);   // still on the list
        Assert.Equal(UninstallerViewModel.StillOpenStatus, row.Status);
        Assert.StartsWith("Completed 0/1 uninstalls.", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("1 app is still installed", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_WhenWindowsNoLongerListsTheApp_ReportsItRemoved()
    {
        // The success half of the pair: an entry that is gone is a removal, as before.
        var vm = await UninstallTestAppAsync();

        Assert.Empty(vm.AllApps);
        Assert.Equal("Completed 1/1 uninstalls.", vm.StatusMessage);
    }
}
