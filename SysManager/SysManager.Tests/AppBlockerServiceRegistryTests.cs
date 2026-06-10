// SysManager · AppBlockerServiceRegistryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
    private const string BlockerDebugger = @"C:\Windows\System32\SysManager_Blocked.exe";

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

        var blocked = _svc.GetBlockedApps().Select(b => b.ExecutableName).ToList();

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
}
