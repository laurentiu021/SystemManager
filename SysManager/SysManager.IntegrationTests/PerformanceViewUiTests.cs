// SysManager · PerformanceViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Performance Mode's Current State card says each setting in words (#2786).
/// </summary>
/// <remarks>
/// The card bound four settings' raw values into fixed prefixes, so it read "Reduced: True", "Enabled: False", and for
/// a Game Bar that is on, "Disabled: False", and a row of its own showed the power plan's GUID. The view is laid out
/// with the app's resources over a stand-in that carries only the profile the card binds, so nothing on this PC is read
/// or changed.
/// </remarks>
[Collection("Network")]
public sealed class PerformanceViewUiTests
{
    /// <summary>What the Current State card binds to, and nothing else.</summary>
    private sealed class CardSource(PerformanceProfile profile)
    {
        public PerformanceProfile Profile { get; } = profile;
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

    /// <summary>Each row of the Current State card as laid out: its label, and the value beside it.</summary>
    private static Dictionary<string, string> CurrentState(PerformanceProfile profile)
    {
        AppResources.Ensure();
        var view = new PerformanceView { DataContext = new CardSource(profile) };
        view.Measure(new Size(1200, 4000));
        view.Arrange(new Rect(0, 0, 1200, 4000));
        view.UpdateLayout();

        var card = Descendants(view).OfType<Grid>()
            .Single(g => g.Children.OfType<TextBlock>().Any(t => t.Text == "Active power plan"));
        var cells = card.Children.OfType<TextBlock>().ToList();
        return cells.Where(c => Grid.GetColumn(c) == 0).ToDictionary(
            label => label.Text,
            label => Assert.Single(cells, c => Grid.GetColumn(c) == 1 && Grid.GetRow(c) == Grid.GetRow(label)).Text);
    }

    [Fact]
    public void TheCard_SaysEachSettingInWords_AndHasNoRowForThePlansGuid()
    {
        var profile = new PerformanceProfile
        {
            ActivePlanName = "Balanced",
            ActivePlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            VisualEffectsReduced = true,
            GameModeEnabled = false,
            XboxGameBarOn = true,
            XboxGameDvrOn = false,
            HasNvidiaGpu = false,
            ProcessorMinPercent = 5,
        };

        StaHelper.Run(() => Assert.Equal(
            new Dictionary<string, string>
            {
                ["Active power plan"] = "Balanced",
                ["Visual effects"] = "Reduced",
                ["Game Mode"] = "Off",
                ["Xbox Game Bar"] = "Partly on: the overlay is on, game recording is off",
                ["GPU max performance"] = "No NVIDIA GPU detected",
                ["Processor min state"] = "5%",
            },
            CurrentState(profile)));
    }

    [Fact]
    public void TheCard_SaysOn_ForEachSettingThatIsOn()
    {
        var profile = new PerformanceProfile
        {
            ActivePlanName = "Ultimate Performance",
            GameModeEnabled = true,
            XboxGameBarOn = true,
            XboxGameDvrOn = true,
            HasNvidiaGpu = true,
            GpuMaxPerformance = true,
            ProcessorMinPercent = 100,
        };

        StaHelper.Run(() =>
        {
            var shown = CurrentState(profile);

            Assert.Equal(("Normal", "On", "On", "On"),
                (shown["Visual effects"], shown["Game Mode"], shown["Xbox Game Bar"], shown["GPU max performance"]));
        });
    }
}
