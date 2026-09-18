// SysManager · AppBlockerWarningTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The rescue banner the App Blocker tab shows for a block it cannot lift, and what it says.
/// </summary>
/// <remarks>
/// The wording is asserted, not just the flag, because the whole value of this feature is in the words:
/// somebody stranded without an elevation prompt needs the registry location, and an elevated user needs
/// to be told to use the button instead. A banner that showed the wrong one of those two would be worse
/// than none — it would send a stuck user to a button that cannot work (#2357).
/// <para><c>IsElevated</c> is set explicitly and the list re-read afterwards, rather than being taken from
/// <c>AdminHelper.IsElevated()</c>: the real value depends on how the test host was launched, so leaving it
/// alone would make exactly one of the two branches run and which one would vary by machine.</para>
/// </remarks>
[Collection("ProcessWideStatics")]
public class AppBlockerWarningTests
{
    /// <summary>The IFEO location the unelevated branch has to name in full.</summary>
    private const string IfeoKeyPath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static AppBlockerViewModel VmWith(bool elevated, params (string Name, bool Unrecoverable)[] rows)
    {
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.GetBlockedApps().Returns([.. rows.Select(r => new BlockedApp
        {
            ExecutableName = r.Name,
            IsUnrecoverable = r.Unrecoverable,
        })]);

        var vm = new AppBlockerViewModel(blocker);
        vm.InitializationComplete.GetAwaiter().GetResult();

        // Re-read the list with the elevation state this test wants, so the recovery sentence is the one
        // being asserted rather than whichever the host happened to have.
        vm.IsElevated = elevated;
        vm.RefreshListCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void NothingUnrecoverable_ShowsNoBanner()
    {
        var vm = VmWith(elevated: false, ("notepad.exe", false), ("steam.exe", false));

        Assert.False(vm.HasUnrecoverableBlock);
        Assert.Equal("", vm.UnrecoverableWarning);
    }

    [Fact]
    public void NoBlocksAtAll_ShowsNoBanner()
    {
        var vm = VmWith(elevated: true);

        Assert.False(vm.HasUnrecoverableBlock);
        Assert.Equal("", vm.UnrecoverableWarning);
    }

    [Fact]
    public void ConsentBlockedAndNotElevated_NamesTheRegistryLocation()
    {
        // The stranded case, and the reason this feature exists. There is no button that can help here, so
        // the banner has to carry the repair itself — the alternative is what actually happened: the person
        // had to find the answer outside the app that caused it.
        var vm = VmWith(elevated: false, ("consent.exe", true));

        Assert.True(vm.HasUnrecoverableBlock);
        Assert.Contains("consent.exe", vm.UnrecoverableWarning, StringComparison.Ordinal);
        Assert.Contains("administrator rights", vm.UnrecoverableWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Debugger", vm.UnrecoverableWarning, StringComparison.Ordinal);
        Assert.Contains(IfeoKeyPath, vm.UnrecoverableWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unblock Selected", vm.UnrecoverableWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsentBlockedAndElevated_PointsAtTheButtonInstead()
    {
        // An elevated app CAN fix this, so the registry instructions would be needless alarm. The two
        // branches must not both appear, or the banner reads as "you are fine" and "you are stuck" at once.
        var vm = VmWith(elevated: true, ("consent.exe", true));

        Assert.True(vm.HasUnrecoverableBlock);
        Assert.Contains("Unblock Selected", vm.UnrecoverableWarning, StringComparison.Ordinal);
        Assert.DoesNotContain(IfeoKeyPath, vm.UnrecoverableWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsentBlocked_ExplainsWhatWasLost()
    {
        // "Cannot be undone" is not enough on its own: consent.exe means nothing on the machine can ask for
        // administrator rights, and that consequence is invisible until something tries.
        var vm = VmWith(elevated: true, ("consent.exe", true));

        Assert.Contains("permission prompt", vm.UnrecoverableWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeveralUnrecoverableBlocks_AreAllNamed()
    {
        var vm = VmWith(elevated: true, ("consent.exe", true), ("winlogon.exe", true), ("notepad.exe", false));

        Assert.True(vm.HasUnrecoverableBlock);
        Assert.Contains("consent.exe", vm.UnrecoverableWarning, StringComparison.Ordinal);
        Assert.Contains("winlogon.exe", vm.UnrecoverableWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("notepad.exe", vm.UnrecoverableWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBannerClearsWhenTheBlockIsGone()
    {
        // A warning that outlives its cause teaches people to ignore warnings. Re-reading an empty list has
        // to reset both the flag and the text, not just the flag — a stale sentence behind a hidden banner
        // would reappear intact the next time anything else set the flag.
        var blocker = Substitute.For<IAppBlockerService>();
        blocker.GetBlockedApps().Returns([new BlockedApp { ExecutableName = "consent.exe", IsUnrecoverable = true }]);

        var vm = new AppBlockerViewModel(blocker);
        await vm.InitializationComplete;
        Assert.True(vm.HasUnrecoverableBlock);

        blocker.GetBlockedApps().Returns([]);
        vm.RefreshListCommand.Execute(null);

        Assert.False(vm.HasUnrecoverableBlock);
        Assert.Equal("", vm.UnrecoverableWarning);
    }
}
