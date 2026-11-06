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

    /// <summary>
    /// A switch whose choice could not be saved says so on screen, under the two switches (#2833).
    /// </summary>
    /// <remarks>
    /// About shows no shared status line, so a sentence set anywhere else would be read by nobody. A folder where the
    /// file goes makes the write fail.
    /// </remarks>
    [Fact]
    public void AStartupChoiceThatCannotBeSaved_SaysSoUnderTheSwitches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        var prefs = new UpdateCheckPreferenceService(dir);
        Directory.CreateDirectory(Path.Combine(dir, UpdateCheckPreferenceService.FileName));

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
            Assert.DoesNotContain(Descendants<TextBlock>(view),
                t => t.Text == ViewModelBase.ChangeNotSavedStatus && t.Visibility == Visibility.Visible);

            vm.CheckForUpdatesOnStartup = !vm.CheckForUpdatesOnStartup;
            view.UpdateLayout();

            Assert.Contains(Descendants<TextBlock>(view),
                t => t.Text == ViewModelBase.ChangeNotSavedStatus && t.Visibility == Visibility.Visible);
        });
    }

    /// <summary>
    /// Nothing in About's version card is cut, at the default window or the smallest: the name, version and tagline
    /// keep the width they need, and every button is inside the card (#2735).
    /// </summary>
    /// <remarks>
    /// The buttons sat in a column beside the name that kept its full width. With nine of them that column was wider
    /// than the card, so the name's column got none, and View license and What's new ran off the card's edge. A cut
    /// element is still laid out at its full width and then clipped by its parent, so its own size cannot tell; the
    /// layout clip WPF gives it can, and this reads that for every element in the card. 1044 is the page's width
    /// beside the sidebar in the window's default 1280 px, and 724 in its smallest, 960.
    /// </remarks>
    [Theory]
    [InlineData(1044)]
    [InlineData(724)]
    public void TheVersionCard_CutsNothing(int pageWidth)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            using var vm = new AboutViewModel(new UpdateService(),
                new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(),
                    new EventLogService()),
                autoCheck: false, preferences: new UpdateCheckPreferenceService(dir), updatesDir: dir);
            var view = new AboutView { DataContext = vm };
            view.Measure(new Size(pageWidth, 900));
            view.Arrange(new Rect(0, 0, pageWidth, 900));
            view.UpdateLayout();

            var cardStyle = (Style)view.FindResource("Card");
            var check = Descendants<Button>(view).Single(b => Equals(b.Content, "Check for updates"));
            DependencyObject card = check;
            while (card is not Border { Style: var style } || !ReferenceEquals(style, cardStyle))
                card = System.Windows.Media.VisualTreeHelper.GetParent(card);

            var buttons = LaidOut(card).OfType<Button>().Count();
            Assert.True(buttons >= 9,
                $"{buttons} buttons were laid out in the version card, where nine are always shown.");
            Assert.Contains(Descendants<TextBlock>(card), t => t.Text == "SysManager");

            var cut = LaidOut(card)
                .Select(e => (Element: e, Room: System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(e)))
                .Where(c => c.Room is not null && Cut(c.Element, c.Room.Bounds))
                .Select(c => $"{Describe(c.Element)} is {c.Element.ActualWidth:F0} x {c.Element.ActualHeight:F0} "
                             + $"and shown {Shown(c.Room!.Bounds)}")
                .ToList();
            Assert.True(cut.Count == 0,
                $"at a page {pageWidth} px wide, these are cut in About's version card:\n  " + string.Join("\n  ", cut));
        });

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The elements under <paramref name="root"/> that are laid out: a collapsed one, and everything inside it, keeps
    /// the sizes of the last pass that showed it, so it is left out with its subtree.
    /// </summary>
    private static IEnumerable<FrameworkElement> LaidOut(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is FrameworkElement element) yield return element;
            foreach (var deeper in LaidOut(child)) yield return deeper;
        }
    }

    /// <summary>Whether the room its parent gives an element is smaller than the element, beyond rounding.</summary>
    private static bool Cut(FrameworkElement element, Rect room) =>
        room.IsEmpty
            ? element.ActualWidth > 1 || element.ActualHeight > 1
            : room.Width + 1 < element.ActualWidth || room.Height + 1 < element.ActualHeight;

    private static string Shown(Rect room) => room.IsEmpty ? "not at all" : $"{room.Width:F0} x {room.Height:F0}";

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"the text \"{text.Text}\"",
        ContentControl { Content: string content } control => $"the {control.GetType().Name} \"{content}\"",
        _ => $"a {element.GetType().Name} holding \"{Descendants<TextBlock>(element).FirstOrDefault()?.Text}\"",
    };

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
