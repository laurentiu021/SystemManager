// SysManager · BulkInstallerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BulkInstallerViewModel"/>. Verifies curated app list,
/// filtering, selection commands, and category logic.
/// </summary>
public class BulkInstallerViewModelTests
{
    // AppIconService gets a temp configDir. With the default it resolves the user's real
    // %LocalAppData%\SysManager — these tests only construct it, but passing the seam keeps the rule
    // uniform: the service is never built against a real profile in a test.
    private static BulkInstallerViewModel NewVm() =>
        new(new BulkInstallerService(new PowerShellRunner()),
            new AppIconService(null, Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Constructor_PopulatesAppsWithCuratedList()
    {
        var vm = NewVm();
        Assert.Equal(46, vm.Apps.Count);
    }

    [Fact]
    public void Constructor_FilteredAppsMatchesAllApps()
    {
        var vm = NewVm();
        Assert.Equal(vm.Apps.Count, vm.FilteredApps.Count);
    }

    [Fact]
    public void SelectAll_SelectsAllFilteredApps()
    {
        var vm = NewVm();
        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.FilteredApps, app => Assert.True(app.IsSelected));
    }

    [Fact]
    public void DeselectAll_DeselectsAllApps()
    {
        var vm = NewVm();
        // First select all, then deselect
        vm.SelectAllCommand.Execute(null);
        vm.DeselectAllCommand.Execute(null);
        Assert.All(vm.Apps, app => Assert.False(app.IsSelected));
    }

    [Theory]
    [InlineData("Browsers", 4)]
    [InlineData("Communication", 6)]
    [InlineData("Media", 3)]
    [InlineData("Development", 4)]
    [InlineData("Utilities", 8)]
    [InlineData("Gaming", 3)]
    [InlineData("Security", 2)]
    [InlineData("Office & Productivity", 4)]
    [InlineData("Creativity", 4)]
    [InlineData("Networking & VPN", 4)]
    [InlineData("Runtimes & Frameworks", 4)]
    public void FilterByCategory_ShowsOnlyMatchingCategory(string category, int expectedCount)
    {
        var vm = NewVm();
        vm.SelectedCategory = category;
        Assert.Equal(expectedCount, vm.FilteredApps.Count);
        Assert.All(vm.FilteredApps, app => Assert.Equal(category, app.Category));
    }

    [Theory]
    [InlineData("Chrome", 1)]
    [InlineData("fire", 1)]
    [InlineData("zzz_nonexistent", 0)]
    public void FilterByText_ShowsMatchingName(string text, int expectedCount)
    {
        var vm = NewVm();
        vm.FilterText = text;
        Assert.Equal(expectedCount, vm.FilteredApps.Count);
    }

    [Fact]
    public void CombinedFilter_CategoryAndText_Works()
    {
        var vm = NewVm();
        vm.SelectedCategory = "Development";
        vm.FilterText = "Git";
        Assert.Single(vm.FilteredApps);
        Assert.Equal("Git", vm.FilteredApps[0].Name);
    }

    [Fact]
    public void Categories_ContainsAllAndElevenSpecificPlusCustom()
    {
        var vm = NewVm();
        Assert.Contains("All", vm.Categories);
        Assert.Contains("Custom", vm.Categories);
        Assert.Equal(13, vm.Categories.Count);
    }

    // ── no-results state for the winget search ──

    /// <summary>
    /// A view-model whose winget search reaches a substituted runner, so no process is started.
    /// </summary>
    /// <remarks>
    /// <c>NewVm</c> builds a real <c>PowerShellRunner</c>, which for the search path means launching an
    /// actual <c>winget search</c> — a live process, a network round-trip, and an answer that depends on the
    /// machine. The seam is one level down from the view-model: the service collects <c>LineReceived</c>
    /// around <c>RunProcessAsync</c>, so a substituted runner that emits nothing is a search that matched
    /// nothing.
    /// </remarks>
    private static BulkInstallerViewModel VmWithSubstitutedRunner(IPowerShellRunner runner) =>
        new(new BulkInstallerService(runner),
            new AppIconService(null, Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"))));

    /// <summary>
    /// A search that matches nothing says so — and only once the search has actually run.
    /// </summary>
    /// <remarks>
    /// Before this, the results <c>ItemsControl</c> hid itself on an empty <c>Count</c> and nothing replaced
    /// it, so a query with no matches produced no results and no explanation. The flag is what keeps the
    /// message out of sight beforehand: an empty list before the first search is the starting state, not a
    /// failed search.
    /// </remarks>
    [Fact]
    public async Task SearchWinget_WhenNothingMatches_SaysSo_ButNotBeforeTheSearch()
    {
        var vm = VmWithSubstitutedRunner(Substitute.For<IPowerShellRunner>());
        Assert.False(vm.SearchFoundNothing);   // nothing searched yet

        vm.SearchQuery = "qwertyasdfzxcv";
        await vm.SearchWingetCommand.ExecuteAsync(null);

        Assert.Empty(vm.SearchResults);
        Assert.True(vm.SearchFoundNothing);
    }

    /// <summary>
    /// A query too short to search leaves the state alone rather than claiming nothing was found.
    /// </summary>
    /// <remarks>
    /// <c>SearchWingetAsync</c> returns before doing anything under two characters. Setting the flag there
    /// would tell the user their one-letter query matched nothing, when it was never sent.
    /// </remarks>
    [Fact]
    public async Task SearchWinget_WithTooShortAQuery_DoesNotClaimNothingWasFound()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = VmWithSubstitutedRunner(runner);

        vm.SearchQuery = "q";
        await vm.SearchWingetCommand.ExecuteAsync(null);

        Assert.False(vm.SearchFoundNothing);

        // Not DidNotReceiveWithAnyArgs: the constructor legitimately runs `winget list` to learn which
        // apps are already installed, so the assertion has to name the SEARCH rather than any winget call.
        await runner.DidNotReceive().RunProcessAsync(
            "winget",
            Arg.Is<string>(args => args.StartsWith("search", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// When winget itself is missing, the tab says THAT — not "no packages found".
    /// </summary>
    /// <remarks>
    /// The two answers point somewhere different: one is "try another word", the other is "winget is not on
    /// this machine". Showing the no-results state on a failure would send the user to reword a query that
    /// was never able to run.
    /// </remarks>
    [Fact]
    public async Task SearchWinget_WhenWingetIsMissing_DoesNotClaimNoPackagesWereFound()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns<Task<int>>(_ => throw new System.ComponentModel.Win32Exception(2));
        var vm = VmWithSubstitutedRunner(runner);

        vm.SearchQuery = "firefox";
        await vm.SearchWingetCommand.ExecuteAsync(null);

        Assert.False(vm.SearchFoundNothing);
        Assert.Equal(WingetFailure.WingetUnavailable, vm.StatusMessage);
    }

    // ── no-results state for the curated list ──

    /// <summary>
    /// The category dropdown offers "Custom", and no curated app is in it — so the list empties with no
    /// text typed at all. That is the state the view now explains.
    /// </summary>
    /// <remarks>
    /// Asserted because the copy tells the user to pick "All" in the category list, and that instruction is
    /// only right if a category really can empty the list. <c>Categories</c> is hardcoded while the app
    /// catalogue is not, so the two can disagree — and they do.
    /// </remarks>
    [Fact]
    public void SelectingACategoryWithNoCuratedApp_EmptiesTheList()
    {
        var vm = NewVm();
        Assert.NotEmpty(vm.FilteredApps);

        vm.SelectedCategory = "Custom";

        Assert.Empty(vm.FilteredApps);
        Assert.DoesNotContain(vm.Apps, a => a.Category == "Custom");
    }

    // ── re-entrancy guard (regression: shared CTS disposed mid-install) ──

    [Fact]
    public void InstallSelectedCommand_DisabledWhileBusy()
    {
        var vm = NewVm();
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));   // idle → clickable

        vm.IsBusy = true;
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));  // running → blocked (no re-entry)

        vm.IsBusy = false;
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));   // done → clickable again
    }
}
