// SysManager · AppBlockerServiceRegistryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Round-trip tests for <see cref="AppBlockerService"/>'s IFEO registry writes
/// (audit finding tests #12). The service takes an injectable registry root; here
/// we point it at a disposable subkey under HKCU so block/unblock can be verified
/// against a real registry hive without administrator rights and without touching
/// the machine's actual HKLM IFEO configuration.
/// </summary>
public sealed class AppBlockerServiceRegistryTests : IDisposable
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    // Mirror the service's own derivation (Environment.SystemDirectory) rather than a
    // hardcoded C:\Windows\System32 — Windows can be installed elsewhere, and asserting a
    // literal would make this test pass only on a default install.
    private static readonly string BlockerDebugger =
        System.IO.Path.Combine(Environment.SystemDirectory, "SysManager_Blocked.exe");

    private readonly string _rootName = @"Software\SysManagerTests\AppBlocker_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly AppBlockerService _svc;

    public AppBlockerServiceRegistryTests()
    {
        // A writable, user-scoped root standing in for Registry.LocalMachine.
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        // The service expects to find the IFEO path under the root; pre-create it.
        _root.CreateSubKey(IfeoPath, writable: true)!.Dispose();
        _svc = new AppBlockerService(_root);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    private string? ReadDebugger(string exeName)
    {
        using var appKey = _root.OpenSubKey($@"{IfeoPath}\{exeName}");
        return appKey?.GetValue("Debugger") as string;
    }

    [Fact]
    public void BlockApp_WritesBlockerDebuggerValue()
    {
        Assert.True(_svc.BlockApp("notepad.exe"));
        Assert.Equal(BlockerDebugger, ReadDebugger("notepad.exe"));
        Assert.True(_svc.IsBlocked("notepad.exe"));
    }

    [Fact]
    public void BlockApp_AppendsExeExtension()
    {
        Assert.True(_svc.BlockApp("calc"));
        Assert.Equal(BlockerDebugger, ReadDebugger("calc.exe"));
    }

    [Fact]
    public void UnblockApp_RemovesTheDebuggerValueAndKey()
    {
        _svc.BlockApp("game.exe");
        Assert.True(_svc.IsBlocked("game.exe"));

        Assert.True(_svc.UnblockApp("game.exe"));
        Assert.False(_svc.IsBlocked("game.exe"));
        Assert.Null(ReadDebugger("game.exe"));
    }

    [Fact]
    public void GetBlockedApps_ListsOnlySysManagerBlockedEntries()
    {
        _svc.BlockApp("one.exe");
        _svc.BlockApp("two.exe");

        var listed = _svc.GetBlockedApps();
        Assert.NotNull(listed);
        var blocked = listed.Select(b => b.ExecutableName).ToList();

        Assert.Contains("one.exe", blocked);
        Assert.Contains("two.exe", blocked);
    }

    [Fact]
    public void BlockApp_DoesNotClobberExternalDebuggerValue()
    {
        // A legitimately-debugged app already has a foreign Debugger value.
        using (var external = _root.CreateSubKey($@"{IfeoPath}\debugged.exe", writable: true)!)
            external.SetValue("Debugger", @"C:\Tools\mydebugger.exe", RegistryValueKind.String);

        Assert.False(_svc.BlockApp("debugged.exe"));
        // The external value must be left intact.
        Assert.Equal(@"C:\Tools\mydebugger.exe", ReadDebugger("debugged.exe"));
    }

    [Fact]
    public void UnblockApp_LeavesForeignDebuggerKeyUntouched()
    {
        using (var external = _root.CreateSubKey($@"{IfeoPath}\external.exe", writable: true)!)
            external.SetValue("Debugger", @"C:\Tools\dbg.exe", RegistryValueKind.String);

        // Unblock should be a no-op on a key we did not set.
        Assert.True(_svc.UnblockApp("external.exe"));
        Assert.Equal(@"C:\Tools\dbg.exe", ReadDebugger("external.exe"));
    }

    // ── Never let App Blocker block SysManager itself ──────────────────────────
    //
    // An IFEO block on our own exe is unrecoverable in-app (UnblockApp needs the app running,
    // but the next launch is redirected to the non-existent blocker path and fails) — the same
    // hazard the BootCriticalExecutables list guards. The own-exe name is injectable so this
    // test can drive the guard (the xUnit host's process name is testhost/dotnet, not SysManager).

    [Theory]
    [InlineData("SysManager.exe", "SysManager.exe")]           // dev/assembly name
    [InlineData("SysManager-v1.52.99.exe", "SysManager-v1.52.99.exe")] // released name
    [InlineData("sysmanager.exe", "SysManager.exe")]           // case-insensitive
    [InlineData("SysManager", "SysManager.exe")]               // bare name (.exe appended before the check)
    public void BlockApp_OwnExecutable_IsRefused_AndNotWritten(string typedName, string ownExeName)
    {
        var svc = new AppBlockerService(_root, ownExecutableName: ownExeName);

        Assert.False(svc.BlockApp(typedName));
        // Prove nothing was written to the (redirected) IFEO hive — the guard bailed before the write.
        Assert.Null(ReadDebugger(ownExeName));
        Assert.False(svc.IsBlocked(ownExeName));
    }

    [Fact]
    public void BlockApp_OwnExeGuard_DoesNotBlockOtherApps()
    {
        // The self-guard must not over-reach: a different app is still blockable as normal.
        var svc = new AppBlockerService(_root, ownExecutableName: "SysManager.exe");
        Assert.True(svc.BlockApp("notepad.exe"));
        Assert.Equal(BlockerDebugger, ReadDebugger("notepad.exe"));
    }

    // ---------- TryBlockApp: telling the refusals apart ----------
    //
    // BlockApp returned a bare bool for six different causes, four of them deliberate safety
    // refusals, and the UI reported every one as "check admin privileges". These pin each cause
    // to its own result so a refusal can never again be mistaken for a permissions problem.

    [Fact]
    public void TryBlockApp_Success_ReportsSuccess()
    {
        Assert.Equal(AppBlockerService.BlockResult.Success, _svc.TryBlockApp("notepad.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBlockApp_EmptyName_ReportsEmptyName(string name)
    {
        Assert.Equal(AppBlockerService.BlockResult.EmptyName, _svc.TryBlockApp(name));
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe")]      // path separators
    [InlineData(@"..\..\evil.exe")]              // traversal
    [InlineData("note*pad.exe")]                 // wildcard
    public void TryBlockApp_InvalidName_ReportsInvalidName(string name)
    {
        Assert.Equal(AppBlockerService.BlockResult.InvalidName, _svc.TryBlockApp(name));
    }

    [Theory]
    [InlineData("winlogon.exe")]
    [InlineData("lsass.exe")]
    [InlineData("explorer.exe")]
    [InlineData("csrss")]        // bare name; .exe is appended before the check
    // Elevation-critical rather than boot-critical: Windows starts fine without the consent UI, but
    // blocking it means no administrator prompt can ever appear again — including the one UnblockApp
    // needs. Same circular hazard, different moment.
    [InlineData("consent.exe")]
    [InlineData("CONSENT")]      // case-insensitive + bare name
    public void TryBlockApp_BootCritical_ReportsBootCritical(string name)
    {
        // The distinction that mattered most: this previously told the user to check their admin
        // rights, sending them to relaunch elevated where the same guard refuses again.
        Assert.Equal(AppBlockerService.BlockResult.BootCritical, _svc.TryBlockApp(name));
    }

    [Fact]
    public void TryBlockApp_OwnExecutable_ReportsOwnExecutable()
    {
        var svc = new AppBlockerService(_root, ownExecutableName: "SysManager.exe");

        Assert.Equal(AppBlockerService.BlockResult.OwnExecutable, svc.TryBlockApp("SysManager.exe"));
    }

    [Fact]
    public void TryBlockApp_ExternalDebuggerPresent_ReportsExternalDebuggerPresent()
    {
        using (var appKey = _root.CreateSubKey($@"{IfeoPath}\devtool.exe", writable: true)!)
            appKey.SetValue("Debugger", @"C:\SomeTool\attach.exe", RegistryValueKind.String);

        Assert.Equal(
            AppBlockerService.BlockResult.ExternalDebuggerPresent,
            _svc.TryBlockApp("devtool.exe"));

        // And the external value survives untouched.
        Assert.Equal(@"C:\SomeTool\attach.exe", ReadDebugger("devtool.exe"));
    }

    [Fact]
    public void BlockApp_StillAgreesWithTryBlockApp()
    {
        // The bool overload is kept for callers that only need success/failure, so the two must
        // not drift apart.
        Assert.True(_svc.BlockApp("notepad.exe"));
        Assert.False(_svc.BlockApp("winlogon.exe"));
        Assert.Equal(
            _svc.TryBlockApp("winlogon.exe") == AppBlockerService.BlockResult.Success,
            _svc.BlockApp("winlogon.exe"));
    }

    [Fact]
    public void TryBlockApp_RefusalsWriteNothing()
    {
        // Every refusal must bail before the registry write, not write and then report failure.
        foreach (var name in new[] { "winlogon.exe", @"C:\evil.exe", "" })
            _svc.TryBlockApp(name);

        Assert.Null(ReadDebugger("winlogon.exe"));
        Assert.False(_svc.IsBlocked("winlogon.exe"));
    }

    // ── A block list that could not be read (#2503) ──
    //
    // An unreadable Image File Execution Options key made GetBlockedApps return an empty list and IsBlocked
    // return false, so the tab said nothing was blocked, and a blocked consent.exe raised no warning. The key is
    // made unreadable for real, with a deny entry for this user that is removed again afterwards.

    [Fact]
    public void AnUnreadableBlockList_IsNull_NotEmpty()
    {
        Assert.True(_svc.BlockApp("notepad.exe"));   // the premise: there is a block to hide

        using var ifeo = _root.OpenSubKey(IfeoPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(identity.User!,
            RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        var security = ifeo.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(deny);
        ifeo.SetAccessControl(security);
        try
        {
            Assert.Null(_svc.GetBlockedApps());
            Assert.Null(_svc.IsBlocked("notepad.exe"));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            ifeo.SetAccessControl(security);
        }

        Assert.True(_svc.IsBlocked("notepad.exe"));   // readable again, so the cleanup can delete the key
    }

    [Fact]
    public void AnAbsentBlockList_IsEmpty_NotAFailure()
    {
        // Nothing has ever been blocked on a machine without the key, which is not a failed read.
        _root.DeleteSubKeyTree(IfeoPath);

        var blocked = _svc.GetBlockedApps();

        Assert.NotNull(blocked);
        Assert.Empty(blocked);
        Assert.False(_svc.IsBlocked("notepad.exe"));
    }
}
