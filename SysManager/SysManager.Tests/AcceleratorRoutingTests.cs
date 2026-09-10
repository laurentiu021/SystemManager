// SysManager · AcceleratorRoutingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="MainWindowViewModel.AcceleratorCommand"/> — which command a keypress reaches on
/// the open tab, and when it must reach nothing.
/// </summary>
/// <remarks>
/// The decision is extracted from the shell's <c>KeyDown</c> handler precisely so it can be tested: the
/// handler itself needs a <c>Window</c>, and the part worth pinning is the routing, not WPF's event
/// plumbing. Same shape as <c>MapTaskbarProgress</c> for the same reason.
/// </remarks>
public class AcceleratorRoutingTests
{
    private sealed class Tab : ViewModelBase
    {
        public int Refreshed { get; private set; }
        public int Cancelled { get; private set; }

        public IRelayCommand Refresh { get; }
        public IRelayCommand Cancel { get; }

        /// <summary>When false, <see cref="Refresh"/> reports it cannot run — the held-down-F5 case.</summary>
        public bool CanRefresh { get; set; } = true;

        public bool OffersRefresh { get; set; } = true;
        public bool OffersCancel { get; set; } = true;

        public Tab()
        {
            Refresh = new RelayCommand(() => Refreshed++, () => CanRefresh);
            Cancel = new RelayCommand(() => Cancelled++);
        }

        protected internal override IRelayCommand? RefreshOnF5 => OffersRefresh ? Refresh : null;
        protected internal override IRelayCommand? EscapeCancel => OffersCancel ? Cancel : null;
    }

    private static NavItem Built(object content) => new()
    {
        Id = "built",
        Label = "Built",
        ViewType = typeof(object),
        Content = content,   // eager path: already materialised, as the selected tab always is
    };

    private static NavItem Lazy(Func<object> factory) => new()
    {
        Id = "lazy",
        Label = "Lazy",
        ViewType = typeof(object),
        ContentFactory = factory,
    };

    [Fact]
    public void F5_ReachesTheTabsRefresh()
    {
        var tab = new Tab();

        var command = MainWindowViewModel.AcceleratorCommand(Built(tab), Key.F5);

        Assert.Same(tab.Refresh, command);
    }

    [Fact]
    public void Escape_ReachesTheTabsCancel()
    {
        var tab = new Tab();

        var command = MainWindowViewModel.AcceleratorCommand(Built(tab), Key.Escape);

        Assert.Same(tab.Cancel, command);
    }

    [Fact]
    public void ATabThatOffersNeither_SwallowsNothing()
    {
        // Null is the correct answer rather than a no-op command: the shell only marks the event handled
        // when it gets something back, so on a tab with nothing to do the key falls through to whatever
        // else would have handled it.
        var tab = new Tab { OffersRefresh = false, OffersCancel = false };

        Assert.Null(MainWindowViewModel.AcceleratorCommand(Built(tab), Key.F5));
        Assert.Null(MainWindowViewModel.AcceleratorCommand(Built(tab), Key.Escape));
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Delete)]
    [InlineData(Key.F4)]
    [InlineData(Key.Space)]
    public void AnyOtherKey_RoutesNowhere(Key key)
    {
        // Deliberately narrow. Delete is in this list because a DataGrid full of startup entries and
        // installed apps is exactly where a bare Delete key must never reach a command.
        Assert.Null(MainWindowViewModel.AcceleratorCommand(Built(new Tab()), key));
    }

    [Fact]
    public void NoTabSelected_RoutesNowhere()
        => Assert.Null(MainWindowViewModel.AcceleratorCommand(null, Key.F5));

    /// <summary>
    /// A keypress must never be what builds a tab's view model.
    /// </summary>
    /// <remarks>
    /// Reading <c>NavItem.Content</c> materialises it, so without the <c>IsContentCreated</c> check this
    /// would construct the very tab it is asking about — and several constructors start a scan or a timer.
    /// The selected tab is always built in practice; relying on that rather than asserting it is how a lazy
    /// graph gets rebuilt by accident, which is the regression
    /// <c>ArchitectureTests.OnlyTheJustifiedTabs_AreBuiltAtStartup</c> exists to prevent.
    /// </remarks>
    [Fact]
    public void AnUnopenedTab_IsNotBuiltByAKeypress()
    {
        var built = 0;
        var item = Lazy(() => { built++; return new Tab(); });

        Assert.Null(MainWindowViewModel.AcceleratorCommand(item, Key.F5));
        Assert.Null(MainWindowViewModel.AcceleratorCommand(item, Key.Escape));

        Assert.Equal(0, built);
        Assert.False(item.IsContentCreated);
    }

    [Fact]
    public void ATabWhoseContentIsNotAViewModel_RoutesNowhere()
    {
        // The nav table is typed as object, and the designer graph puts plain instances in it.
        Assert.Null(MainWindowViewModel.AcceleratorCommand(Built(new object()), Key.F5));
    }

    /// <summary>
    /// The routing hands back the command even when it cannot run; refusing is the shell's job.
    /// </summary>
    /// <remarks>
    /// Deliberate split. Keeping <c>CanExecute</c> out of here leaves this a pure question about intent —
    /// "what does F5 mean on this tab" — and keeps the "do not start a second scan" rule in the one place
    /// that actually executes. The pairing is what matters, so both halves are asserted: this test pins the
    /// command comes back, and <see cref="TheShell_RefusesAnF5ThatCannotRun"/> pins that a caller obeying
    /// the documented contract does not run it.
    /// </remarks>
    [Fact]
    public void AnF5ThatCannotRun_IsStillReturned()
    {
        var tab = new Tab { CanRefresh = false };

        var command = MainWindowViewModel.AcceleratorCommand(Built(tab), Key.F5);

        Assert.Same(tab.Refresh, command);
        Assert.False(command!.CanExecute(null));
    }

    [Fact]
    public void TheShell_RefusesAnF5ThatCannotRun()
    {
        // The handler's own two lines, reproduced: resolve, then check CanExecute for F5 only. A held-down
        // F5 during a scan that gates itself must not queue a second one.
        var tab = new Tab { CanRefresh = false };

        var command = MainWindowViewModel.AcceleratorCommand(Built(tab), Key.F5);
        if (command is not null && command.CanExecute(null)) command.Execute(null);

        Assert.Equal(0, tab.Refreshed);

        tab.CanRefresh = true;
        var again = MainWindowViewModel.AcceleratorCommand(Built(tab), Key.F5);
        if (again is not null && again.CanExecute(null)) again.Execute(null);

        Assert.Equal(1, tab.Refreshed);
    }

    [Fact]
    public void Escape_IsNotGatedOnCanExecute()
    {
        // EscapeCancel already returns null unless there is something to stop, so the flag and the command
        // travel together and a second gate would only add a way for Escape to go quiet. Pinned because
        // the F5 work above introduced a CanExecute check next door, and copying it here would be the
        // easy mistake.
        var shell = System.IO.File.ReadAllText(ShellSourcePath());

        var at = shell.IndexOf("private void Window_KeyDown", StringComparison.Ordinal);
        Assert.True(at >= 0, "Window_KeyDown not found — this test would otherwise assert nothing");
        var handler = shell[at..Math.Min(shell.Length, at + 1200)];

        Assert.Contains("Key.F5 && !command.CanExecute(null)", handler, StringComparison.Ordinal);
    }

    private static string ShellSourcePath()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "SysManager", "Services")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = System.IO.Path.Combine(dir!.FullName, "SysManager", "MainWindow.xaml.cs");
        Assert.True(System.IO.File.Exists(path), $"MainWindow.xaml.cs not found at {path}");
        return path;
    }
}
