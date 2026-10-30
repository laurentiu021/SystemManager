// SysManager · BandwidthMonitorViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Bandwidth Monitor shows its per-app Download, Upload and Session total columns only while precise mode runs
/// (#2705).
/// </summary>
/// <remarks>
/// Those figures come from the administrator-only kernel trace, and by connection they are zero. The columns hid
/// through a <c>RelativeSource</c> binding, which never attaches on a <c>DataGridColumn</c> because a column is not in
/// the visual tree, so they stayed visible and every app read 0 bps down, 0 bps up and 0 B in the mode the tab starts
/// in. This loads the real XAML on the STA thread with the app's dictionaries, the way
/// <see cref="AudioMixerViewUiTests"/> does, over a source that touches no network.
/// </remarks>
public sealed class BandwidthMonitorViewUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    /// <summary>A source that answers with no traffic and touches no network.</summary>
    private sealed class QuietSource : IBandwidthMonitorService
    {
        public BandwidthMode Mode => BandwidthMode.Connections;
        public bool IsAvailable => true;
        public bool Start() => true;
        public Task<BandwidthSnapshot> SampleAsync(CancellationToken ct = default) =>
            Task.FromResult(new BandwidthSnapshot(BandwidthMode.Connections, 0, 0, []));
        public void Dispose() { }
    }

    /// <summary>Built off the STA thread; the tab is never activated, so the poll loop stays parked.</summary>
    private async Task<BandwidthMonitorViewModel> VmAsync()
    {
        var vm = new BandwidthMonitorViewModel(
            new BandwidthHistoryService(_dir), () => new QuietSource(), etwSourceFactory: null);
        await vm.InitializationComplete;
        return vm;
    }

    private static BandwidthMonitorView Laid(BandwidthMonitorViewModel vm)
    {
        AppResources.Ensure();
        var view = new BandwidthMonitorView { DataContext = vm };
        view.Measure(new Size(1200, 1000));
        view.Arrange(new Rect(0, 0, 1200, 1000));
        view.UpdateLayout();
        return view;
    }

    private static readonly string[] PreciseColumns = ["Download", "Upload", "Session total"];

    [Fact]
    public async Task ThePreciseColumns_ShowOnlyWhilePreciseModeRuns()
    {
        using var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var grid = Named<DataGrid>(view, "Network usage by app");
            Assert.False(vm.PreciseMode);

            Assert.Equal([Visibility.Visible, Visibility.Visible], [Column(grid, "App").Visibility,
                Column(grid, "Connections").Visibility]);
            Assert.All(PreciseColumns, header => Assert.Equal(Visibility.Collapsed, Column(grid, header).Visibility));

            vm.PreciseMode = true;
            view.UpdateLayout();
            Assert.All(PreciseColumns, header => Assert.Equal(Visibility.Visible, Column(grid, header).Visibility));

            vm.PreciseMode = false;
            view.UpdateLayout();
            Assert.All(PreciseColumns, header => Assert.Equal(Visibility.Collapsed, Column(grid, header).Visibility));
        });
    }

    /// <summary>
    /// The "Alert over" box keeps what is typed in it, decimal point included, while the threshold follows (#2706).
    /// </summary>
    /// <remarks>
    /// It bound the number and wrote it back on every key, so typing "2." showed "2" and the next key made it 25.
    /// Typed through the box's own binding, on the thread that owns it.
    /// </remarks>
    [Fact]
    public async Task TheAlertBox_KeepsWhatIsTyped_AndTheThresholdFollows()
    {
        using var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var box = Named<TextBox>(view, "Alert threshold in megabits per second");

            box.Text = "2.";
            Assert.Equal("2.", box.Text);
            Assert.Equal(2.0, vm.AlertThresholdMbps);

            box.Text = "2.5";
            Assert.Equal("2.5", box.Text);
            Assert.Equal(2.5, vm.AlertThresholdMbps);

            box.Text = "";
            Assert.Equal("", box.Text);
            Assert.Equal(0.0, vm.AlertThresholdMbps);
            Assert.False(Validation.GetHasError(box));
        });
    }

    private static DataGridColumn Column(DataGrid grid, string header) =>
        Assert.Single(grid.Columns, column => Equals(column.Header, header));

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
