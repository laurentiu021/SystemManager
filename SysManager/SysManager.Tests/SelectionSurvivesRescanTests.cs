// SysManager · SelectionSurvivesRescanTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// A rescan must keep the ticks the user set, in Shortcut Cleaner and Browser Cleaner (#2304).
/// </summary>
/// <remarks>
/// Both tabs rebuild their list from a fresh scan and both arrive PRE-SELECTED, so a rescan did not merely
/// forget the user's choice — it reversed it, and the next "Delete selected" acted on what they had
/// excluded. <c>RefreshOnF5</c> is <c>ScanCommand</c> on both, so pressing F5 was enough. Same class as
/// #2300 (drives) and #2301 (cleanup categories).
/// <para>Tested through each view model's own pure carry-forward rather than through the command, because
/// both take a CONCRETE service whose scan walks the real filesystem: driving the command would make these
/// assertions depend on the machine's actual broken shortcuts and installed browsers. The last two tests
/// hold each view model to calling its carry-forward, and to calling it before the rebuild, so the logic
/// cannot end up correct and wired to nothing.</para>
/// </remarks>
public class SelectionSurvivesRescanTests
{
    // ── Shortcut Cleaner ────────────────────────────────────────────────────

    private static BrokenShortcut Lnk(string path, bool selected = true) => new()
    {
        Name = Path.GetFileNameWithoutExtension(path),
        ShortcutPath = path,
        TargetPath = @"C:\gone\missing.exe",
        Location = "Desktop",
        IsSelected = selected
    };

    [Fact]
    public void Shortcuts_FirstScan_LeavesTheDefaultsAlone()
    {
        // Every broken shortcut is a candidate, so the default is ticked. Breaking this would ship a tab
        // where the first scan selects nothing and the delete button is dead.
        BrokenShortcut[] fresh = [Lnk(@"C:\a.lnk"), Lnk(@"C:\b.lnk")];

        ShortcutCleanerViewModel.CarryForwardSelection([], fresh);

        Assert.All(fresh, s => Assert.True(s.IsSelected));
    }

    [Fact]
    public void Shortcuts_Rescan_KeepsOneTheUserUnticked()
    {
        // THE regression: unticking is how the user says "keep this shortcut".
        BrokenShortcut[] previous = [Lnk(@"C:\a.lnk", selected: false), Lnk(@"C:\b.lnk", selected: true)];
        BrokenShortcut[] fresh = [Lnk(@"C:\a.lnk"), Lnk(@"C:\b.lnk")];

        ShortcutCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Shortcuts_Rescan_KeepsEverythingUnticked_WhenTheUserUntickedEverything()
    {
        // The case a "did they select anything?" test gets wrong: unticking the lot is a decision, and
        // reading it as "nothing chosen yet" re-ticks all of it — which is the bug.
        BrokenShortcut[] previous = [Lnk(@"C:\a.lnk", selected: false), Lnk(@"C:\b.lnk", selected: false)];
        BrokenShortcut[] fresh = [Lnk(@"C:\a.lnk"), Lnk(@"C:\b.lnk")];

        ShortcutCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.DoesNotContain(fresh, s => s.IsSelected);
    }

    [Fact]
    public void Shortcuts_APathThatIsNewlyBroken_TakesTheDefault()
    {
        BrokenShortcut[] previous = [Lnk(@"C:\a.lnk", selected: false)];
        BrokenShortcut[] fresh = [Lnk(@"C:\a.lnk"), Lnk(@"C:\new.lnk")];

        ShortcutCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Shortcuts_PathMatchingIgnoresCase()
    {
        // Windows paths are case-insensitive, so a scan returning a different casing must not be treated as
        // a different shortcut and silently re-ticked.
        BrokenShortcut[] previous = [Lnk(@"C:\Users\Public\Desktop\App.lnk", selected: false)];
        BrokenShortcut[] fresh = [Lnk(@"c:\users\public\desktop\app.lnk")];

        ShortcutCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
    }

    // ── Browser Cleaner ────────────────────────────────────────────────────

    private static BrowserCleanupItem Item(string browser, string category, bool sensitive, bool selected) => new()
    {
        Browser = browser,
        Category = category,
        Description = category + " for " + browser,
        Paths = [@"C:\nowhere\" + browser + @"\" + category],
        IsSensitive = sensitive,
        IsSelected = selected
    };

    /// <summary>A fresh scan result, selected the way the service selects: everything not sensitive.</summary>
    private static BrowserCleanupItem Scanned(string browser, string category, bool sensitive = false) =>
        Item(browser, category, sensitive, selected: !sensitive);

    [Fact]
    public void Browsers_FirstScan_LeavesTheDefaultsAlone()
    {
        BrowserCleanupItem[] fresh = [Scanned("Chrome", "Cache"), Scanned("Chrome", "Cookies", sensitive: true)];

        BrowserCleanerViewModel.CarryForwardSelection([], fresh);

        Assert.True(fresh[0].IsSelected);
        Assert.False(fresh[1].IsSelected);
    }

    [Fact]
    public void Browsers_Rescan_KeepsCacheTheUserUnticked()
    {
        // The dangerous direction: cache is pre-selected, so unticking it was reversed and the data got
        // deleted anyway.
        BrowserCleanupItem[] previous = [Item("Chrome", "Cache", sensitive: false, selected: false)];
        BrowserCleanupItem[] fresh = [Scanned("Chrome", "Cache")];

        BrowserCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void Browsers_Rescan_KeepsCookiesTheUserDeliberatelyTicked()
    {
        // The other direction, and the reason this tab is wrong both ways: cookies are deliberately NOT
        // pre-selected, so ticking one is an explicit choice to be signed out — and a rescan revoked it.
        BrowserCleanupItem[] previous = [Item("Chrome", "Cookies", sensitive: true, selected: true)];
        BrowserCleanupItem[] fresh = [Scanned("Chrome", "Cookies", sensitive: true)];

        BrowserCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void Browsers_TheSameCategoryInTwoBrowsers_IsTrackedSeparately()
    {
        // Category alone is not the identity. Keyed on category only, unticking Chrome's cache would also
        // untick Edge's — or worse, one browser's choice would silently apply to another.
        BrowserCleanupItem[] previous =
        [
            Item("Chrome", "Cache", sensitive: false, selected: false),
            Item("Edge", "Cache", sensitive: false, selected: true)
        ];
        BrowserCleanupItem[] fresh = [Scanned("Chrome", "Cache"), Scanned("Edge", "Cache")];

        BrowserCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Browsers_ABrowserInstalledSinceTheLastScan_TakesTheDefault()
    {
        BrowserCleanupItem[] previous = [Item("Chrome", "Cache", sensitive: false, selected: false)];
        BrowserCleanupItem[] fresh = [Scanned("Chrome", "Cache"), Scanned("Firefox", "Cache")];

        BrowserCleanerViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    // ── Both scans must actually call their carry-forward, before rebuilding ──

    private static string ViewModelSource(string name)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(BrokenShortcut).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "SysManager", "ViewModels", name + ".cs"));
    }

    [Theory]
    [InlineData("ShortcutCleanerViewModel", "BrokenShortcuts.ReplaceWith(results)")]
    [InlineData("BrowserCleanerViewModel", "Items.ReplaceWith(items)")]
    public void TheScan_CallsCarryForwardSelection_BeforeItRebuildsTheList(string viewModel, string rebuild)
    {
        // Without this, the logic above could be perfectly correct and reachable from nothing — the defect
        // class this repo hits most often. The ORDER matters as much as the call: run after the rebuild and
        // the previous ticks are already gone.
        var source = ViewModelSource(viewModel);

        var call = source.IndexOf("CarryForwardSelection(previous,", StringComparison.Ordinal);
        Assert.True(call > 0,
            $"{viewModel} does not call CarryForwardSelection(previous, …) — either the rescan no longer "
            + "preserves the user's ticks, or this guard needs re-pointing.");

        var replace = source.IndexOf(rebuild, StringComparison.Ordinal);
        Assert.True(replace > 0, $"{viewModel} no longer contains '{rebuild}' — re-point this guard.");
        Assert.True(call < replace,
            $"{viewModel} calls CarryForwardSelection after '{rebuild}', so the previous ticks are already gone.");
    }
}
