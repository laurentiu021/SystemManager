// SysManager · ThemeBrushTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="ThemeBrush"/> paints a fixed colour as a brush and a theme brush's name as a reference to it (#2818).
/// </summary>
/// <remarks>
/// The reference is what makes a colour follow a theme switch: <c>ThemeService.Apply</c> stores a new brush under each
/// name, and only a reference looks the name up again. <c>ThemeSwitchColourTests</c> switches the theme under the
/// views; these pin the parts, with no Application, so a name resolves to nothing and only the reference is checked.
/// </remarks>
public class ThemeBrushTests
{
    [StaFact]
    public void AFixedColour_PaintsThatColour_WithOneFrozenBrushForEveryElement()
    {
        var first = new Ellipse();
        var second = new Ellipse();

        ThemeBrush.SetFill(first, "#4CC9F0");
        ThemeBrush.SetFill(second, "#4cc9f0");

        var brush = Assert.IsType<SolidColorBrush>(first.Fill);
        Assert.Equal(Color.FromRgb(0x4C, 0xC9, 0xF0), brush.Color);
        Assert.True(brush.IsFrozen);
        Assert.Same(brush, second.Fill);
    }

    [StaFact]
    public void AFixedColour_KeepsItsAlpha()
    {
        var chip = new Border();

        ThemeBrush.SetBackground(chip, "#207B8396");

        Assert.Equal(Color.FromArgb(0x20, 0x7B, 0x83, 0x96), Assert.IsType<SolidColorBrush>(chip.Background).Color);
    }

    [StaTheory]
    [InlineData("#GGHHII")]
    [InlineData("#12")]
    [InlineData("#")]
    public void AFixedColourThatDoesNotParse_IsGrey(string color)
    {
        var dot = new Ellipse();

        ThemeBrush.SetFill(dot, color);

        Assert.Same(Brushes.Gray, dot.Fill);
    }

    [StaFact]
    public void AName_SetsAReferenceToTheThemeBrush_NotTheBrush()
    {
        // A brush looked up once keeps the old theme's colour after a switch; a reference looks the name up again.
        var verdict = new TextBlock();

        ThemeBrush.SetForeground(verdict, StatusColors.Good);

        var source = DependencyPropertyHelper.GetValueSource(verdict, TextBlock.ForegroundProperty);
        Assert.Equal(BaseValueSource.Local, source.BaseValueSource);
        Assert.True(source.IsExpression, "the name was set as a brush rather than as a reference to the theme's brush");
        Assert.IsNotAssignableFrom<Brush>(verdict.ReadLocalValue(TextBlock.ForegroundProperty));
    }

    [StaFact]
    public void AName_OnARun_SetsAReferenceToo()
    {
        var run = new Run("code");

        ThemeBrush.SetForeground(run, "Accent");

        Assert.True(DependencyPropertyHelper.GetValueSource(run, TextElement.ForegroundProperty).IsExpression);
    }

    [StaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoColour_ClearsWhatItSet(string? color)
    {
        var bar = new Border();
        ThemeBrush.SetBackground(bar, "#FF0000");

        ThemeBrush.SetBackground(bar, color);

        Assert.Same(DependencyProperty.UnsetValue, bar.ReadLocalValue(Border.BackgroundProperty));
    }

    [StaFact]
    public void AFixedColourAfterAName_ReplacesTheReference()
    {
        var text = new TextBlock();
        ThemeBrush.SetForeground(text, "SuccessText");

        ThemeBrush.SetForeground(text, "#123456");

        Assert.False(DependencyPropertyHelper.GetValueSource(text, TextBlock.ForegroundProperty).IsExpression);
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
    }

    [StaFact]
    public void EachProperty_PaintsThePropertyOfTheElementItIsOn()
    {
        // WPF shares one property between the elements that have it, except a TextBlock's Background, which is its
        // text's own.
        const string Red = "#FF0000";
        var expected = Color.FromRgb(0xFF, 0, 0);
        Color Of(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;

        var text = new TextBlock();
        var run = new Run();
        var button = new Button();
        var bar = new ProgressBar();
        var border = new Border();
        var grid = new Grid();
        var dot = new Ellipse();
        var chip = new Border();

        ThemeBrush.SetForeground(text, Red);
        ThemeBrush.SetForeground(run, Red);
        ThemeBrush.SetForeground(button, Red);
        ThemeBrush.SetForeground(bar, Red);
        ThemeBrush.SetBackground(border, Red);
        ThemeBrush.SetBackground(grid, Red);
        ThemeBrush.SetBackground(button, Red);
        ThemeBrush.SetBackground(text, Red);
        ThemeBrush.SetFill(dot, Red);
        ThemeBrush.SetBorderBrush(chip, Red);
        ThemeBrush.SetBorderBrush(button, Red);

        Assert.Equal(expected, Of(text.Foreground));
        Assert.Equal(expected, Of(run.Foreground));
        Assert.Equal(expected, Of(button.Foreground));
        Assert.Equal(expected, Of(bar.Foreground));
        Assert.Equal(expected, Of(border.Background));
        Assert.Equal(expected, Of(grid.Background));
        Assert.Equal(expected, Of(button.Background));
        Assert.Equal(expected, Of(text.Background));
        Assert.Equal(expected, Of(dot.Fill));
        Assert.Equal(expected, Of(chip.BorderBrush));
        Assert.Equal(expected, Of(button.BorderBrush));
    }

    /// <summary>
    /// Every colour a converter or a status can name is one the app can paint: a fixed colour that parses, or a brush
    /// App.xaml defines.
    /// </summary>
    /// <remarks>
    /// A name the app does not define leaves the element at its default colour, which is not an error anyone sees.
    /// </remarks>
    [Fact]
    public void EveryColourTheConvertersAndStatusesName_IsOneTheAppCanPaint()
    {
        var app = XDocument.Load(TestPaths.AppFile("App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushes = app.Descendants()
            .Where(e => e.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute(x + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(brushes.Count >= 40, $"read {brushes.Count} brushes from App.xaml, too few to check against");

        List<(string From, object Color)> named = [];
        void Name(IValueConverter converter, IEnumerable<object?> values)
        {
            foreach (var value in values)
                named.Add(($"{converter.GetType().Name}({value})",
                    converter.Convert(value!, typeof(string), null!, CultureInfo.InvariantCulture)));
        }

        Name(new OutputKindToColorConverter(), Enum.GetValues<OutputKind>().Cast<object?>());
        Name(new ProcessStatusToColorConverter(), ["Running", "Not responding", "Suspended", null]);
        Name(new SafetyLevelToColorConverter(), Enum.GetValues<SafetyLevel>().Cast<object?>());
        Name(new SafetyLevelToBackgroundColorConverter(), Enum.GetValues<SafetyLevel>().Cast<object?>());
        Name(new ProcessSafetyToColorConverter(), Enum.GetNames<ProcessSafety>().Append(null));
        Name(new ProcessSafetyToBackgroundColorConverter(), Enum.GetNames<ProcessSafety>().Append(null));
        Name(new SignatureTrustToColorConverter(), Enum.GetValues<SignatureTrust>().Cast<object?>());
        Name(new SignatureTrustToBackgroundColorConverter(), Enum.GetValues<SignatureTrust>().Cast<object?>());
        named.AddRange(StatusColors.AllBrushKeys.Select(key => ("StatusColors", (object)key)));

        var unpaintable = named
            .Where(n => n.Color is not string color
                        || (color.StartsWith('#') ? !Parses(color) : !brushes.Contains(color)))
            .Select(n => $"{n.From} -> {n.Color}")
            .ToList();

        Assert.True(unpaintable.Count == 0,
            "These name no colour the app can paint:\n  " + string.Join("\n  ", unpaintable));
        Assert.True(named.Count >= 30, $"checked {named.Count} colours; the list of converters went short");
    }

    private static bool Parses(string color)
    {
        try
        {
            _ = (Color)ColorConverter.ConvertFromString(color);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
