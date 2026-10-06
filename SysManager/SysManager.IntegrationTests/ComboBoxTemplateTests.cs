// SysManager · ComboBoxTemplateTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Models;

namespace SysManager.IntegrationTests;

/// <summary>
/// The app's own ComboBox template shows what a closed box is told to show.
/// </summary>
/// <remarks>
/// <c>DisplayMemberPath</c> is not a template: WPF turns it into the box's <c>ItemTemplateSelector</c>, and the
/// closed box only shows it if the template passes that selector to the presenter that draws the selection. The
/// template App.xaml has carried since v1.52.24 (#1243) passed the template and not the selector, so every box
/// that names a member drew its item's <c>ToString()</c> instead — Volume Control's presets as
/// "VolumePreset { Name = Evening, Entries = … }", Gaming Profile's game as "RunningProcess { ProcessId = … }".
/// A compiler, a view-model test and a binding check all pass that; only drawing the box shows it.
/// </remarks>
public sealed class ComboBoxTemplateTests
{
    [Fact]
    public void AClosedBox_ShowsItsDisplayMember_NotTheItem()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var combo = new ComboBox
            {
                ItemsSource = new[]
                {
                    new VolumePreset("Evening", []),
                    new VolumePreset("Gaming", []),
                },
                DisplayMemberPath = nameof(VolumePreset.Name),
                SelectedIndex = 0,
            };

            var shown = Drawn(combo);

            Assert.Equal(["Evening"], shown);
        });
    }

    [Fact]
    public void AClosedBox_ShowsTheChangedSelection()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var combo = new ComboBox
            {
                ItemsSource = new[]
                {
                    new AudioDevice("{0.0.0.00000000}.{1}", "Headphones (Studio)", IsDefault: true),
                    new AudioDevice("{0.0.0.00000000}.{2}", "Speakers (Desk)", IsDefault: false),
                },
                DisplayMemberPath = nameof(AudioDevice.FriendlyName),
                SelectedIndex = 0,
            };
            Drawn(combo);

            combo.SelectedIndex = 1;

            Assert.Equal(["Speakers (Desk)"], Drawn(combo));
        });
    }

    /// <summary>Lays the closed box out the way a window would and returns the text it draws, in order.</summary>
    private static List<string> Drawn(ComboBox combo)
    {
        if (combo.Parent is null)
        {
            var host = new Border { Child = combo };
            host.Measure(new Size(400, 60));
            host.Arrange(new Rect(0, 0, 400, 60));
        }
        combo.UpdateLayout();

        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            // The chevron is a Segoe Fluent glyph, a single private-use character: part of the box, not the item.
            if (node is TextBlock { Text.Length: > 0 } block && !IsGlyph(block.Text)) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(combo);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';
}
