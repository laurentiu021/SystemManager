using System.Windows;
using System.Windows.Controls;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class AboutViewUiTests
{
    [Fact]
    public void View_Instantiates_OnStaThread()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var view = new AboutView();
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void View_BindsToAboutViewModel()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var view = new AboutView { DataContext = new AboutViewModel() };
            view.ApplyTemplate();
            Assert.IsType<AboutViewModel>(view.DataContext);
        });
    }

    [Fact]
    public void View_BindsToFullMainVm_AboutProperty()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var mainVm = new MainWindowViewModel();
            var view = new AboutView { DataContext = mainVm.About };
            Assert.NotNull(view.DataContext);
        });
    }

    [Fact]
    public void View_UsesCurrentVersion_FromVm()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var vm = new AboutViewModel();
            var view = new AboutView { DataContext = vm };
            Assert.NotNull(vm.CurrentVersion);
        });
    }

    [Fact]
    public void View_CommandsResolve()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var vm = new AboutViewModel();
            Assert.NotNull(vm.CheckForUpdatesCommand);
            Assert.NotNull(vm.LoadHistoryCommand);
            Assert.NotNull(vm.InstallUpdateCommand);
            Assert.NotNull(vm.OpenRepoCommand);
            Assert.NotNull(vm.OpenLicenseCommand);
            Assert.NotNull(vm.OpenManualDownloadCommand);
            Assert.NotNull(vm.OpenDownloadFolderCommand);
            Assert.NotNull(vm.DownloadCommand);
        });
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
                // Merge App resources so styles defined at app scope are available.
                var uri = new Uri("pack://application:,,,/SysManager;component/App.xaml", UriKind.Absolute);
                var dict = (ResourceDictionary)Application.LoadComponent(uri);
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch
            {
                // App may already exist on this STA thread — best effort.
            }
        }
    }
}
