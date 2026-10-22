// SysManager · FilteredOutListViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text;
using System.Windows;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// A list a search or a filter emptied says nothing matches, and a list with nothing in it says how to fill it, on
/// Uninstaller, Right-click Menu, Task Scheduler and Preinstalled Apps (#2811).
/// </summary>
/// <remarks>
/// Three of the empty states were bound to the filtered list and gave the "nothing listed" advice, so a search with no
/// match was told to scan again, which keeps the search. Preinstalled Apps' was bound to the full list, so a search
/// with no match showed nothing at all. The unit tests prove the view models' flags; this loads the real XAML with the
/// real view models, as <see cref="RecentChangesViewUiTests"/> does, to prove each view shows one of its two states and
/// the right one. Nothing on this PC is read: the runners and the right-click menu service are fakes written here, and
/// each view model is settled on the test's thread before the view is built on the STA thread.
/// </remarks>
[Collection("Network")]
public sealed class FilteredOutListViewUiTests
{
    /// <summary>A runner that answers every script with <paramref name="rows"/> and runs nothing.</summary>
    private sealed class RowsRunner(params PSObject[] rows) : IPowerShellRunner
    {
        public event Action<PowerShellLine>? LineReceived { add { } remove { } }

        public event Action<int>? ProgressChanged { add { } remove { } }

        public Task<Collection<PSObject>> RunAsync(string script, IDictionary<string, object?>? parameters = null,
                                                   CancellationToken cancellationToken = default) =>
            Task.FromResult(new Collection<PSObject>(rows.ToList()));

        public Task<int> RunScriptViaPwshAsync(string script, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> RunProcessAsync(string fileName, string arguments,
                                         CancellationToken cancellationToken = default,
                                         Encoding? outputEncoding = null) => Task.FromResult(0);

        public Task<int> RunProcessWithShellAsync(string fileName, string arguments,
                                                  CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>Lists one right-click entry and changes nothing.</summary>
    private sealed class OneMenuEntry : IContextMenuService
    {
        public List<ContextMenuEntry> ScanEntries() =>
        [
            new()
            {
                Name = "Open with Contoso", Command = @"C:\Program Files\Contoso\contoso.exe",
                RegistryPath = @"HKCR\*\shell\Contoso", Location = "Files", IsEnabled = true,
            },
        ];

        public bool DisableEntry(ContextMenuEntry entry) => throw new NotSupportedException("nothing here changes");

        public bool EnableEntry(ContextMenuEntry entry) => throw new NotSupportedException("nothing here changes");

        public bool IsClassicMenuEnabled() => false;

        public bool EnableClassicMenu() => throw new NotSupportedException("nothing here changes");

        public bool DisableClassicMenu() => throw new NotSupportedException("nothing here changes");
    }

    /// <summary>The Uninstaller's leftovers, never reached: no app is removed here.</summary>
    private sealed class NoLeftovers : ILeftoverService
    {
        public Task<LeftoverGroup> FindAsync(UninstallProbe probe, IReadOnlyCollection<UninstallProbe> stillInstalled,
                                             CancellationToken ct = default) =>
            throw new NotSupportedException("nothing here is uninstalled");

        public Task<LeftoverRemoval> RemoveAsync(IReadOnlyList<LeftoverItem> items, CancellationToken ct = default) =>
            throw new NotSupportedException("nothing here is uninstalled");

        public IReadOnlyList<LeftoverGroup> LoadPending() => [];

        public void RememberPending(IReadOnlyList<LeftoverGroup> groups) { }

        public void ForgetPending(IReadOnlyCollection<string> folders) { }
    }

    /// <summary>A restore point that is never made: Preinstalled Apps changes nothing here.</summary>
    private sealed class NoRestorePoint : ISessionRestorePoint
    {
        public Task<bool> EnsureAsync(string description, CancellationToken ct = default) => Task.FromResult(false);

        public bool CreatedThisSession => false;

        public string ConfirmationNotice => "";
    }

    private static PSObject TaskRow(string name, string path, string author)
    {
        var row = new PSObject();
        row.Properties.Add(new PSNoteProperty("TaskName", name));
        row.Properties.Add(new PSNoteProperty("TaskPath", path));
        row.Properties.Add(new PSNoteProperty("State", "Ready"));
        row.Properties.Add(new PSNoteProperty("Author", author));
        row.Properties.Add(new PSNoteProperty("Description", ""));
        return row;
    }

    // ── Uninstaller ────────────────────────────────────────────────────────

    private static UninstallerViewModel Uninstaller() =>
        new(new UninstallerService(new RowsRunner(), () => false), new NoLeftovers(), () => false);

    [Fact]
    public void Uninstaller_ASearchThatMatchesNothing_SaysSo_InsteadOfAskingForAScan()
    {
        var vm = Uninstaller();
        vm.AllApps.Add(new InstalledApp { Name = "Contoso Photo", Id = "Contoso.Photo" });
        vm.FilterText = "zzzz";

        StaHelper.Run(() => AssertShowsOnly(Laid<UninstallerView>(vm),
            "No applications match your search", "No applications to show"));
    }

    [Fact]
    public void Uninstaller_WithNothingListed_AsksForAScan()
    {
        var vm = Uninstaller();
        vm.FilterText = "zzzz";

        StaHelper.Run(() => AssertShowsOnly(Laid<UninstallerView>(vm),
            "No applications to show", "No applications match your search"));
    }

    // ── Right-click Menu ───────────────────────────────────────────────────

    [Fact]
    public async Task RightClickMenu_FiltersThatHideEveryEntry_SaySo_InsteadOfAskingForAScan()
    {
        var vm = new ContextMenuViewModel(new OneMenuEntry());
        await vm.InitializationComplete;
        vm.FilterText = "zzzz";

        StaHelper.Run(() => AssertShowsOnly(Laid<ContextMenuView>(vm),
            "No entries match your filters", "No context menu entries"));
    }

    [Fact]
    public async Task RightClickMenu_WithEntriesShown_ShowsNeitherState()
    {
        var vm = new ContextMenuViewModel(new OneMenuEntry());
        await vm.InitializationComplete;

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid<ContextMenuView>(vm));
            Assert.DoesNotContain("No entries match your filters", shown);
            Assert.DoesNotContain("No context menu entries", shown);
        });
    }

    // ── Task Scheduler ─────────────────────────────────────────────────────

    [Fact]
    public async Task TaskScheduler_HidingSystemTasksWhenThatIsAllThereIs_SaysSo_InsteadOfAskingForAScan()
    {
        var vm = new TaskSchedulerViewModel(new TaskSchedulerService(
            new RowsRunner(TaskRow("Defrag", @"\Microsoft\Windows\Defrag\", "Microsoft"))));
        await vm.InitializationComplete;
        vm.HideSystemTasks = true;

        StaHelper.Run(() => AssertShowsOnly(Laid<TaskSchedulerView>(vm),
            "No tasks match your filter", "No scheduled tasks found"));
    }

    [Fact]
    public async Task TaskScheduler_WithNothingListed_SaysNoneWereFound()
    {
        var vm = new TaskSchedulerViewModel(new TaskSchedulerService(new RowsRunner()));
        await vm.InitializationComplete;

        StaHelper.Run(() => AssertShowsOnly(Laid<TaskSchedulerView>(vm),
            "No scheduled tasks found", "No tasks match your filter"));
    }

    // ── Preinstalled Apps ──────────────────────────────────────────────────

    [Fact]
    public async Task PreinstalledApps_ASearchThatMatchesNothing_SaysSo()
    {
        // The state it had is gated on the full list, so this showed bare column headers and no message at all.
        var vm = new DebloaterViewModel(new DebloaterService(new RowsRunner()), new NoRestorePoint());
        await vm.InitializationComplete;
        vm.Apps.Add(new StoreApp
        {
            Name = "Contoso.Photo",
            DisplayName = "Contoso Photo",
            PackageFullName = "Contoso.Photo_1.0.0.0_x64__8wekyb3d8bbwe",
            PackageFamilyName = "Contoso.Photo_8wekyb3d8bbwe",
            Publisher = "CN=Contoso",
            Version = "1.0.0.0",
        });
        vm.HasApps = true;
        vm.SearchText = "zzzz";

        StaHelper.Run(() => AssertShowsOnly(Laid<DebloaterView>(vm),
            "No apps match your search", "No Store apps found"));
    }

    [Fact]
    public async Task PreinstalledApps_WithNothingListed_SaysNoneWereFound()
    {
        var vm = new DebloaterViewModel(new DebloaterService(new RowsRunner()), new NoRestorePoint());
        await vm.InitializationComplete;

        StaHelper.Run(() => AssertShowsOnly(Laid<DebloaterView>(vm),
            "No Store apps found", "No apps match your search"));
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

    private static void AssertShowsOnly(FrameworkElement view, string shownTitle, string hiddenTitle)
    {
        var shown = ShownTexts(view);
        Assert.Contains(shownTitle, shown);
        Assert.DoesNotContain(hiddenTitle, shown);
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is System.Windows.Controls.TextBlock { Text.Length: > 0 } block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }
}
