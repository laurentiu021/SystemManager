using System.Windows;
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
            var view = new DeepCleanupView { DataContext = new DeepCleanupViewModel() };
            Assert.IsType<DeepCleanupViewModel>(view.DataContext);
        });
    }

    [Fact]
    public void MainVm_DeepCleanup_Instantiates()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.DeepCleanup);
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

    [Fact]
    public void NavItems_CorrectOrder_AboutLast()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("nav-about", vm.NavItems.Last().Id);
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
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
    }
}
