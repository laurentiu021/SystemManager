// SysManager · SelectionSurvivesRescanPart2Tests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// A rescan must keep the ticks the user set, in App Updates and Profile Export/Import (#2304, group A).
/// </summary>
/// <remarks>
/// Both rebuild their list with everything selected — App Updates because <see cref="AppPackage"/> defaults
/// to selected, Profile because <c>ApplySections</c> hard-coded <c>IsSelected = true</c> on every row. So a
/// refresh reversed the user's choice rather than forgetting it, and the next action then upgraded or
/// exported what they had excluded. F5 reaches both.
/// <para>App Updates is driven through its real <c>ScanCommand</c>, because <see cref="IWingetService"/>
/// exists and can be substituted — that is the strongest available proof. Profile takes a concrete service,
/// so its carry-forward is tested directly and a source guard holds the call site.</para>
/// </remarks>
public class SelectionSurvivesRescanPart2Tests
{
    // ── App Updates: end to end through the real command ───────────────────

    private static AppPackage Pkg(string id, string available = "2.0", bool selected = true) => new()
    {
        Id = id,
        Name = id.Replace('.', ' '),
        CurrentVersion = "1.0",
        AvailableVersion = available,
        IsSelected = selected
    };

    /// <summary>A winget substitute that returns a fresh list each call, as the real one does.</summary>
    private static IWingetService WingetReturning(params Func<List<AppPackage>>[] perCall)
    {
        var winget = Substitute.For<IWingetService>();
        var call = 0;
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(perCall[Math.Min(call++, perCall.Length - 1)]()));
        return winget;
    }

    [Fact]
    public async Task AppUpdates_FirstScan_SelectsEverything()
    {
        // Upgrading all is the intended default, so the first scan must arrive fully ticked. Breaking this
        // would ship a tab whose Upgrade button does nothing until every row is ticked by hand.
        var vm = new AppUpdatesViewModel(WingetReturning(() => [Pkg("Vendor.A"), Pkg("Vendor.B")]));

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Packages.Count);
        Assert.All(vm.Packages, p => Assert.True(p.IsSelected));
    }

    [Fact]
    public async Task AppUpdates_Rescan_KeepsAPackageTheUserUnticked()
    {
        // THE regression, proven through the command the user actually presses.
        var vm = new AppUpdatesViewModel(WingetReturning(() => [Pkg("Vendor.A"), Pkg("Vendor.B")]));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Packages.First(p => p.Id == "Vendor.A").IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.Packages.First(p => p.Id == "Vendor.A").IsSelected);
        Assert.True(vm.Packages.First(p => p.Id == "Vendor.B").IsSelected);
    }

    [Fact]
    public async Task AppUpdates_Rescan_KeepsAnUntickWhenANewerVersionIsOffered()
    {
        // Keyed on the package id, not the version: "do not upgrade this app" does not stop being true
        // because the version on offer moved on.
        var vm = new AppUpdatesViewModel(WingetReturning(
            () => [Pkg("Vendor.A", available: "2.0")],
            () => [Pkg("Vendor.A", available: "3.0")]));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Packages[0].IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal("3.0", vm.Packages[0].AvailableVersion);   // it really did rescan
        Assert.False(vm.Packages[0].IsSelected);
    }

    [Fact]
    public async Task AppUpdates_Rescan_KeepsEverythingUnticked_WhenTheUserUntickedEverything()
    {
        var vm = new AppUpdatesViewModel(WingetReturning(() => [Pkg("Vendor.A"), Pkg("Vendor.B")]));
        await vm.ScanCommand.ExecuteAsync(null);

        foreach (var p in vm.Packages) p.IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Packages.Count);
        Assert.DoesNotContain(vm.Packages, p => p.IsSelected);
    }

    [Fact]
    public async Task AppUpdates_APackageThatAppearsLater_IsSelected()
    {
        // The user never saw it, so it takes the default — which for this tab means ticked.
        var vm = new AppUpdatesViewModel(WingetReturning(
            () => [Pkg("Vendor.A")],
            () => [Pkg("Vendor.A"), Pkg("Vendor.New")]));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Packages[0].IsSelected = false;

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.False(vm.Packages.First(p => p.Id == "Vendor.A").IsSelected);
        Assert.True(vm.Packages.First(p => p.Id == "Vendor.New").IsSelected);
    }

    // ── Profile Export/Import ──────────────────────────────────────────────

    private static SelectableSection Section(string key, bool selected = true) =>
        new(new ConfigSection(key, key + " settings", key + ".json", "{}")) { IsSelected = selected };

    [Fact]
    public void Profile_FirstPopulation_SelectsEverySection()
    {
        SelectableSection[] fresh = [Section("theme"), Section("privacy")];

        ProfileViewModel.CarryForwardSelection([], fresh);

        Assert.All(fresh, s => Assert.True(s.IsSelected));
    }

    [Fact]
    public void Profile_Refresh_KeepsASectionTheUserUnticked()
    {
        // The ticks decide what an export writes and what an import applies, so an untick coming back means
        // carrying over settings the user deliberately left behind.
        SelectableSection[] previous = [Section("theme", selected: false), Section("privacy", selected: true)];
        SelectableSection[] fresh = [Section("theme"), Section("privacy")];

        ProfileViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Profile_Refresh_KeepsEverythingUnticked_WhenTheUserUntickedEverything()
    {
        SelectableSection[] previous = [Section("theme", selected: false), Section("privacy", selected: false)];
        SelectableSection[] fresh = [Section("theme"), Section("privacy")];

        ProfileViewModel.CarryForwardSelection(previous, fresh);

        Assert.DoesNotContain(fresh, s => s.IsSelected);
    }

    [Fact]
    public void Profile_ASectionThatAppearsLater_TakesTheDefault()
    {
        SelectableSection[] previous = [Section("theme", selected: false)];
        SelectableSection[] fresh = [Section("theme"), Section("newtab")];

        ProfileViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Profile_MatchesOnTheKey_NotTheDisplayName()
    {
        // A display name is presentation text and can be reworded; the key is what the service builds the
        // section from. Matching on the name would silently re-tick a renamed section.
        SelectableSection[] previous =
        [
            new(new ConfigSection("theme", "Appearance", "theme.json", "{}")) { IsSelected = false }
        ];
        SelectableSection[] fresh =
        [
            new(new ConfigSection("theme", "Theme and colours", "theme.json", "{}"))
        ];

        ProfileViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void ProfileApplySections_CallsCarryForward_BeforeItRebuildsTheList()
    {
        // ProfileService is concrete, so the call site is asserted in the source. Order matters as much as
        // the call: after ReplaceWith the previous ticks are already gone.
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ConfigSection).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "SysManager", "ViewModels", "ProfileViewModel.cs"));

        var call = source.IndexOf("CarryForwardSelection(Sections,", StringComparison.Ordinal);
        Assert.True(call > 0,
            "ProfileViewModel does not call CarryForwardSelection(Sections, …) — either a refresh no longer "
            + "preserves the user's ticks, or this guard needs re-pointing.");

        var replace = source.IndexOf("Sections.ReplaceWith(", StringComparison.Ordinal);
        Assert.True(replace > 0, "ProfileViewModel no longer calls Sections.ReplaceWith — re-point this guard.");
        Assert.True(call < replace,
            "CarryForwardSelection runs after Sections.ReplaceWith, so the previous ticks are already gone.");

        // And the hard-coded re-tick must not come back.
        Assert.DoesNotContain("new SelectableSection(s) { IsSelected = true }", source, StringComparison.Ordinal);
    }
}
