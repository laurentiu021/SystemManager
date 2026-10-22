// SysManager · PendingRebootTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PendingReboot"/>, the one pending-restart check the Dashboard's alert and the Windows Update
/// tab both make (#2802), over a disposable HKCU subkey laid out as the machine's registry is, so nothing reads or
/// writes the real one.
/// </summary>
/// <remarks>
/// The Dashboard read only Windows Update's key, so after a Windows feature change, or an installer that scheduled
/// files to replace at the next start, it showed a green "No pending reboots" while the Windows Update tab said a
/// reboot was required.
/// </remarks>
public sealed class PendingRebootTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\PendingReboot_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public PendingRebootTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private void Create(string path) => _root.CreateSubKey(path, writable: true)!.Dispose();

    private void FileRenames(params string[] pairs)
    {
        using var session = _root.CreateSubKey(PendingReboot.SessionManagerKey, writable: true)!;
        session.SetValue(PendingReboot.FileRenamesValue, pairs, RegistryValueKind.MultiString);
    }

    [Fact]
    public void Nothing_IsNoReason()
    {
        Assert.Empty(PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void AWindowsFeatureChange_IsAReason()
    {
        // Component servicing's flag, which Windows Features leaves set after a change made without a restart.
        Create(PendingReboot.ComponentServicingKey);

        Assert.Equal(["Windows features or components"], PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void WindowsUpdate_IsAReason()
    {
        Create(PendingReboot.WindowsUpdateKey);

        Assert.Equal(["Windows Update"], PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void FilesWaitingToBeReplaced_AreAReason()
    {
        // A source and a target, and a file to delete, whose target is empty.
        FileRenames(@"\??\C:\Temp\new.dll", @"!\??\C:\Program Files\App\app.dll", @"\??\C:\Temp\old.tmp", "");

        Assert.Equal(["files waiting to be replaced"], PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void AnEmptyRenameList_IsNoReason()
    {
        FileRenames();
        Assert.Empty(PendingReboot.Reasons(_root)!);

        FileRenames("");
        Assert.Empty(PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void EveryReason_IsListed_InOrder()
    {
        Create(PendingReboot.ComponentServicingKey);
        Create(PendingReboot.WindowsUpdateKey);
        FileRenames(@"\??\C:\Temp\new.dll", @"!\??\C:\Program Files\App\app.dll");

        Assert.Equal(
            ["Windows features or components", "Windows Update", "files waiting to be replaced"],
            PendingReboot.Reasons(_root)!);
    }

    [Fact]
    public void Describe_SaysWhatTheWindowsUpdateTabPrints()
    {
        Assert.Equal("No pending reboot detected.", PendingReboot.Describe([]));
        Assert.Equal("REBOOT REQUIRED - reasons: Windows Update, files waiting to be replaced",
            PendingReboot.Describe(["Windows Update", "files waiting to be replaced"]));
        Assert.Equal("Could not read whether a reboot is pending.", PendingReboot.Describe(null));
    }
}
