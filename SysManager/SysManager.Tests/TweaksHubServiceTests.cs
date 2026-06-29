// SysManager · TweaksHubServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class TweaksHubServiceTests
{
    private static PrivacyToggle Toggle(string name, string path, bool enabled) => new()
    {
        Name = name, Description = "d", Category = "c",
        RegistryPath = path, ValueName = "v", EnabledValue = 1, DisabledValue = 0, IsEnabled = enabled,
    };

    private static TweakItem Item(string path, bool selected, bool applied)
    {
        var t = TweakItem.From(Toggle("n", path, applied));
        t.IsSelected = selected;
        return t;
    }

    // ── Tier classification (the real risk/elevation boundary) ─────────────

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", TweakTier.Advanced)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Foo", TweakTier.Advanced)]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", TweakTier.Essential)]
    [InlineData(@"HKEY_CURRENT_USER\Software\Foo", TweakTier.Essential)]
    public void ClassifyTier_ByHive(string path, TweakTier expected)
        => Assert.Equal(expected, TweakItem.ClassifyTier(path));

    [Fact]
    public void From_CopiesAppliedStateFromToggle()
    {
        Assert.True(TweakItem.From(Toggle("n", @"HKCU\x", enabled: true)).IsApplied);
        Assert.False(TweakItem.From(Toggle("n", @"HKCU\x", enabled: false)).IsApplied);
    }

    // ── Pending counts ─────────────────────────────────────────────────────

    [Fact]
    public void PendingApplyCount_CountsSelectedNotYetApplied()
    {
        var items = new[]
        {
            Item(@"HKCU\a", selected: true,  applied: false), // would apply
            Item(@"HKCU\b", selected: true,  applied: true),  // already applied → not pending-apply
            Item(@"HKCU\c", selected: false, applied: false), // not selected
        };
        Assert.Equal(1, TweaksHubService.PendingApplyCount(items));
    }

    [Fact]
    public void PendingUndoCount_CountsSelectedAlreadyApplied()
    {
        var items = new[]
        {
            Item(@"HKCU\a", selected: true,  applied: true),  // would undo
            Item(@"HKCU\b", selected: true,  applied: true),  // would undo
            Item(@"HKCU\c", selected: true,  applied: false), // not applied → not pending-undo
            Item(@"HKCU\d", selected: false, applied: true),  // not selected
        };
        Assert.Equal(2, TweaksHubService.PendingUndoCount(items));
    }

    [Fact]
    public void PendingCounts_EmptyInput_AreZero()
    {
        Assert.Equal(0, TweaksHubService.PendingApplyCount([]));
        Assert.Equal(0, TweaksHubService.PendingUndoCount([]));
    }
}
