// SysManager · AboutViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Controls;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

[Collection("Network")]
public class AboutViewUiTests
{
    /// <summary>
    /// A throwaway config directory. AboutViewModel persists its startup-check preference under
    /// %AppData%\SysManager, and the convenience constructors used to resolve that unconditionally,
    /// so building one here rewrote the developer's real preference file (#1785).
    /// </summary>
    private static string AboutConfigDir()
        => Path.Combine(Path.GetTempPath(), "SysManagerTests", "about-ui");

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
            var view = new AboutView { DataContext = new AboutViewModel(AboutConfigDir()) };
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
            // Reach the About VM through the real nav graph — the per-tab accessor was removed
            // when tab VMs became lazily built (the "eager-VM startup herd" fix).
            var aboutVm = mainVm.NavItems.First(n => n.Id == "nav-about").Content;
            var view = new AboutView { DataContext = aboutVm };
            Assert.NotNull(view.DataContext);
            Assert.IsType<AboutViewModel>(view.DataContext);
        });
    }

    [Fact]
    public void View_UsesCurrentVersion_FromVm()
    {
        StaHelper.Run(() =>
        {
            EnsureAppResources();
            var vm = new AboutViewModel(AboutConfigDir());
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
            var vm = new AboutViewModel(AboutConfigDir());
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
                System.Windows.Application.Current?.Resources.MergedDictionaries.Add(dict);
            }
            catch
            {
                // App may already exist on this STA thread — best effort.
            }
        }
    }
}
