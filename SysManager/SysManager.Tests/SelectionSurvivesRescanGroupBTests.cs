// SysManager · SelectionSurvivesRescanGroupBTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// A rescan must keep the ticks the user set in Debloater, Uninstaller and App Blocker (#2304, group B).
/// </summary>
/// <remarks>
/// These three are the milder half of the class. Their rows arrive UNSELECTED, so a refresh cleared the
/// selection rather than reversing it: the Remove / Uninstall / Unblock button simply stopped doing anything
/// until every row was ticked again. Nothing here could act on a row the user had excluded, which is why
/// they were fixed after the four that could.
/// <para>All three services are concrete, and one of the three call sites is a private method reached only
/// from a real scan, so the KEY each tab uses is asserted against <see cref="SelectionCarry"/> directly —
/// the shared body's own behaviour is covered by <c>SelectionCarryTests</c>, so what is worth pinning here
/// is the identity each tab chose, which is the part that differs and the part that is easy to get wrong.
/// A source guard holds all three call sites.</para>
/// </remarks>
public class SelectionSurvivesRescanGroupBTests
{
    // ── Debloater: keyed on the package FAMILY name, so a version bump does not lose the tick ──

    private static StoreApp Store(string family, string version, bool selected) => new()
    {
        PackageFullName = $"{family}_{version}_x64__8wekyb3d8bbwe",
        PackageFamilyName = family,
        Name = family,
        DisplayName = family,
        Publisher = "Test",
        Version = version,
        IsSelected = selected
    };

    [Fact]
    public void Debloater_ATickSurvivesTheAppBeingUpdatedBetweenScans()
    {
        // PackageFullName carries the version, so keying on it would make an updated app look brand new and
        // silently drop the tick. The family name is what stays the same.
        StoreApp[] previous = [Store("Microsoft.Test", "1.0.0.0", selected: true)];
        StoreApp[] fresh = [Store("Microsoft.Test", "2.0.0.0", selected: false)];

        SelectionCarry.Apply(previous, fresh, a => a.PackageFamilyName, StringComparer.OrdinalIgnoreCase);

        Assert.NotEqual(previous[0].PackageFullName, fresh[0].PackageFullName);   // it really did change
        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void Debloater_TwoDifferentAppsStaySeparate()
    {
        StoreApp[] previous = [Store("Vendor.A", "1.0", selected: true), Store("Vendor.B", "1.0", selected: false)];
        StoreApp[] fresh = [Store("Vendor.A", "1.0", selected: false), Store("Vendor.B", "1.0", selected: false)];

        SelectionCarry.Apply(previous, fresh, a => a.PackageFamilyName, StringComparer.OrdinalIgnoreCase);

        Assert.True(fresh[0].IsSelected);
        Assert.False(fresh[1].IsSelected);
    }

    // ── Uninstaller: keyed on id AND name, because a registry-sourced entry can have no id ──

    private static InstalledApp Installed(string id, string name, bool selected) =>
        new() { Id = id, Name = name, IsSelected = selected };

    [Fact]
    public void Uninstaller_ATickSurvivesARescan()
    {
        InstalledApp[] previous = [Installed("Vendor.App", "Vendor App", selected: true)];
        InstalledApp[] fresh = [Installed("Vendor.App", "Vendor App", selected: false)];

        SelectionCarry.Apply(previous, fresh, a => (a.Id, a.Name));

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void Uninstaller_AppsWithNoIdAreNotCollapsedIntoOne()
    {
        // THE reason the key is a pair. This list is not winget-only, and an entry discovered through the
        // registry can have an empty id — keyed on id alone every such app becomes the same row, so one
        // tick would spread to all of them.
        InstalledApp[] previous =
        [
            Installed("", "Legacy Tool One", selected: true),
            Installed("", "Legacy Tool Two", selected: false)
        ];
        InstalledApp[] fresh =
        [
            Installed("", "Legacy Tool One", selected: false),
            Installed("", "Legacy Tool Two", selected: false)
        ];

        SelectionCarry.Apply(previous, fresh, a => (a.Id, a.Name));

        Assert.True(fresh[0].IsSelected);
        Assert.False(fresh[1].IsSelected);
    }

    // ── App Blocker: keyed on the executable name, which IS the identity ──

    [Fact]
    public void AppBlocker_ATickSurvivesARefresh_AndMatchesCaseInsensitively()
    {
        // An IFEO block is per executable NAME and the registry records nothing else, so that is the whole
        // identity. Windows compares executable names case-insensitively, so a refresh reporting different
        // capitalisation must not read as a different app.
        BlockedApp[] previous = [new() { ExecutableName = "Notepad.exe", IsSelected = true }];
        BlockedApp[] fresh = [new() { ExecutableName = "notepad.exe", IsSelected = false }];

        SelectionCarry.Apply(previous, fresh, a => a.ExecutableName, StringComparer.OrdinalIgnoreCase);

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void AppBlocker_AnAppUnblockedElsewhereIsSimplyGone()
    {
        BlockedApp[] previous =
        [
            new() { ExecutableName = "notepad.exe", IsSelected = true },
            new() { ExecutableName = "calc.exe", IsSelected = true }
        ];
        BlockedApp[] fresh = [new() { ExecutableName = "notepad.exe", IsSelected = false }];

        SelectionCarry.Apply(previous, fresh, a => a.ExecutableName, StringComparer.OrdinalIgnoreCase);

        Assert.Single(fresh);
        Assert.True(fresh[0].IsSelected);
    }

    // ── All three really call it, before rebuilding ─────────────────────────

    private static string ViewModelSource(string name)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(StoreApp).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "SysManager", "ViewModels", name + ".cs"));
    }

    /// <summary>The text of a call's own argument list, so a key can be asserted without matching the file.</summary>
    private static string ArgumentsOf(string source, string call)
    {
        var open = source.IndexOf(call, StringComparison.Ordinal);
        if (open < 0) return "";
        var start = open + call.Length - 1;   // the '(' itself

        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[start..(i + 1)];
        }
        return "";
    }

    [Theory]
    [InlineData("DebloaterViewModel", "Apps.ReplaceWith(apps)", "a => a.PackageFamilyName", true)]
    [InlineData("UninstallerViewModel", "AllApps.ReplaceWith(list)", "a => (a.Id, a.Name)", false)]
    [InlineData("AppBlockerViewModel", "BlockedApps.ReplaceWith(apps)", "a => a.ExecutableName", true)]
    public void TheRebuild_CallsSelectionCarry_WithTheRightKey_BeforeItReplacesTheList(
        string viewModel, string rebuild, string expectedKey, bool caseInsensitive)
    {
        // Asserting only that Apply is CALLED proved too weak: the key tests above pass their own key, so
        // changing the one production passes reddened nothing. The key and the comparer are the part that
        // differs per tab and the part that is easy to get wrong, so they are read out of the call itself.
        var source = ViewModelSource(viewModel);

        var call = source.IndexOf("SelectionCarry.Apply(", StringComparison.Ordinal);
        Assert.True(call > 0,
            $"{viewModel} does not call SelectionCarry.Apply — either a refresh no longer preserves the "
            + "user's ticks, or this guard needs re-pointing.");

        var replace = source.IndexOf(rebuild, StringComparison.Ordinal);
        Assert.True(replace > 0, $"{viewModel} no longer contains '{rebuild}' — re-point this guard.");
        Assert.True(call < replace,
            $"{viewModel} calls SelectionCarry.Apply after '{rebuild}', so the previous ticks are already gone.");

        var args = ArgumentsOf(source, "SelectionCarry.Apply(");
        Assert.False(args.Length == 0, $"could not read {viewModel}'s SelectionCarry.Apply argument list");

        Assert.Contains(expectedKey, args, StringComparison.Ordinal);

        // A case-sensitive comparer on a Windows executable name or package family name means the same row
        // reported with different capitalisation silently loses its tick.
        if (caseInsensitive)
            Assert.Contains("StringComparer.OrdinalIgnoreCase", args, StringComparison.Ordinal);
    }
}
