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

    // ── winget resolution (ultra-audit P1: bare-name binary-planting LPE) ──
    //
    // winget is an MSIX execution alias, not a System32 tool, so the System32 probes never match.
    // Before ResolveWinget, ResolveSystemTool("winget") fell through and returned the UNROOTED bare
    // name "winget" — and launched with UseShellExecute=false, an unrooted name lets CreateProcess
    // search the app's OWN directory first, so an attacker-planted winget.exe beside the portable
    // .exe would run with the app's (possibly elevated) privileges. The resolver must ALWAYS return
    // a rooted path and NEVER the bare name / the user-writable %LOCALAPPDATA% alias.

    [Theory]
    [InlineData("winget")]
    [InlineData("winget.exe")]
    [InlineData("WinGet")]      // case-insensitive
    [InlineData("WINGET.EXE")]
    public void ResolveSystemTool_Winget_NeverReturnsBareName_AlwaysRooted(string name)
    {
        var resolved = SystemPaths.ResolveSystemTool(name);
        Assert.NotEqual(name, resolved);
        Assert.True(Path.IsPathRooted(resolved),
            $"winget must resolve to a rooted path (got '{resolved}') so CreateProcess can't search the app directory");
        Assert.EndsWith("winget.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWinget_NeverPointsAtUserWritableLocalAppData()
    {
        // The per-user execution alias lives in the user-WRITABLE %LOCALAPPDATA%\Microsoft\WindowsApps
        // — trusting it for an elevated launch would defeat the whole hardening. The resolver must
        // point at the admin-only WindowsApps install (or the rooted System32 fail-closed path),
        // never at LocalAppData.
        var resolved = SystemPaths.ResolveWinget();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.False(
            resolved.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
            $"resolved winget path must not be under user-writable LocalAppData (got '{resolved}')");
    }

    [Fact]
    public void ResolveWinget_WhenInstalled_ResolvesUnderWindowsAppsAndExists()
    {
        // CI (windows-latest) ships App Installer, so winget should resolve to a real, existing
        // binary under %ProgramFiles%\WindowsApps. If a future runner image lacks it, the resolver
        // fails closed to a rooted System32 path (asserted by the theory above), so gate this
        // stronger assertion on the file actually existing.
        var resolved = SystemPaths.ResolveWinget();
        if (File.Exists(resolved))
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            Assert.StartsWith(windowsApps, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Microsoft.DesktopAppInstaller_", resolved, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ParsePackageVersion_ParsesVersionSegment_NumericallyOrdered()
    {
        // 1.29 must sort ABOVE 1.9 — an ordinal string compare would get this wrong, which is why
        // the resolver parses System.Version rather than comparing folder-name strings.
        var v129 = SystemPaths.ParsePackageVersion(
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.279.0_x64__8wekyb3d8bbwe");
        var v9 = SystemPaths.ParsePackageVersion(
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe");
        Assert.True(v129 > v9, $"1.29.x ({v129}) must sort above 1.9.x ({v9})");
    }

    [Fact]
    public void ParsePackageVersion_UnexpectedShape_ReturnsZeroSortsLast()
    {
        // A folder that doesn't match the expected shape must sort last, never throw.
        var bad = SystemPaths.ParsePackageVersion(@"C:\Program Files\WindowsApps\SomethingElse");
        Assert.Equal(new Version(0, 0), bad);
    }
}
