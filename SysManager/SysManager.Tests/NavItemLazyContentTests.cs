// SysManager · NavItemLazyContentTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
        Glyph = "",
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
            Glyph = "",
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
        var item = new NavItem { Id = "bad", Label = "Bad", Glyph = "", ViewType = typeof(object) };
        Assert.Throws<InvalidOperationException>(() => _ = item.Content);
    }
}
