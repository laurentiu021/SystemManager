// SysManager · AboutViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Controls;
using SysManager.Services;
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
            AppResources.Ensure();
            var view = new AboutView();
            Assert.NotNull(view);
        });
    }

    [Fact]
    public void View_BindsToAboutViewModel()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
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
            AppResources.Ensure();
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
            AppResources.Ensure();
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
            AppResources.Ensure();
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

    /// <summary>
    /// Both of About's switches for the checks SysManager makes on its own are on the page, show what is saved, and a
    /// tick on the new one reaches the file through the view model (#2613).
    /// </summary>
    /// <remarks>
    /// The view model's tests prove the property loads and saves. They cannot prove the box is bound to it: a binding
    /// on the wrong path compiles, draws a box, and changes nothing. The startup check is not started, so nothing here
    /// asks GitHub, and the setting lives in a folder of this test's own.
    /// </remarks>
    [Fact]
    public void TheTwoSwitchesForTheChecksAtStartup_ShowWhatIsSaved_AndATickIsSaved()
    {
        const string versionBox = "Check GitHub for a new version when SysManager starts";
        const string appUpdatesBox = "Check winget for app updates when SysManager starts";
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        var prefs = new UpdateCheckPreferenceService(dir);
        prefs.SetCheckAppUpdatesOnStartup(false);

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var vm = new AboutViewModel(new UpdateService(),
                new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()),
                autoCheck: false, preferences: prefs, updatesDir: dir);
            var view = new AboutView { DataContext = vm };
            view.Measure(new Size(1200, 2400));
            view.Arrange(new Rect(0, 0, 1200, 2400));
            view.UpdateLayout();

            var boxes = Descendants<CheckBox>(view).Where(b => b.Content is versionBox or appUpdatesBox).ToList();
            Assert.Equal(2, boxes.Count);
            Assert.True(boxes.Single(b => (string)b.Content == versionBox).IsChecked);
            var appUpdates = boxes.Single(b => (string)b.Content == appUpdatesBox);
            Assert.False(appUpdates.IsChecked);

            appUpdates.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);

            Assert.True(vm.CheckAppUpdatesOnStartup);
            Assert.True(new UpdateCheckPreferenceService(dir).Load()?.CheckAppUpdatesOnStartup);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
