// SysManager · SystemPathsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Services;

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

    [Theory]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    [InlineData("POWERSHELL.EXE")]
    public void ResolveSystemTool_PowerShellMissing_ReturnsRootedFailClosedPath(string name)
    {
        const string trustedSystemDirectory = @"C:\TrustedWindows\System32";

        var resolved = SystemPaths.ResolveSystemTool(
            name,
            trustedSystemDirectory,
            static _ => false);

        Assert.Equal(
            @"C:\TrustedWindows\System32\WindowsPowerShell\v1.0\powershell.exe",
            resolved,
            ignoreCase: true);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.NotEqual(name, resolved);
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

    // ── winget resolution (bare-name binary-planting LPE) ──────────────────
    //
    // winget is an MSIX execution alias, not a System32 tool, so the System32 probes never match.
    // Before ResolveWinget, ResolveSystemTool("winget") fell through and returned the UNROOTED bare
    // name "winget" — and launched with UseShellExecute=false, an unrooted name lets CreateProcess
    // search the app's OWN directory first, so an attacker-planted winget.exe beside the portable
    // .exe would run with the app's (possibly elevated) privileges. The resolver must ALWAYS return
    // a rooted path and NEVER the bare name, and for an elevated launch never the user-writable
    // %LOCALAPPDATA% alias. Without administrator rights the alias is the one winget there is (#2699).

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
    public void ResolveWinget_ForAnElevatedLaunch_NeverPointsAtUserWritableLocalAppData()
    {
        // The per-user execution alias lives in the user-WRITABLE %LOCALAPPDATA%\Microsoft\WindowsApps
        // — trusting it for an elevated launch would defeat the whole hardening. The resolver must
        // point at the admin-only WindowsApps install (or the rooted System32 fail-closed path),
        // never at LocalAppData.
        var resolved = SystemPaths.ResolveWinget(isElevated: true);
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
        var resolved = SystemPaths.ResolveWinget(isElevated: true);
        if (File.Exists(resolved))
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            Assert.StartsWith(windowsApps, resolved, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Microsoft.DesktopAppInstaller_", resolved, StringComparison.OrdinalIgnoreCase);
        }
    }

    private const string ProgramFiles = @"C:\Program Files";
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";
    private const string Package = "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe";

    private static IEnumerable<string> NotListed(string folder, string pattern) =>
        throw new UnauthorizedAccessException($@"Access to the path '{folder}' is denied.");

    [Fact]
    public void ResolveWinget_WithoutAdministratorRights_IsTheUsersOwnAlias_AndListsNothing()
    {
        // A process without administrator rights may not list WindowsApps, so the lookup always fell back to a
        // System32 path that does not exist, and winget was "not available" in the mode SysManager starts in (#2699).
        var listed = false;

        var resolved = SystemPaths.ResolveWinget(false, ProgramFiles, LocalAppData,
            (_, _) => { listed = true; return []; }, _ => true);

        Assert.Equal(@"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\winget.exe", resolved);
        Assert.False(listed);
    }

    [Fact]
    public void ResolveWinget_WithoutAdministratorRights_AndNoProfileFolder_FailsClosed()
    {
        // An unrooted alias path would be looked up from the current directory, which can be anywhere.
        var resolved = SystemPaths.ResolveWinget(false, ProgramFiles, "", (_, _) => [], _ => true);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "winget.exe"), resolved);
    }

    [Fact]
    public void ResolveWinget_Elevated_WhenWindowsAppsCannotBeListed_FailsClosed_NotToTheAlias()
    {
        var resolved = SystemPaths.ResolveWinget(true, ProgramFiles, LocalAppData, NotListed, _ => true);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "winget.exe"), resolved);
    }

    [Fact]
    public void ResolveWinget_Elevated_TakesTheNewestPackageThatHoldsWinget()
    {
        var windowsApps = Path.Combine(ProgramFiles, "WindowsApps");
        var older = Path.Combine(windowsApps, "Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe");
        var newer = Path.Combine(windowsApps, "Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe");
        var resources = Path.Combine(windowsApps, "Microsoft.DesktopAppInstaller_1.30.0.0_neutral_~_8wekyb3d8bbwe");
        string? listedIn = null, listedFor = null;

        var resolved = SystemPaths.ResolveWinget(true, ProgramFiles, LocalAppData,
            (folder, pattern) => { (listedIn, listedFor) = (folder, pattern); return [older, resources, newer]; },
            path => path != Path.Combine(resources, "winget.exe"));

        Assert.Equal(Path.Combine(newer, "winget.exe"), resolved);
        Assert.Equal(windowsApps, listedIn);
        Assert.Equal(Package, listedFor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveSystemTool_Winget_FollowsTheElevationItIsGiven(bool isElevated)
    {
        var resolved = SystemPaths.ResolveSystemTool("winget", isElevated);
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(!isElevated, string.Equals(alias, resolved, StringComparison.OrdinalIgnoreCase));
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

    // ---------- the running build's own extraction directory (#1999) ----------
    //
    // The shipped exe is single-file with IncludeNativeLibrariesForSelfExtract, so the host unpacks
    // native libraries into %TEMP%\.net\<app>\<hash> and AppContext.BaseDirectory points there. Both
    // wholesale %TEMP% sweeps (Tune-Up's temp cleanup and Deep Cleanup's "Temporary files") must skip
    // that tree, so the rule lives here rather than being copied into each service.

    [Theory]
    // A sibling whose name merely STARTS with the excluded path must not be swallowed: this is the
    // boundary bug a bare StartsWith would introduce.
    [InlineData(@"C:\Temp\.net\SysManagerX\lib.dll", @"C:\Temp\.net\SysManager", false)]
    [InlineData(@"C:\Temp\.net\SysManager\lib.dll", @"C:\Temp\.net\SysManager", true)]
    [InlineData(@"C:\Temp\.net\SysManager", @"C:\Temp\.net\SysManager", true)]
    [InlineData(@"C:\Temp\.net\SysManager\", @"C:\Temp\.net\SysManager", true)]
    [InlineData(@"C:\Temp\other\lib.dll", @"C:\Temp\.net\SysManager", false)]
    [InlineData(@"C:\Temp\.net\sysmanager\lib.dll", @"C:\Temp\.net\SysManager", true)]  // case-insensitive
    // A drive root (#2825): Path.TrimEndingDirectorySeparator is root-aware and leaves "D:\" as "D:\"
    // rather than the ambiguous "D:", so appending a separator unconditionally built the prefix "D:\\" —
    // which nothing under the root ever starts with.
    [InlineData(@"D:\Games\App", @"D:\", true)]
    [InlineData(@"D:\", @"D:\", true)]
    [InlineData(@"D:\Games\App", @"C:\", false)]
    public void IsInsideSubtree_ComparesOnADirectoryBoundary(string candidate, string subtree, bool expected)
    {
        Assert.Equal(expected, SystemPaths.IsInsideSubtree(candidate, subtree));
    }

    [Fact]
    public void IsInsideSubtree_WithNoSubtree_ExcludesNothing()
    {
        // Null must exclude NOTHING, not everything: the walkers pass this straight through, so
        // getting it backwards would silently turn both cleanups into no-ops.
        Assert.False(SystemPaths.IsInsideSubtree(@"C:\Temp\anything.txt", null));
        Assert.False(SystemPaths.IsInsideSubtree(@"C:\Temp\anything.txt", ""));
        Assert.False(SystemPaths.IsInsideSubtree(@"C:\Temp\anything.txt", "   "));
    }

    [Fact]
    public void OwnExtractionDirectory_IsResolvedAndAbsolute()
    {
        // Under the test host this is the output folder rather than a temp extraction directory, which
        // is the point: outside a single-file run the exclusion is inert. It must still resolve to an
        // absolute path with no trailing separator, or the boundary comparison cannot work.
        var own = SystemPaths.OwnExtractionDirectory;

        Assert.False(string.IsNullOrWhiteSpace(own));
        Assert.True(Path.IsPathFullyQualified(own!));
        Assert.Equal(Path.TrimEndingDirectorySeparator(own!), own);
        Assert.True(SystemPaths.IsInsideSubtree(own!, own));
    }

    [Fact]
    public void ResolveSystemTool_WindowsDirectoryTool_ResolvesToRootedExistingPath()
    {
        // explorer.exe is the reason the Windows-directory fallback exists. It is NOT in System32, so a
        // System32-only probe returned the bare name unchanged — which reads as resolution and pins nothing.
        // Eight launches were routed through this helper on the strength of that fallback, so without this
        // test the whole change could regress to a no-op that still looks like a fix.
        var resolved = SystemPaths.ResolveSystemTool("explorer.exe");

        Assert.True(Path.IsPathRooted(resolved), $"explorer.exe must resolve to a rooted path, got '{resolved}'");
        Assert.True(File.Exists(resolved), $"resolved explorer.exe must exist, got '{resolved}'");
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "explorer.exe"),
            resolved,
            ignoreCase: true);
    }
    [Theory]
    // Redirected by the host variable: taken as given, only normalised.
    [InlineData(@"D:\bundles\", null, @"D:\bundles")]
    [InlineData(@"D:\bundles", null, @"D:\bundles")]
    // Not redirected: <temp>\.net, with or without the trailing separator the environment supplies.
    [InlineData(null, @"C:\Temp\", @"C:\Temp\.net")]
    [InlineData(null, @"C:\Temp", @"C:\Temp\.net")]
    [InlineData("", @"C:\Temp\", @"C:\Temp\.net")]
    [InlineData("   ", @"C:\Temp\", @"C:\Temp\.net")]
    public void ResolveBundleExtractionRoot_PrefersTheHostOverrideThenTempDotNet(
        string? overrideDir, string? tempPath, string expected)
    {
        // The root matters more than this app's own leaf under it: excluding only the leaf left every
        // OTHER single-file .NET app's extracted native libraries exposed to a wholesale %TEMP% sweep.
        Assert.Equal(expected, SystemPaths.ResolveBundleExtractionRoot(overrideDir, tempPath ?? @"C:\Temp"));
    }

    [Fact]
    public void BundleExtractionRoot_IsResolvedAndContainsNothingElsesTemp()
    {
        // Resolved once at type initialisation, so a test can only assert shape — which is exactly why
        // the branching lives in ResolveBundleExtractionRoot above.
        var root = SystemPaths.BundleExtractionRoot;

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathFullyQualified(root!));
        Assert.Equal(Path.TrimEndingDirectorySeparator(root!), root);

        // A sibling of the extraction root inside the same temp folder must NOT be excluded, or a sweep
        // would silently spare unrelated temp files.
        Assert.False(SystemPaths.IsInsideSubtree(Path.Join(Path.GetTempPath(), "unrelated.tmp"), root));
    }

    // ── the temp sweeps' own folder never comes from TMP or TEMP (#2700) ──

    [Theory]
    [InlineData(@"C:\Users\me", @"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\Users\me\", @"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"D:\Profiles\me", @"D:\Profiles\me\AppData\Local\Temp")]
    public void ResolveUserTempRoot_IsAppDataLocalTempUnderTheProfile(string profile, string expected) =>
        Assert.Equal(expected, SystemPaths.ResolveUserTempRoot(profile));

    /// <summary>
    /// Every input that cannot be a real profile folder resolves to nothing, so the sweep cleans no user folder at
    /// all rather than a guessed one. A drive or share root is refused outright: <c>AppData\Local\Temp</c> under it
    /// is not a temp folder Windows made.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Users\me")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    public void ResolveUserTempRoot_RefusesWhatCannotBeAProfile(string? profile) =>
        Assert.Null(SystemPaths.ResolveUserTempRoot(profile));

    [Fact]
    public void UserTempRoot_IsThisAccountsOwnProfileTemp()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));   // else the comparison below is vacuous

        Assert.Equal(Path.Join(profile, "AppData", "Local", "Temp"), SystemPaths.UserTempRoot, ignoreCase: true);
    }

    /// <summary>
    /// Deep Cleanup and Quick Cleanup read two different variables, TMP first through <c>GetTempPath</c> and TEMP
    /// directly, so they could measure and delete two different folders. Both now take the one resolved root.
    /// </summary>
    [Fact]
    public void DeepCleanupAndQuickCleanup_SweepTheSameUserTempFolder()
    {
        var root = SystemPaths.UserTempRoot;
        Assert.False(string.IsNullOrEmpty(root));   // else both sides could agree on nothing

        Assert.Equal(root, new SystemCleanupRoots().UserTemp);
        Assert.Equal(root, TuneUpService.TempRoots()[0]);
    }

    [Fact]
    public void IsInsideAnySubtree_ExcludesAMatchInAnyPosition_AndIgnoresBlanks()
    {
        // The walkers pass the extraction root AND this process's own leaf, so a candidate matching
        // either has to be excluded regardless of order — and a null among them must exclude nothing,
        // since BundleExtractionRoot can fail to resolve on an unusual machine.
        const string other = @"C:\Temp\.net\SomeOtherApp\abc123\native.dll";
        const string root = @"C:\Temp\.net";
        const string own = @"C:\Temp\.net\SysManager-v1.0.0\hash";

        Assert.True(SystemPaths.IsInsideAnySubtree(other, root, own));
        Assert.True(SystemPaths.IsInsideAnySubtree(other, own, root));
        Assert.True(SystemPaths.IsInsideAnySubtree(other, null, root));
        Assert.False(SystemPaths.IsInsideAnySubtree(other, null, ""));
        Assert.False(SystemPaths.IsInsideAnySubtree(@"C:\Temp\unrelated.tmp", root, own));
        Assert.False(SystemPaths.IsInsideAnySubtree(other));
    }

}
