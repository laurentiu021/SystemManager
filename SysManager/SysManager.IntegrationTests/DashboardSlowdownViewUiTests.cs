// SysManager · DashboardSlowdownViewUiTests
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
/// The Dashboard really draws the "Why is it slow?" check (#1529): the button, the five while they are looked at, the
/// findings with their numbers and buttons, and the two ways of finding nothing.
/// </summary>
/// <remarks>
/// The unit tests prove the check finds and words the right things, and that the view model holds them. They cannot
/// prove the view shows them: a binding on the wrong path compiles and draws nothing. So this loads the real XAML on the
/// STA thread with the app's dictionaries, as <see cref="RecentChangesViewUiTests"/> does. The findings are set on the
/// view model directly rather than by running the check, so nothing on this PC is read and no lock is taken.
/// </remarks>
[Collection("Network")]
public sealed class DashboardSlowdownViewUiTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0);

    /// <summary>The Segoe Fluent Icons tick and warning sign the list marks each of the five with.</summary>
    private const string Tick = "\uE73E";
    private const string Warning = "\uE7BA";

    /// <summary>Never asked: the tests set what it found, so the button only has to be able to run.</summary>
    private sealed class UnusedSlowdown : ISlowdownService
    {
        public Task<SlowdownReport> CheckAsync(IProgress<SlowdownProbeStatus>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException("No test here runs the check.");
    }

    // A winget that answers at once, so the alerts the Dashboard checks when it loads do not wait for a real listing.
    private sealed class QuietWinget : IWingetService
    {
        public event Action<PowerShellLine>? LineReceived { add { } remove { } }

        public Task<List<AppPackage>> ListUpgradableAsync(CancellationToken ct = default) => Task.FromResult(new List<AppPackage>());

        public Task<WingetResult> UpgradeAsync(string packageId, CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");

        public Task<WingetResult> UpgradeAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("No test here upgrades anything.");
    }

    private static readonly SlowdownReport ThreeFindings = new(
        [
            new SlowdownFinding(SlowdownKind.DriveNearlyFull, "Only 4 GB is left on the C: drive", "That is 97% of the drive in use.",
                [
                    new SlowdownAction("Free up space", "nav-deep-cleanup", "Deep Cleanup", IsPrimary: true),
                    new SlowdownAction("See what is using it", "nav-disk-analyzer", "Disk Analyzer"),
                ]) { Rank = 1 },
            new SlowdownFinding(SlowdownKind.ProcessorBusy, "Google Chrome is using 61% of the processor", "Across its 14 processes, right now.",
                [new SlowdownAction("See what is running", "nav-processes", "Process Manager")]) { Rank = 2 },
            new SlowdownFinding(SlowdownKind.LongUptime, "Windows has not restarted in 23 days", "Not a problem by itself.", [])
                { Rank = 3 },
        ],
        [],
        Now);

    private static async Task<DashboardViewModel> VmAsync()
    {
        var sys = new SystemInfoService();
        var diskHealth = new DiskHealthService();
        var vm = new DashboardViewModel(
            sys,
            new TuneUpService(new ShortcutCleanerService(), diskHealth, sys),
            new HealthScoreService(sys, diskHealth, new BatteryService()),
            new TemperatureService(diskHealth, skipHardwareInit: true),
            new QuietWinget(),
            // Redirected on purpose: reading a crash marker CONSUMES it, so pointing this at the real profile would
            // delete a genuine crash report before the user saw it (#1772).
            new CrashMarkerService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-slow-crash")),
            new MemoryTestService(),
            new NavigationService(),
            new WindowsUpdateService(),
            new SpeedTestService(),
            new SpeedTestHistoryService(Path.Combine(Path.GetTempPath(), "SysManagerTests", "dash-slow-speed")),
            slowdown: new UnusedSlowdown());

        // The load fills the drives and the alerts from another thread. Waited for, so none of it changes a list the
        // view below is drawing.
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
    public async Task TheButton_IsNamedWithItsOwnWords_AndRunsTheCheck()
    {
        using var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var button = Named<Button>(Laid(vm), "Why is it slow? — look for what is slowing this PC down right now");

            Assert.Equal("Why is it slow?", button.Content);
            Assert.Same(vm.CheckSlowdownCommand, button.Command);
            Assert.Equal("btn-dashboard-why-slow", AutomationProperties.GetAutomationId(button));
        });
    }

    [Fact]
    public async Task BeforeAnyCheck_NeitherCardIsShown()
    {
        using var vm = await VmAsync();

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.DoesNotContain("Why is this PC slow?", shown);
        });
    }

    [Fact]
    public async Task WhileItLooks_TheFiveAreListed_EachMarkedWhereItStands()
    {
        using var vm = await VmAsync();
        vm.SlowdownProbes.ReplaceWith(
        [
            new SlowdownProbeStatus(SlowdownProbe.DiskSpace, Done: true, Read: true, "C: 96% full"),
            new SlowdownProbeStatus(SlowdownProbe.Processor, Done: false, Read: false, ""),
            new SlowdownProbeStatus(SlowdownProbe.Startup, Done: true, Read: false, ""),
            new SlowdownProbeStatus(SlowdownProbe.Memory, Done: true, Read: true, "91% in use"),
            new SlowdownProbeStatus(SlowdownProbe.Uptime, Done: false, Read: false, ""),
        ]);
        vm.IsSlowdownCheckRunning = true;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var list = Named<ItemsControl>(view, "What the check is looking at");

            Assert.Contains("Why is this PC slow?", ShownTexts(view));
            Assert.Equal(
                ["Disk space — C: 96% full", "What is running…", "Startup programs — could not be read",
                 "Memory — 91% in use", "Time since the last restart…"],
                ShownTexts(list));

            // A spinner only beside what is still being looked at.
            var spinners = Descendants<ProgressBar>(list).Where(Shown).Select(AutomationProperties.GetName);
            Assert.Equal(["Checking What is running", "Checking Time since the last restart"], spinners);

            // A tick beside what was read, and the warning sign beside what could not be.
            var marks = Descendants<TextBlock>(list).Where(t => Shown(t) && IsGlyph(t.Text)).Select(t => t.Text);
            Assert.Equal([Tick, Warning, Tick], marks);

            var cancel = Named<Button>(view, "Cancel the slowness check");
            Assert.True(Shown(cancel));
            Assert.Same(vm.CancelSlowdownCheckCommand, cancel.Command);
        });
    }

    [Fact]
    public async Task WhatTheCheckFound_IsDrawnWorstFirst_Numbered_WithTheButtonsOfEach()
    {
        using var vm = await VmAsync();
        vm.SlowdownReport = ThreeFindings;
        vm.HasSlowdownReport = true;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var findings = Named<ItemsControl>(view, "What is slowing this PC down, worst first");

            Assert.Equal(
                ["1", "Only 4 GB is left on the C: drive", "That is 97% of the drive in use.", "Free up space", "See what is using it",
                 "2", "Google Chrome is using 61% of the processor", "Across its 14 processes, right now.", "See what is running",
                 "3", "Windows has not restarted in 23 days", "Not a problem by itself."],
                ShownTexts(findings));
            Assert.Contains("Checked at 15:00 · 5 things looked at · worst first", ShownTexts(view));

            // Each button opens its tab through the command the other cards' links use, and only the one the card leads
            // with is the primary, purple one.
            var buttons = Descendants<Button>(findings).Where(Shown).ToList();
            Assert.Equal(
                ["Free up space — open Deep Cleanup", "See what is using it — open Disk Analyzer", "See what is running — open Process Manager"],
                buttons.Select(AutomationProperties.GetName));
            Assert.All(buttons, b => Assert.Same(vm.OpenTabCommand, b.Command));
            Assert.Equal(["nav-deep-cleanup", "nav-disk-analyzer", "nav-processes"], buttons.Select(b => (string)b.CommandParameter));
            Assert.Same(view.FindResource("PrimaryButton"), buttons[0].Style);
            Assert.All(buttons.Skip(1), b => Assert.Same(view.FindResource("SecondaryButton"), b.Style));

            Assert.DoesNotContain("Nothing obvious is slowing it down", ShownTexts(view));
            Assert.Same(vm.DismissSlowdownReportCommand, Named<Button>(view, "Dismiss what the slowness check found").Command);
        });
    }

    [Fact]
    public async Task NothingFound_WithEverythingLookedAt_SaysSo_AndOffersSystemHealth()
    {
        using var vm = await VmAsync();
        vm.SlowdownReport = new SlowdownReport([], [], Now);
        vm.HasSlowdownReport = true;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(view);

            Assert.Contains("Nothing obvious is slowing it down", shown);
            Assert.DoesNotContain("Nothing stood out in what could be checked.", shown);
            var health = Named<Button>(view, "Open System Health");
            Assert.True(Shown(health));
            Assert.Same(vm.OpenTabCommand, health.Command);
            Assert.Equal("nav-system-health", health.CommandParameter);
        });
    }

    [Fact]
    public async Task NothingFound_InWhatCouldBeLookedAt_IsNotCalledNothing_AndNamesWhatWasNot()
    {
        using var vm = await VmAsync();
        vm.SlowdownReport = new SlowdownReport([], [SlowdownProbe.DiskSpace], Now);
        vm.HasSlowdownReport = true;

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var shown = ShownTexts(view);

            Assert.DoesNotContain("Nothing obvious is slowing it down", shown);
            Assert.Contains("Nothing stood out in what could be checked.", shown);
            Assert.Contains("Not checked this time: disk space. The result above does not cover it.", shown);
            Assert.Contains("Checked at 15:00 · 4 of 5 things looked at", shown);
            Assert.False(Shown(Named<Button>(view, "Open System Health")));
        });
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == name);

    /// <summary>
    /// Whether nothing between <paramref name="element"/> and the view is collapsed. <c>IsVisible</c> cannot say: it is
    /// false for anything not shown in a window, and these views are laid out without one.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order, icon glyphs left out.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && !IsGlyph(block.Text)) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';

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
