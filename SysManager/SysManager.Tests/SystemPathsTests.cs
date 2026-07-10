// SysManager · SystemPathsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Pins the binary-planting / LPE hardening: bare Windows tool names must resolve to their
/// full trusted System32 path so an attacker-planted executable in the app's own directory
/// cannot be run (especially elevated). Non-system / explicit paths must pass through unchanged.
/// </summary>
public class SystemPathsTests
{
    [Fact]
    public void ResolveSystemTool_KnownSystem32Tool_ReturnsFullSystem32Path()
    {
        // cmd.exe is always present in System32 on Windows (CI runs on windows-latest).
        var resolved = SystemPaths.ResolveSystemTool("cmd.exe");
        Assert.True(Path.IsPathRooted(resolved), "resolved path must be rooted");
        Assert.True(File.Exists(resolved), "resolved path must exist");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveSystemTool_PowerShell_ResolvesToRootedExistingPath()
    {
        // powershell.exe lives in the System32\WindowsPowerShell\v1.0 subfolder, not System32 itself.
        var resolved = SystemPaths.ResolveSystemTool("powershell.exe");
        Assert.True(Path.IsPathRooted(resolved), "powershell.exe must resolve to a rooted path");
        Assert.True(File.Exists(resolved), "resolved powershell.exe must exist");
        Assert.EndsWith("powershell.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSystemTool_BareNameWithoutExeSuffix_ResolvesToRootedExistingPath()
    {
        // Regression: a caller passing the extension-less name "powershell" (as
        // WindowsFeaturesService did) must still be pinned to the trusted System32 path.
        // Before the ".exe"-fallback fix, File.Exists("...\\powershell") was false so the
        // method fell through and returned the UNROOTED bare name — re-opening the exact
        // binary-planting/LPE vector the helper exists to close.
        var resolved = SystemPaths.ResolveSystemTool("powershell");
        Assert.True(Path.IsPathRooted(resolved), "bare 'powershell' must resolve to a rooted path");
        Assert.True(File.Exists(resolved), "resolved path must exist on disk");
        Assert.EndsWith("powershell.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSystemTool_BareSystem32NameWithoutExe_ResolvesToRootedExistingPath()
    {
        // Same fallback for a plain System32 tool: "cmd" -> full path to cmd.exe.
        var resolved = SystemPaths.ResolveSystemTool("cmd");
        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveSystemTool_AlreadyRootedPath_ReturnedUnchanged()
    {
        const string p = @"C:\Windows\System32\netsh.exe";
        Assert.Equal(p, SystemPaths.ResolveSystemTool(p));
    }

    [Theory]
    [InlineData(@"sub\dir\tool.exe")]
    [InlineData("sub/dir/tool.exe")]
    public void ResolveSystemTool_ContainsSeparator_ReturnedUnchanged(string name)
    {
        // A path separator means the caller already chose a location — never rewrite it.
        Assert.Equal(name, SystemPaths.ResolveSystemTool(name));
    }

    [Fact]
    public void ResolveSystemTool_UnknownBareName_ReturnedUnchanged()
    {
        const string name = "definitely-not-a-real-tool-xyz.exe";
        Assert.Equal(name, SystemPaths.ResolveSystemTool(name));
    }

    [Fact]
    public void ResolveSystemTool_EmptyOrNull_ReturnedUnchanged()
    {
        Assert.Equal("", SystemPaths.ResolveSystemTool(""));
        Assert.Null(SystemPaths.ResolveSystemTool(null!));
    }
}
