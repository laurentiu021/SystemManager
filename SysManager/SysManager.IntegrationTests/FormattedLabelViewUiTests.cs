// SysManager · FormattedLabelViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Chips, buttons, menu items and tooltips whose words are built around a bound value show the words, not only the
/// value (#2808).
/// </summary>
/// <remarks>
/// They were bindings with a <c>StringFormat</c> on <c>Content</c>, <c>Header</c> or <c>ToolTip</c>, which are typed
/// <c>object</c>, and WPF formats only for a string: Services' chips read "12" and "40", the row menus read only the
/// service's or the program's name, "Kill" included, and Settings Watchdog's reason column had no tooltip at all. The
/// unit tests and <c>ArchitectureTests.EveryStringFormat_IsOnAPropertyThatHoldsAString</c> prove the parts; this loads
/// the real XAML with the real view models to prove what each control shows. A row menu is laid out without being
/// opened, which is when its bindings find it, and the name UI Automation reads is checked with the words, since a
/// menu item is announced by its header. Nothing on this PC is read or changed: every service here is a fake.
/// </remarks>
[Collection("Network")]
public sealed class FormattedLabelViewUiTests
{
    /// <summary>A runner that answers every script with nothing and runs nothing.</summary>
    private sealed class NothingRunner : IPowerShellRunner
    {
        public event Action<PowerShellLine>? LineReceived { add { } remove { } }

        public event Action<int>? ProgressChanged { add { } remove { } }

        public Task<Collection<PSObject>> RunAsync(string script, IDictionary<string, object?>? parameters = null,
                                                   CancellationToken cancellationToken = default) =>
            Task.FromResult(new Collection<PSObject>());

        public Task<int> RunScriptViaPwshAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> RunProcessAsync(string fileName, string arguments,
                                         CancellationToken cancellationToken = default,
                                         Encoding? outputEncoding = null) => Task.FromResult(0);

        public Task<int> RunProcessWithShellAsync(string fileName, string arguments,
                                                  CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>Watches one setting, reads it as 0, has no baseline, and changes nothing.</summary>
    private sealed class OneWatchedSetting : ISettingsWatchdogService
    {
        public IReadOnlyList<WatchedSetting> Catalog { get; } =
        [
            new("telemetry", "Diagnostic data (telemetry)",
                "Windows Update can raise the telemetry level back to Full.",
                "Privacy", @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry",
                new Dictionary<int, string> { [0] = "Off (Security)" }),
        ];

        public IReadOnlyDictionary<string, int?> ReadCurrent() => new Dictionary<string, int?> { ["telemetry"] = 0 };

        public IReadOnlyDictionary<string, int?> SaveBaseline(DateTime takenAt) =>
            throw new NotSupportedException("nothing here is saved");

        public BaselineSnapshot? LoadBaseline() => null;

        public bool BaselineFileExists => false;

        public IReadOnlyList<SettingDrift> DetectDrift() => [];

        public IReadOnlyList<SettingDrift> DetectDrift(BaselineSnapshot baseline,
                                                       IReadOnlyDictionary<string, int?> current) => [];

        public bool Restore(SettingDrift drift) => throw new NotSupportedException("nothing here is restored");

        public DriftSightings RecordDrift(IReadOnlyList<SettingDrift> drifts, DateTime now) =>
            throw new NotSupportedException("there is no baseline to drift from");
    }

    /// <summary>
    /// What Services' toolbar binds, as plain values. The view model applies a read through the Application's
    /// dispatcher, which this suite's STA thread does not run, so its load would never finish here; the counts and the
    /// button's words are proved on the view model by the unit tests.
    /// </summary>
    private sealed class ServicesToolbar
    {
        public int SafeCount => 1;
        public int CautionCount => 2;
        public int CriticalCount => 3;
        public int NotRatedCount => 4;
        public int RunningCount => 5;
        public int StoppedCount => 6;
        public int SafeToDisableCount => 7;
        public int KeepEnabledCount => 8;
        public int AdvancedCount => 9;
        public int HighlightedCount => 1;
        public string ClearMarksText => "Clear 1 mark";
    }

    // ── Services ───────────────────────────────────────────────────────────

    [Fact]
    public void Services_FilterChips_SayWhatTheyFilter_BesideTheirCounts()
    {
        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid<ServicesView>(new ServicesToolbar()));

            Assert.Contains("Safe (1)", shown);
            Assert.Contains("Caution (2)", shown);
            Assert.Contains("Critical (3)", shown);
            Assert.Contains("Not rated (4)", shown);
            Assert.Contains("Running (5)", shown);
            Assert.Contains("Stopped (6)", shown);
            Assert.Contains("Safe to disable (7)", shown);
            Assert.Contains("Keep enabled (8)", shown);
            Assert.Contains("Advanced (9)", shown);
            Assert.Contains("Clear 1 mark", shown);
        });
    }

    [Fact]
    public void Services_RowMenu_NamesTheActionAndTheService()
    {
        var spooler = new ServiceEntry { Name = "Spooler", DisplayName = "Print Spooler" };

        StaHelper.Run(() => AssertMenuReads(RowMenu<ServicesView>(spooler),
            "Start Print Spooler", "Stop Print Spooler", "Disable Print Spooler", "Enable Print Spooler",
            "Mark or unmark this service: Print Spooler"));
    }

    // ── Process Manager ────────────────────────────────────────────────────

    [Fact]
    public void ProcessManager_RowMenu_NamesTheActionAndTheProgram()
    {
        var process = new ProcessEntry { Name = "notepad.exe" };

        StaHelper.Run(() => AssertMenuReads(RowMenu<ProcessManagerView>(process),
            "Open file location for notepad.exe", "Kill notepad.exe"));
    }

    // ── Disk Analyzer ──────────────────────────────────────────────────────

    [Fact]
    public void DiskAnalyzer_FolderBlockMenu_NamesTheFolder()
    {
        var tile = new TreemapTile(null, 0, 0, 200, 100, "AppData", "38.0 GB · 42%", ShowName: true, ShowDetail: true,
                                   "#7C3AED", "#FFFFFF", "AppData · 38.0 GB", "AppData, 38.0 GB, 42 percent",
                                   IsPartlyUnreadable: false);

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var block = (Style)new DiskAnalyzerView().Resources["MapTile"];
            AssertMenuReads(Opened(MenuOf(block), tile), "Show AppData in Explorer");
        });
    }

    // ── Bulk Installer ─────────────────────────────────────────────────────

    [Fact]
    public async Task BulkInstaller_SelectCategoryButton_NamesTheCategory()
    {
        var icons = Directory.CreateTempSubdirectory("FormattedLabels_Icons_");
        try
        {
            var vm = new BulkInstallerViewModel(new BulkInstallerService(new NothingRunner()),
                                                new AppIconService(null, icons.FullName));
            await vm.InitializationComplete;
            vm.SelectedCategory = vm.Categories.First(c => c != "All");

            StaHelper.Run(() =>
                Assert.Contains($"Select {vm.SelectedCategory}", ShownTexts(Laid<BulkInstallerView>(vm))));
        }
        finally
        {
            icons.Delete(recursive: true);
        }
    }

    // ── System Logs ────────────────────────────────────────────────────────

    [Fact]
    public void Logs_ClearMarksButton_AndLogFolderTooltip_ReadInFull()
    {
        // The tooltip's template held a doubled quote, which ended it at "SysManager" as well.
        var vm = new LogsViewModel(new EventLogService()) { LogFolder = @"D:\logs", HighlightedCount = 2 };

        StaHelper.Run(() =>
        {
            var view = Laid<LogsView>(vm);
            var folder = Logical(view).OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "btn-logs-open-folder");

            Assert.Contains("Clear 2 marks", ShownTexts(view));
            Assert.Equal(@"Opens SysManager's own diagnostic logs: D:\logs", folder.ToolTip);
        });
    }

    // ── Settings Watchdog ──────────────────────────────────────────────────

    [Fact]
    public async Task SettingsWatchdog_ReasonCell_HasTheWholeReasonAndTheRegistryLocationAsItsTooltip()
    {
        var vm = new SettingsWatchdogViewModel(new OneWatchedSetting());
        await vm.InitializationComplete;

        StaHelper.Run(() =>
        {
            var reason = Descendants(Laid<SettingsWatchdogView>(vm)).OfType<TextBlock>()
                .Single(t => t.Text == "Windows Update can raise the telemetry level back to Full.");

            Assert.Equal("Windows Update can raise the telemetry level back to Full.\n"
                         + @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry", reason.ToolTip);
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>The view built after the app's dictionaries are loaded, which its styles come from.</summary>
    private static TView Laid<TView>(object vm) where TView : FrameworkElement, new()
    {
        AppResources.Ensure();
        var view = new TView { DataContext = vm };
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
        return view;
    }

    /// <summary>
    /// The menu a row of <typeparamref name="TView"/>'s list opens, laid out for a row showing <paramref name="row"/>.
    /// </summary>
    private static List<MenuItem> RowMenu<TView>(object row) where TView : FrameworkElement, new()
    {
        AppResources.Ensure();
        var grid = Logical(new TView()).OfType<DataGrid>().First(g => g.RowStyle is { } style && HasMenu(style));
        return Opened(MenuOf(grid.RowStyle), row);
    }

    private static bool HasMenu(Style style) =>
        style.Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.ContextMenuProperty);

    private static ContextMenu MenuOf(Style style) =>
        (ContextMenu)style.Setters.OfType<Setter>()
            .Single(s => s.Property == FrameworkElement.ContextMenuProperty).Value;

    /// <summary>
    /// The menu's items once it is laid out for <paramref name="row"/>, as if opened on it: its items then have the
    /// menu as an ancestor, which is what their bindings look for, and the row as its placement target.
    /// </summary>
    private static List<MenuItem> Opened(ContextMenu menu, object row)
    {
        menu.PlacementTarget = new Border { DataContext = row };
        menu.Measure(new Size(800, 600));
        menu.Arrange(new Rect(0, 0, 800, 600));
        menu.UpdateLayout();
        return menu.Items.OfType<MenuItem>().ToList();
    }

    /// <summary>Each item says <paramref name="headers"/>, in order, on screen and to UI Automation.</summary>
    private static void AssertMenuReads(List<MenuItem> items, params string[] headers)
    {
        Assert.Equal(headers, items.Select(i => i.Header as string));
        Assert.Equal(headers, items.Select(i => UIElementAutomationPeer.CreatePeerForElement(i)?.GetName()));
        Assert.Equal(headers, items.Select(i => ShownTexts(i).FirstOrDefault()));
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Logical-tree walk, for controls a view has not laid out yet.</summary>
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Logical(child)) yield return deeper;
        }
    }
}
