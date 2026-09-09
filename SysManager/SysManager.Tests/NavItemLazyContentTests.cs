// SysManager · NavItemLazyContentTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows.Shell;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pins the lazy-Content contract that lets MainWindowViewModel defer building the ~49 tab
/// view-models until their tab is first opened (the "eager-VM startup herd" fix). A NavItem must:
/// build nothing until Content is first accessed, then cache it; never force creation from
/// IsContentCreated or Dispose; and still support an eagerly-assigned instance (tests + the few
/// startup-required tabs like Dark Mode).
/// </summary>
public class NavItemLazyContentTests
{
    private sealed class CountingVm : ViewModelBase
    {
        public static int Built;
        public CountingVm() => Built++;
    }

    private static NavItem LazyItem(Func<object> factory) => new()
    {
        Id = "lazy",
        Label = "Lazy",
        ViewType = typeof(object),
        ContentFactory = factory,
    };

    [Fact]
    public void Lazy_DoesNotBuildContent_UntilFirstAccess()
    {
        int built = 0;
        var item = LazyItem(() => { built++; return new object(); });

        Assert.False(item.IsContentCreated);
        Assert.Equal(0, built);

        _ = item.Content;              // first access materialises

        Assert.True(item.IsContentCreated);
        Assert.Equal(1, built);
    }

    [Fact]
    public void Lazy_CachesContent_FactoryRunsOnce()
    {
        int built = 0;
        var item = LazyItem(() => { built++; return new object(); });

        var a = item.Content;
        var b = item.Content;

        Assert.Same(a, b);
        Assert.Equal(1, built);        // second access does NOT re-run the factory
    }

    [Fact]
    public void IsContentCreated_DoesNotForceCreation()
    {
        int built = 0;
        var item = LazyItem(() => { built++; return new object(); });

        _ = item.IsContentCreated;     // must not build
        _ = item.IsContentCreated;

        Assert.Equal(0, built);
    }

    [Fact]
    public void Dispose_OnUnopenedLazyItem_DoesNotBuildContent()
    {
        int built = 0;
        var item = LazyItem(() => { built++; return new object(); });

        item.Dispose();                // never opened → nothing to dispose, nothing to build

        Assert.False(item.IsContentCreated);
        Assert.Equal(0, built);
    }

    [Fact]
    public void Eager_ContentAvailableImmediately_WithoutFactory()
    {
        var vm = new object();
        var item = new NavItem
        {
            Id = "eager",
            Label = "Eager",
            ViewType = typeof(object),
            Content = vm,
        };

        Assert.True(item.IsContentCreated);
        Assert.Same(vm, item.Content);
    }

    [Fact]
    public void Lazy_ForwardsIsBusy_AfterMaterialisation()
    {
        var backing = new CountingVm();
        var item = LazyItem(() => backing);

        // Before open: the item is not wired to the VM, so it reports not-busy.
        Assert.False(item.IsBusy);

        _ = item.Content;              // materialise + wire IsBusy forwarding

        backing.IsBusy = true;
        Assert.True(item.IsBusy);      // now forwarded
        backing.IsBusy = false;
        Assert.False(item.IsBusy);
    }

    [Fact]
    public void Dispose_OnOpenedItem_UnsubscribesAndDisposesVm()
    {
        var backing = new CountingVm();
        var item = LazyItem(() => backing);
        _ = item.Content;              // open it

        item.Dispose();

        // After dispose, VM events no longer forward (stale IsBusy must not update).
        backing.IsBusy = true;
        Assert.False(item.IsBusy);
    }

    [Fact]
    public void Content_WithNeitherEagerNorFactory_Throws()
    {
        var item = new NavItem { Id = "bad", Label = "Bad", ViewType = typeof(object) };
        Assert.Throws<InvalidOperationException>(() => _ = item.Content);
    }
    // ---------- taskbar progress (#1584) ----------

    private sealed class ProgressVm : ViewModelBase
    {
    }

    [Fact]
    public void WireBusy_MirrorsTheViewModelsProgressImmediately()
    {
        // Not only on the next change. A tab opened while an operation is already running would otherwise
        // publish nothing to the taskbar until the next percentage tick, and an indeterminate operation
        // never ticks at all.
        var vm = new ProgressVm { Progress = 42, IsProgressIndeterminate = true, IsBusy = true };
        var item = new NavItem { Id = "t", Label = "T", ViewType = typeof(object), Content = vm }.WireBusy();

        Assert.Equal(42, item.Progress);
        Assert.True(item.IsProgressIndeterminate);
    }

    [Fact]
    public void ProgressChangesOnTheViewModel_ReachTheNavItem()
    {
        var vm = new ProgressVm();
        var item = new NavItem { Id = "t", Label = "T", ViewType = typeof(object), Content = vm }.WireBusy();

        vm.Progress = 70;
        vm.IsProgressIndeterminate = true;

        Assert.Equal(70, item.Progress);
        Assert.True(item.IsProgressIndeterminate);

        vm.IsProgressIndeterminate = false;
        Assert.False(item.IsProgressIndeterminate);
    }

    [Theory]
    [InlineData(1, 0.01)]
    [InlineData(42, 0.42)]
    [InlineData(100, 1.0)]
    public void MapTaskbarProgress_APercentageBecomesANormalBar(int progress, double expected)
    {
        var item = new NavItem { Id = "t", Label = "T", ViewType = typeof(object), Progress = progress };

        var (state, value) = MainWindowViewModel.MapTaskbarProgress(item);

        Assert.Equal(TaskbarItemProgressState.Normal, state);
        Assert.Equal(expected, value, precision: 6);
    }

    [Fact]
    public void MapTaskbarProgress_IndeterminateWinsOverAPercentage()
    {
        // A view-model setting both is mid-operation with no meaningful total. The indeterminate flag is the
        // more specific claim, and a bar parked on a stale percentage would be worse than a marquee.
        var item = new NavItem
        {
            Id = "t", Label = "T", ViewType = typeof(object),
            Progress = 42, IsProgressIndeterminate = true,
        };

        Assert.Equal(TaskbarItemProgressState.Indeterminate,
                     MainWindowViewModel.MapTaskbarProgress(item).State);
    }

    [Theory]
    [InlineData(0)]     // also what a finished operation leaves behind — an empty green bar reads as "starting"
    [InlineData(-1)]
    [InlineData(101)]
    public void MapTaskbarProgress_NothingMeaningfulShowsNothing(int progress)
    {
        var item = new NavItem { Id = "t", Label = "T", ViewType = typeof(object), Progress = progress };

        var (state, value) = MainWindowViewModel.MapTaskbarProgress(item);

        Assert.Equal(TaskbarItemProgressState.None, state);
        Assert.Equal(0, value);
    }

    [Fact]
    public void MapTaskbarProgress_WithNoSelectedTab_ShowsNothing()
        => Assert.Equal(TaskbarItemProgressState.None,
                        MainWindowViewModel.MapTaskbarProgress(null).State);

    [Fact]
    public void MapTaskbarProgress_DoesNotMaterialiseTheTabsViewModel()
    {
        // The whole lazy-startup design turns on this. Reading NavItem.Content builds the view-model, so a
        // shell that asked Content what to show on the taskbar would rebuild every tab it looked at — and
        // the mapping is called on every navigation and every progress tick.
        var built = 0;
        var item = LazyItem(() => { built++; return new ProgressVm(); });

        _ = MainWindowViewModel.MapTaskbarProgress(item);

        Assert.False(item.IsContentCreated);
        Assert.Equal(0, built);
    }

    [Fact]
    public void MainWindow_BindsTheTaskbarButtonToBothHalvesOfTheState()
    {
        // The repo's dominant defect shape: a view-model property that is computed, tested, and bound by
        // nothing. Neither half is an [ObservableProperty], so the general unreachable-property guard cannot
        // see them — only the shipped XAML can answer for this one.
        var xaml = File.ReadAllText(Path.Combine(FindAppDir(), "MainWindow.xaml"));
        var markup = System.Text.RegularExpressions.Regex.Replace(
            xaml, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Contains("<TaskbarItemInfo", markup, StringComparison.Ordinal);
        Assert.Contains("ProgressState=\"{Binding TaskbarProgressState}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ProgressValue=\"{Binding TaskbarProgressValue}\"", markup, StringComparison.Ordinal);
    }

    /// <summary>Walks up to the app project directory, the way the architecture guards do.</summary>
    private static string FindAppDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "SysManager", "SysManager");
            if (File.Exists(Path.Combine(candidate, "MainWindow.xaml"))) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("could not locate the app project from " + AppContext.BaseDirectory);
    }

}
