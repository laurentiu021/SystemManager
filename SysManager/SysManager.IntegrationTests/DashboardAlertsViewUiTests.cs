// SysManager · DashboardAlertsViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Dashboard really draws an app-update check that About kept from running at startup (#2613): the line in grey, and
/// a button that says where it goes.
/// </summary>
/// <remarks>
/// The unit tests prove the alert is built that way. They cannot prove the view draws it: the dot takes its colour from
/// a trigger per severity, so a severity without one is drawn green, and the button's name is put together from two
/// bindings, which compiles whatever their paths say. So this loads the real XAML on the STA thread with the app's
/// dictionaries, as <see cref="DashboardSlowdownViewUiTests"/> does. The setting and every record the Dashboard keeps live
/// in a folder of this test's own.
/// </remarks>
[Collection("Network")]
public sealed class DashboardAlertsViewUiTests
{
    // Never asked, since the check at startup is switched off; answering at once keeps the load from waiting on a real one.
    private sealed class QuietWinget : IWingetService
    {
        public event Action<PowerShellLine>? LineReceived { add { } remove { } }

        public Task<List<AppPackage>> ListUpgradableAsync(CancellationToken ct = default) => Task.FromResult(new List<AppPackage>());

        public Task<WingetResult> UpgradeAsync(string packageId, CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");

        public Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");
    }

    private static async Task<DashboardViewModel> VmWithTheAppUpdateCheckOffAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        var updateCheck = new UpdateCheckPreferenceService(dir);
        updateCheck.SetCheckAppUpdatesOnStartup(false);

        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        var vm = new DashboardViewModel(
            sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys, new ShellRecycleBin()),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            new QuietWinget(),
            // In the test's folder: reading a crash marker CONSUMES it (#1772).
            new CrashMarkerService(Path.Combine(dir, "crash")),
            new MemoryTestService(),
            new NavigationService(),
            new WindowsUpdateService(),
            new SpeedTestService(new PowerShellRunner()),
            new SpeedTestHistoryService(Path.Combine(dir, "speed")),
            updateCheck);

        // The load builds the alerts. Waited for, so the list is there before the view below draws it.
        await vm.InitializationComplete;
        return vm;
    }

    private static DashboardView Laid(DashboardViewModel vm)
    {
        AppResources.Ensure();
        var view = new DashboardView { DataContext = vm };
        view.Measure(new Size(1400, 4000));
        view.Arrange(new Rect(0, 0, 1400, 4000));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task AnAppUpdateCheckSwitchedOff_IsGrey_AndItsButtonSaysWhereItGoes()
    {
        using var vm = await VmWithTheAppUpdateCheckOffAsync();

        StaHelper.Run(() =>
        {
            var button = Named<Button>(Laid(vm), "Open App Updates: App updates not checked at startup");

            Assert.Equal("Open App Updates", button.Content);
            Assert.Same(vm.OpenAlertCommand, button.Command);
            Assert.Equal("nav-app-updates", Assert.IsType<DashboardAlert>(button.CommandParameter).NavTargetId);
            Assert.Equal(Visibility.Visible, button.Visibility);

            // The dot on the alert's own row, in the colour of nothing to judge rather than the green of "all good".
            var row = (DependencyObject)VisualTreeHelper.GetParent(button);
            var dot = Assert.Single(Descendants<System.Windows.Shapes.Ellipse>(row));
            Assert.Same(Application.Current.Resources["TextMuted"], dot.Fill);
        });
    }

    /// <summary>
    /// A long alert title wraps onto a second line at the smallest window, where it was cut mid-word (#2787).
    /// </summary>
    /// <remarks>
    /// The title sat in a horizontal StackPanel beside its dot, which gave it unlimited width, so its ellipsis never
    /// ran and the card's edge cut it: about 160 px of title fit at the smallest window, and "3 memory hardware errors
    /// in 30 days — test your RAM" lost the instruction. The cut falls on the panel around the title, not the title
    /// itself, so this reads the title and everything it sits in.
    /// </remarks>
    [Fact]
    public async Task ALongTitle_WrapsAtTheSmallestWindow_InsteadOfBeingCut()
    {
        const string title = "3 memory hardware errors in 30 days — test your RAM";
        using var vm = await VmWithTheAppUpdateCheckOffAsync();

        StaHelper.Run(() =>
        {
            vm.Alerts.Add(new DashboardAlert
            {
                Title = title,
                Severity = AlertSeverity.Red,
                State = AlertLoadingState.Complete,
            });
            AppResources.Ensure();
            var view = new DashboardView { DataContext = vm };
            view.Measure(new Size(PageWidth.Smallest, 4000));
            view.Arrange(new Rect(0, 0, PageWidth.Smallest, 4000));
            view.UpdateLayout();

            var block = Assert.Single(Descendants<TextBlock>(view), t => t.Text == title);
            for (DependencyObject? at = block; at is not null && at != view; at = VisualTreeHelper.GetParent(at))
            {
                if (at is FrameworkElement element && LayoutClip.IsCutAcross(element, out var room))
                    Assert.Fail($"{LayoutClip.Describe(element)} is {element.ActualWidth:F0} px wide and shown "
                                + $"{LayoutClip.Shown(room)}, so the alert's title is cut.");
            }
            Assert.True(block.ActualHeight > 2 * block.FontSize,
                $"the title is {block.ActualHeight:F0} px tall, one line, so this window did not need it to wrap.");
        });
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
