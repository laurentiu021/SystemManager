// SysManager · DeepCleanupViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class DeepCleanupViewUiTests
{
    [Fact]
    public void View_Instantiates_OnStaThread()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var view = new DeepCleanupView();
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void View_BindsToDeepCleanupViewModel()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var view = new DeepCleanupView { DataContext = new DeepCleanupViewModel(new DeepCleanupService(), new LargeFileScanner(), new FixedDriveService()) };
            Assert.IsType<DeepCleanupViewModel>(view.DataContext);
        });
    }

    [Fact]
    public void MainVm_DeepCleanup_Instantiates()
    {
        var vm = new MainWindowViewModel();
        // The per-tab accessor was removed when tab VMs became lazily built (the "eager-VM
        // startup herd" fix); reach the VM through the real nav graph instead.
        var deepCleanup = vm.NavItems.First(n => n.Id == "nav-deep-cleanup").Content;
        Assert.IsType<DeepCleanupViewModel>(deepCleanup);
    }

    [Fact]
    public void MainNav_IncludesDeepCleanup()
    {
        var vm = new MainWindowViewModel();
        Assert.Contains(vm.NavItems, n => n.Id == "nav-deep-cleanup");
    }

    [Fact]
    public void MainNav_IncludesAbout()
    {
        var vm = new MainWindowViewModel();
        Assert.Contains(vm.NavItems, n => n.Id == "nav-about");
    }

    /// <summary>
    /// About closes out the Info group — the last thing in the sidebar before Advanced.
    /// </summary>
    /// <remarks>
    /// Was <c>NavItems_CorrectOrder_AboutLast</c>, asserting <c>NavItems.Last().Id == "nav-about"</c>. That
    /// stopped being true when the Advanced group was appended after Info, so the real last entry is
    /// <c>nav-env-variables</c> — and nothing said so, because CI only compile-checks this project.
    /// <para>Rewritten to the invariant that survived the change rather than deleted: About belongs at the end
    /// of Info, which is where the shell's own comment puts it ("About is eager … the sidebar version label
    /// and the tab show one shared VM"). Asserting it as the last of its group cannot rot when a group is
    /// appended, only when About itself moves — which is the thing worth catching.</para>
    /// </remarks>
    [Fact]
    public void NavItems_AboutIsLastInTheInfoGroup()
    {
        var vm = new MainWindowViewModel();
        var info = vm.NavGroups.Single(g => g.Id == "grp-info");

        Assert.Equal("nav-about", info.Children[^1].Id);
    }

    [Fact]
    public void NavItems_DeepCleanup_AfterCleanup()
    {
        var vm = new MainWindowViewModel();
        var ids = vm.NavItems.Select(n => n.Id).ToList();
        var cleanupIdx = ids.IndexOf("nav-cleanup");
        var deepIdx = ids.IndexOf("nav-deep-cleanup");
        Assert.True(deepIdx == cleanupIdx + 1, "Deep cleanup should follow Cleanup in nav");
    }

    private static void EnsureAppResources()
    {
        if (System.Windows.Application.Current == null)
        {
            try
            {
                var _ = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var uri = new Uri("pack://application:,,,/SysManager;component/App.xaml", UriKind.Absolute);
                var dict = (ResourceDictionary)Application.LoadComponent(uri);
                System.Windows.Application.Current?.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
    }
}
