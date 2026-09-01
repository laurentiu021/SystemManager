// SysManager · LegacyPanelServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="LegacyPanelService"/>. The catalog is asserted for integrity
/// (every entry well-formed, names unique) and <see cref="LegacyPanelService.Launch"/>
/// is verified to reject panels that are not part of the hard-coded catalog (the
/// security boundary). Actually launching an applet is a side effect and not unit-tested.
/// </summary>
public class LegacyPanelServiceTests
{
    [Fact]
    public void Panels_CatalogIsNotEmpty()
        => Assert.NotEmpty(LegacyPanelService.Panels);

    [Fact]
    public void Panels_EveryEntryHasNameAndLauncher()
    {
        Assert.All(LegacyPanelService.Panels, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.False(string.IsNullOrWhiteSpace(p.FileName));
            Assert.NotNull(p.Arguments); // may be empty, never null
        });
    }

    [Fact]
    public void Panels_NamesAreUnique()
    {
        var names = LegacyPanelService.Panels.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Panels_LaunchersUseKnownHosts()
    {
        var allowedHosts = new[] { "control.exe", "mmc.exe", "netplwiz.exe", "SystemPropertiesAdvanced.exe" };
        Assert.All(LegacyPanelService.Panels, p => Assert.Contains(p.FileName, allowedHosts));
    }

    [Fact]
    public void Launch_RejectsPanelNotInCatalog()
    {
        var svc = new LegacyPanelService();
        // A panel that looks plausible but is not the same instance as any catalog entry.
        var rogue = new LegacyPanel("Rogue", "not in catalog", "", "cmd.exe", "/c calc");
        Assert.False(svc.Launch(rogue));
    }

    [Fact]
    public void Launch_NullPanel_Throws()
    {
        var svc = new LegacyPanelService();
        Assert.Throws<ArgumentNullException>(() => svc.Launch(null!));
    }

    // ---------- the catalog is launched by path, not by name ----------

    [Fact]
    public void EveryPanel_ResolvesToARootedExistingProgram()
    {
        // The catalog is hard-coded, which is not the same as the program being fixed. With
        // UseShellExecute=true an unrooted name is resolved through HKCU's App Paths key and then PATH,
        // neither of which needs elevation to modify, so "control.exe" means whatever those lookups answer —
        // and when SysManager is elevated, that answer runs elevated.
        //
        // Behavioural rather than textual on purpose: the architecture guard scans for a bare literal handed
        // to a launch, and this service hands it a property, so the guard cannot see this service at all.
        foreach (var panel in LegacyPanelService.Panels)
        {
            var resolved = SystemPaths.ResolveSystemTool(panel.FileName);

            Assert.True(Path.IsPathRooted(resolved),
                $"'{panel.Name}' launches '{panel.FileName}', which does not resolve to a rooted path");
            Assert.True(File.Exists(resolved),
                $"'{panel.Name}' resolves to '{resolved}', which does not exist");
        }
    }

    [Fact]
    public void Launch_PassesTheCatalogNameThroughTheResolver()
    {
        // The other half. The test above proves every catalog entry CAN be pinned; this proves Launch
        // actually pins it. Without both, a correct catalog could still be launched by bare name — which is
        // exactly the state this service shipped in.
        //
        // Source text because the alternative is starting Control Panel from a unit test. Matches the code
        // shape rather than a comment, and asserts the un-resolved form is gone, so deleting the call cannot
        // leave this green.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "SysManager", "SysManager", "Services", "LegacyPanelService.cs"));

        Assert.Contains("FileName = SystemPaths.ResolveSystemTool(panel.FileName),", source);
        Assert.DoesNotContain("FileName = panel.FileName,", source);
    }

    /// <summary>The repository root — the source this test reads is not copied to the test output.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SUPPORT.md"))
                && File.Exists(Path.Combine(dir.FullName, "README.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
