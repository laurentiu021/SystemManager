// SysManager · ThemeSwitchColourTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Colours chosen by the data take the new theme's brushes when the theme changes (#2818).
/// </summary>
/// <remarks>
/// Console lines, verdicts, a disk's health card, Markdown code and the theme panel's own cards kept the brush the
/// theme had when they were drawn: each looked its brush up once, and the theme stores a new brush under every name
/// rather than changing the old one. A console line written on a dark preset stayed #E6E6E6 on Clean Indigo's white
/// card. A table is the exception: it builds its cells again when the theme changes, so its chips took the new colours
/// even then, and the table checks here keep them doing so. Process Manager's Status chip used fixed colours, so on a
/// light preset it was wrong from the first frame.
/// <para>Each view is laid out with the app's resources in a window that is never shown, which is what a theme switch
/// reaches, and the switch is a real <see cref="ThemeService.Apply"/> of a light preset from a service with a folder of
/// its own: Lavender, whose accent differs from the dark default's as well, so the Markdown code changes colour too.
/// Elements are found again after the switch, since a table's are new. Every brush the switch replaced is put back
/// afterwards, so no later test in this process reads Lavender. Nothing on this PC is read or changed: every row is
/// constructed.</para>
/// </remarks>
public sealed class ThemeSwitchColourTests : IDisposable
{
    private const string LightPreset = "lavender";
    private static readonly DateTime At = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Local);
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ThemeSwitch_");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>What System Fixes binds for its SFC verdict, as plain values.</summary>
    private sealed class SfcResult
    {
        public string SfcVerdict => "Windows found damaged files and could not repair some of them.";
        public string SfcVerdictColorHex => StatusColors.Bad;
    }

    /// <summary>System Health's disk cards, as plain values: its view model reads the PC's real disks.</summary>
    private sealed class DiskCards(params DiskHealthReport[] reports)
    {
        public IReadOnlyList<DiskHealthReport> DiskHealth { get; } = reports;
    }

    /// <summary>Process Manager's list, as plain values: its view model reads the PC's real processes.</summary>
    private sealed class ProcessList(params ProcessEntry[] processes)
    {
        public IReadOnlyList<ProcessEntry> FilteredProcesses { get; } = processes;
    }

    /// <summary>Services' list, as plain values: its view model reads the PC's real services.</summary>
    private sealed class ServiceList(params ServiceEntry[] services)
    {
        public IReadOnlyList<ServiceEntry> Services { get; } = services;
    }

    [Fact]
    public void ConsoleLines_TakeTheNewThemesColours()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var console = new ConsoleViewModel();
            console.Append(new PowerShellLine(OutputKind.Output, "Freed 2.1 GB of temporary files.", At));
            console.Append(new PowerShellLine(OutputKind.Error, "Access is denied.", At));

            InAWindow(new ConsoleView { DataContext = console }, view => AcrossASwitch(view,
                sample: () => Text(view, "Freed 2.1 GB of temporary files.").Foreground,
                check: () =>
                {
                    Painted(Text(view, "Freed 2.1 GB of temporary files.").Foreground, "OutOutputBrush");
                    Painted(Text(view, "Access is denied.").Foreground, "OutErrorBrush");
                }));
        });
    }

    [Fact]
    public void AVerdict_TakesTheNewThemesColour()
    {
        var result = new SfcResult();

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            InAWindow(new SystemFixesView { DataContext = result }, view => AcrossASwitch(view,
                sample: () => Text(view, result.SfcVerdict).Foreground,
                check: () => Painted(Text(view, result.SfcVerdict).Foreground, StatusColors.Bad)));
        });
    }

    [Fact]
    public void ADisksHealthCard_TakesTheNewThemesColours()
    {
        var disk = new DiskHealthReport
        {
            FriendlyName = "Samsung SSD 980",
            Verdict = "Wear is high: plan a replacement.",
            VerdictColorHex = StatusColors.Warning,
        };

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            InAWindow(new SystemHealthView { DataContext = new DiskCards(disk) }, view => AcrossASwitch(view,
                sample: () => Text(view, disk.Verdict).Foreground,
                check: () =>
                {
                    var verdict = Text(view, disk.Verdict);
                    var title = Text(view, disk.FriendlyName);
                    Painted(verdict.Foreground, StatusColors.Warning);
                    Painted(DotBeside(title).Fill, StatusColors.Warning);
                    Painted(((Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(verdict))).BorderBrush,
                            StatusColors.Warning);
                }));
        });
    }

    [Fact]
    public void ProcessManagersChips_TakeTheNewThemesColours()
    {
        // Status used fixed colours, so on a light preset it was wrong from the first frame, not only after a switch.
        var process = new ProcessEntry
        {
            Pid = 4120,
            Name = "notepad",
            Status = "Running",
            SafetyLevel = nameof(ProcessSafety.Trusted),
            Signature = SignatureTrust.Verified,
            SignatureDetail = "Signed by Microsoft Windows.",
        };

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            InAWindow(new ProcessManagerView { DataContext = new ProcessList(process) }, view =>
            {
                TextBlock Chip(string words) => Text(Descendants(view).OfType<DataGrid>().Single(), words);

                AcrossASwitch(view,
                    sample: () => Chip("Running").Foreground,
                    check: () =>
                    {
                        var status = Chip("Running");
                        Painted(status.Foreground, "SuccessText");
                        Painted(DotBeside(status).Fill, "SuccessText");
                        Painted(BarBeside(status).Background, "SuccessText");

                        var safety = Chip("Known app");
                        Painted(safety.Foreground, "SuccessText");
                        Painted(DotBeside(safety).Fill, "SuccessText");
                        Painted(ChipOf(safety).Background, "SuccessBgSubtle");
                        Painted(ChipOf(safety).BorderBrush, "SuccessBgSubtle");

                        var signature = Chip("Verified");
                        Painted(signature.Foreground, "SuccessText");
                        Painted(DotBeside(signature).Fill, "SuccessText");
                        Painted(ChipOf(signature).Background, "SuccessBgSubtle");
                    });
            });
        });
    }

    [Fact]
    public void ServicesSafetyChip_TakesTheNewThemesColour_AndKeepsItsFixedTint()
    {
        var service = new ServiceEntry
        {
            Name = "Spooler",
            DisplayName = "Print Spooler",
            SafetyLevel = SafetyLevel.Caution,
        };

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            InAWindow(new ServicesView { DataContext = new ServiceList(service) }, view =>
            {
                TextBlock Caution() => Text(Descendants(view).OfType<DataGrid>().Single(), "Caution");

                AcrossASwitch(view,
                    sample: () => Caution().Foreground,
                    check: () =>
                    {
                        Painted(Caution().Foreground, "WarningText");
                        Painted(DotBeside(Caution()).Fill, "WarningText");
                        Assert.Equal(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B), Colour(ChipOf(Caution()).Background));
                    });
            });
        });
    }

    [Fact]
    public void MarkdownCode_TakesTheNewThemesAccent()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var notes = new TextBlock();
            MarkdownTextBlock.SetMarkdown(notes, "Run `sfc /scannow` from an elevated prompt.");

            InAWindow(notes, text =>
            {
                Brush Code() => text.Inlines.OfType<Run>().Single(r => r.Text == "sfc /scannow").Foreground;

                AcrossASwitch(text, sample: Code, check: () => Painted(Code(), "Accent"));
            });
        });
    }

    [Fact]
    public void TheThemePanelsCards_TakeTheNewThemesColours()
    {
        // A switch from Follow Windows leaves the panel's cards as they were built. The panel's own theme service keeps
        // its file in this test's folder and leaves the app's brushes alone.
        var panelTheme = new ThemeService(_dir.FullName, windowsPrefersDark: () => true, paintsTheApp: false);
        panelTheme.SetPreset(ThemeService.DefaultPresetId);

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            InAWindow(new ThemePopup(panelTheme), panel =>
            {
                panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var cards = panel.PresetPanel.Children.OfType<Border>().ToList();
                var chosen = cards.Single(c => (string)c.Tag == ThemeService.DefaultPresetId);
                var other = cards.First(c => !ReferenceEquals(c, chosen));
                var name = Logical(other).OfType<TextBlock>().Single(t => t.Text.Length > 0);

                AcrossASwitch(panel,
                    sample: () => name.Foreground,
                    check: () =>
                    {
                        Painted(name.Foreground, "TextPrimary");
                        Painted(chosen.BorderBrush, "Accent");
                        Painted(other.BorderBrush, "Border1");
                    });
            });
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="check"/> with the app's brushes, applies the light preset, lays <paramref name="view"/> out
    /// again and runs it again, then puts back every brush the switch replaced.
    /// </summary>
    /// <param name="view">The view the elements are in.</param>
    /// <param name="sample">
    /// A colour the switch has to change, so the second run cannot pass on brushes that stayed.
    /// </param>
    /// <param name="check">
    /// The assertions, the same before and after: each element shows its brush as it is now.
    /// </param>
    private void AcrossASwitch(FrameworkElement view, Func<Brush> sample, Action check)
    {
        var resources = Application.Current.Resources;
        var saved = resources.Keys.Cast<object>().ToDictionary(key => key, key => resources[key]);
        check();
        var before = Colour(sample());

        try
        {
            new ThemeService(_dir.FullName, windowsPrefersDark: () => true).Apply(ThemePreset.Defaults[LightPreset]);
            Layout(view);

            check();
            Assert.NotEqual(before, Colour(sample()));
        }
        finally
        {
            foreach (var added in resources.Keys.Cast<object>().Where(key => !saved.ContainsKey(key)).ToList())
                resources.Remove(added);
            foreach (var (key, value) in saved)
                if (!ReferenceEquals(resources[key], value)) resources[key] = value;
        }
    }

    /// <summary>
    /// <paramref name="brush"/> is the brush the app's resources hold under <paramref name="key"/> now.
    /// </summary>
    private static void Painted(Brush brush, string key)
    {
        var current = Application.Current.Resources[key];
        Assert.True(ReferenceEquals(brush, current),
            $"painted {Describe(brush)}, not the theme's {key}, which is {Describe(current)}");
    }

    private static Color Colour(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static string Describe(object? brush) =>
        brush is SolidColorBrush solid ? solid.Color.ToString() : $"{brush}";

    /// <summary>
    /// Lays <paramref name="view"/> out in a window that is never shown, runs <paramref name="test"/>, and closes it.
    /// </summary>
    private static void InAWindow<TView>(TView view, Action<TView> test) where TView : FrameworkElement
    {
        var window = new Window { Content = view, ShowActivated = false };
        try
        {
            Layout(view);
            test(view);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1000));
        view.Arrange(new Rect(0, 0, 1400, 1000));
        view.UpdateLayout();
    }

    private static TextBlock Text(DependencyObject root, string text) =>
        Assert.Single(Descendants(root).OfType<TextBlock>(), t => t.Text == text);

    /// <summary>The dot drawn before a chip's or a card's words.</summary>
    private static Ellipse DotBeside(TextBlock words) =>
        Assert.Single(((Panel)VisualTreeHelper.GetParent(words)).Children.OfType<Ellipse>());

    /// <summary>The chip a chip's words sit in.</summary>
    private static Border ChipOf(TextBlock words) =>
        (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(words));

    /// <summary>The bar down the left edge of Process Manager's status chip.</summary>
    private static Border BarBeside(TextBlock words) =>
        ((Grid)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(words))).Children.OfType<Border>().First();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Logical(child)) yield return deeper;
        }
    }
}
