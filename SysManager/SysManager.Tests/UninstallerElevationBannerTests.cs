// SysManager · UninstallerElevationBannerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Xml.Linq;

namespace SysManager.Tests;

/// <summary>
/// Pins the Uninstaller's elevated-state banner: the one place in the app where being an administrator
/// takes a feature AWAY.
/// </summary>
/// <remarks>
/// <para>The gate itself is deliberate and stays — <c>CanUninstall</c> requires an unelevated process so
/// each app's own uninstaller can raise its own UAC prompt. What was wrong was how the tab said so. It
/// used the identical gold <c>WarningBgSubtle</c> banner that the other 30 tabs use to mean "you are
/// elevated, so you can now do more", and filled it with "Uninstall is disabled in administrator
/// sessions. Reopen SysManager normally to continue." Two problems in one control: the colour promised
/// more access while the words removed it, and the instruction asked for something the app cannot help
/// with — there is no de-elevation path anywhere in the codebase, only
/// <c>AdminHelper.RelaunchAsAdmin</c>, which goes one way.</para>
/// <para>Asserted against the shipped markup because that is the only place the defect existed: every
/// view-model test passed throughout, since <c>CanUninstall</c> was behaving exactly as written.
/// <c>ArchitectureTests.EveryGoldElevationBanner_PromisesMoreAccess</c> guards the colour convention
/// across all views; this file pins THIS banner's copy, which that check cannot see once the banner is
/// no longer gold.</para>
/// </remarks>
public class UninstallerElevationBannerTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// The banner shown WHEN elevated. Both banners live in the same <c>Grid.Row="1"</c> slot and are
    /// switched by <c>IsElevated</c>, so they are told apart by the converter's Inverse parameter — a
    /// file-wide text search would read whichever came first.
    /// </summary>
    private static XElement ElevatedBanner()
    {
        var doc = XDocument.Load(ViewPath());
        var banners = doc.Descendants(Presentation + "Border")
            .Where(b =>
            {
                var visibility = b.Attribute("Visibility")?.Value ?? "";
                return visibility.Contains("IsElevated", StringComparison.Ordinal)
                    && !visibility.Contains("Inverse", StringComparison.Ordinal);
            })
            .ToList();

        Assert.Single(banners);
        return banners[0];
    }

    private static string BannerMessage(XElement banner) =>
        banner.Descendants(Presentation + "TextBlock")
            .Select(t => t.Attribute("Text")?.Value ?? "")
            // Drop the icon glyph (a single private-use codepoint) and any bound value.
            .Where(t => t.Length >= 20 && !t.StartsWith('{'))
            .Select(t => string.Join(" ", t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Single();

    [Fact]
    public void TheElevatedBannerIsNeutral_NotTheGoldMoreUnlockedTreatment()
    {
        var banner = ElevatedBanner();

        var background = banner.Attribute("Background")?.Value ?? "";
        Assert.DoesNotContain("Warning", background, StringComparison.Ordinal);
        // Specifically the neutral treatment of the not-elevated banner directly above it, so the slot
        // does not visibly change shape or colour at the moment the user elevates on another tab.
        Assert.Contains("Surface2", background, StringComparison.Ordinal);

        // The gold accent stripe is part of the same "unlocked" vocabulary and must go with it.
        Assert.DoesNotContain(
            banner.Descendants(Presentation + "Border"),
            inner => (inner.Attribute("Background")?.Value ?? "").Contains("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public void TheElevatedBannerGivesTheReason_NotJustTheRestriction()
    {
        var message = BannerMessage(ElevatedBanner());

        // "so each app can show you its own permission prompt" is the load-bearing half: without a reason
        // the restriction reads as a bug on the app's most consequential tab. Asserted on the substance
        // rather than the exact sentence, so the copy can be improved without a test edit.
        Assert.Contains("own", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prompt", message, StringComparison.OrdinalIgnoreCase);

        // And it must not reintroduce the jargon the persona cannot parse.
        Assert.DoesNotContain("administrator sessions", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheGateItselfIsUnchanged_ElevationAloneBlocksUninstall()
    {
        // Guards against "fixing" the banner by deleting the restriction it describes. The banner is only
        // honest while the gate is real, and the gate exists so each uninstaller raises its own UAC prompt
        // rather than silently inheriting ours.
        //
        // The list is populated first, and both states are asserted. CanUninstall is
        // `NotBusy && HasApps && !IsElevated`, so on an unscanned list HasApps is false and CanExecute
        // returns false no matter what elevation says — asserting only the elevated case would pass with
        // the elevation term deleted, which is precisely the change this test exists to catch.
        var vm = new SysManager.ViewModels.UninstallerViewModel(
            new SysManager.Services.UninstallerService(new SysManager.Services.PowerShellRunner()));
        vm.IsElevated = false;
        vm.AllApps.Add(new SysManager.Models.InstalledApp { Name = "app", Id = "id" });
        vm.FilterText = "app";   // triggers ApplyFilter, which refreshes AppCount
        vm.FilterText = "";

        Assert.True(vm.UninstallSelectedCommand.CanExecute(null),
            "With a populated list and no elevation, uninstall must be available — otherwise the elevated " +
            "assertion below proves nothing.");

        vm.IsElevated = true;
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
    }

    // Walks up from the test binaries to the app project — .xaml is not copied to the output.
    private static string ViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "SysManager", "Views", "UninstallerView.xaml");
        Assert.True(File.Exists(path), $"UninstallerView.xaml not found at {path}");
        return path;
    }
}
