// SysManager · LegacyPanelServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
}
