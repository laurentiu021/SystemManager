// SysManager · ConverterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for WPF value converters in Helpers.
/// These don't need a running Application — they test the Convert/ConvertBack
/// logic directly.
/// </summary>
public class ConverterTests
{
    // ---------- OutputKindToColorConverter ----------

    [Theory]
    [InlineData(OutputKind.Error, "OutErrorBrush")]
    [InlineData(OutputKind.Warning, "OutWarnBrush")]
    [InlineData(OutputKind.Info, "OutInfoBrush")]
    [InlineData(OutputKind.Verbose, "OutVerboseBrush")]
    [InlineData(OutputKind.Debug, "OutDebugBrush")]
    [InlineData(OutputKind.Progress, "OutProgressBrush")]
    [InlineData(OutputKind.Output, "OutOutputBrush")]
    public void OutputKindToColor_NamesTheConsoleBrushOfEachKind(OutputKind kind, string expected)
    {
        var conv = new OutputKindToColorConverter();
        Assert.Equal(expected, conv.Convert(kind, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OutputKindToColor_UnknownValue_IsTheOrdinaryOutputColour()
    {
        var conv = new OutputKindToColorConverter();
        Assert.Equal("OutOutputBrush", conv.Convert(999, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OutputKindToColor_ConvertBack_Throws()
    {
        var conv = new OutputKindToColorConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack("OutErrorBrush", typeof(OutputKind), null!, CultureInfo.InvariantCulture));
    }

    // ---------- FlexibleBoolToVisibilityConverter ----------

    [Fact]
    public void FlexibleBool_True_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_False_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_Null_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_NonNullObject_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("anything", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_True_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(true, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_False_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(false, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_ConvertBack_Throws()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Visibility.Visible, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    // Numeric values drive visibility by their non-zero-ness — this is what lets an
    // empty-state message bind to a collection's .Count with ConverterParameter=Inverse
    // (show the message only when Count == 0). Before this, any int was treated as a
    // non-null object => always truthy, so count-bound empty states never appeared.

    [Fact]
    public void FlexibleBool_ZeroCount_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(0, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_NonZeroCount_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(5, typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_ZeroCount_ReturnsVisible()
    {
        // The empty-state pattern: Count == 0 with Inverse => the message shows.
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(0, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_Inverse_NonZeroCount_ReturnsCollapsed()
    {
        // Count > 0 with Inverse => the message is hidden (the list has content).
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert(12, typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_ZeroLong_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, conv.Convert(0L, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FlexibleBool_EmptyString_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_WhitespaceString_ReturnsCollapsed()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("   ", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void FlexibleBool_NonEmptyString_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("hello", typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void FlexibleBool_EmptyString_Inverse_ReturnsVisible()
    {
        var conv = new FlexibleBoolToVisibilityConverter();
        var result = conv.Convert("", typeof(Visibility), "Inverse", CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    // ---------- BoolToElevationBadgeBrushConverter ----------

    [Fact]
    public void ElevationBadge_True_ReturnsGreenBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert(true, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x4C, 0xAF, 0x50), brush.Color);
    }

    [Fact]
    public void ElevationBadge_False_ReturnsGrayBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert(false, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x9E, 0x9E, 0x9E), brush.Color);
    }

    [Fact]
    public void ElevationBadge_NonBool_ReturnsGrayBrush()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        var result = conv.Convert("not a bool", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x9E, 0x9E, 0x9E), brush.Color);
    }

    [Fact]
    public void ElevationBadge_ConvertBack_Throws()
    {
        var conv = new BoolToElevationBadgeBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.White, typeof(bool), null!, CultureInfo.InvariantCulture));
    }

    // ---------- ProcessStatusToColorConverter ----------

    [Theory]
    [InlineData("Running", "SuccessText")]
    [InlineData("Not responding", "DangerText")]
    [InlineData("not RESPONDING", "DangerText")]
    public void StatusColor_NamesTheThemesStatusText(string status, string expected)
    {
        // Fixed #22C55E and #EF4444 on every theme put "Running" at 1.5 to 1.9:1 on the light presets' chip (#2818).
        var conv = new ProcessStatusToColorConverter();
        Assert.Equal(expected, conv.Convert(status, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("")]
    [InlineData(null)]
    public void StatusColor_AnyOtherStatus_IsGrey(string? status)
    {
        var conv = new ProcessStatusToColorConverter();
        Assert.Equal("#808080", conv.Convert(status!, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void StatusColor_ConvertBack_Throws()
    {
        var conv = new ProcessStatusToColorConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack("SuccessText", typeof(string), null!, CultureInfo.InvariantCulture));
    }

    // ---------- ProcessSafety* (Process Manager provenance chip) ----------
    //
    // These exist as SEPARATE converters from SafetyLevel* because ProcessEntry.SafetyLevel is a
    // STRING (ProcessSafety: System/Trusted/Unknown) while SafetyLevel* switch on the Models
    // SafetyLevel ENUM (Safe/Caution/Critical). Binding the string to the enum converters misses
    // every arm and falls through to `_ => Critical`, i.e. every process rendered red. The first
    // test below is what makes that mistake impossible to reintroduce silently.

    [Fact]
    public void SafetyLevel_NotRated_ReadsAsNotRated_InTheNeutralGrey()
    {
        // #1512: grey and plain, so "we have no rating" no longer wears the red that means "this breaks Windows".
        var text = new SafetyLevelToTextConverter().Convert(SafetyLevel.NotRated, typeof(string), null!, CultureInfo.InvariantCulture);
        var color = new SafetyLevelToColorConverter();
        var background = new SafetyLevelToBackgroundColorConverter();
        object Colour(IValueConverter converter, SafetyLevel level) =>
            converter.Convert(level, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("Not rated", text);
        Assert.Equal("TextMuted", Colour(color, SafetyLevel.NotRated));
        Assert.Equal("DangerText", Colour(color, SafetyLevel.Critical));
        Assert.NotEqual(Colour(background, SafetyLevel.Critical), Colour(background, SafetyLevel.NotRated));
    }

    [Fact]
    public void EverySafetyLevel_HasItsOwnWordsAndColours()
    {
        // A level the converters do not name falls through to Critical's red and word, which is how most services came
        // to read Critical: this keeps a new level from doing it silently.
        var text = new SafetyLevelToTextConverter();
        var color = new SafetyLevelToColorConverter();
        var background = new SafetyLevelToBackgroundColorConverter();
        var levels = Enum.GetValues<SafetyLevel>();

        List<object> Each(IValueConverter converter) =>
            [.. levels.Select(l => converter.Convert(l, typeof(string), null!, CultureInfo.InvariantCulture))];
        var words = Each(text);
        var inks = Each(color);
        var fills = Each(background);

        Assert.Equal(levels.Length, words.Distinct().Count());
        Assert.Equal(levels.Length, inks.Distinct().Count());
        Assert.Equal(levels.Length, fills.Distinct().Count());
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Trusted")]
    [InlineData("Unknown")]
    public void SafetyLevelEnumConverters_CannotRenderProcessStrings(string provenance)
    {
        // Guard, not an endorsement: proves the enum converters treat all three provenance strings
        // identically (the fall-through), so they are unusable here and the dedicated ones are required.
        var text = new SafetyLevelToTextConverter();
        Assert.Equal("Critical", text.Convert(provenance, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("System", "Windows")]
    [InlineData("system", "Windows")]          // database casing must not matter
    [InlineData("Trusted", "Known app")]
    [InlineData("Unknown", "Not recognised")]
    [InlineData("", "Not recognised")]
    [InlineData(null, "Not recognised")]
    public void ProcSafetyText_UsesPlainLanguageLabels(string? provenance, string expected)
    {
        // The raw enum names are developer-facing. "Windows" / "Known app" / "Not recognised" answer
        // the question the target user is actually asking.
        var conv = new ProcessSafetyToTextConverter();
        Assert.Equal(expected, conv.Convert(provenance!, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProcSafetyColor_ThreeProvenanceValues_AreVisuallyDistinct()
    {
        // If two of them named the same colour the column would convey nothing.
        var conv = new ProcessSafetyToColorConverter();
        var system = conv.Convert("System", typeof(string), null!, CultureInfo.InvariantCulture);
        var trusted = conv.Convert("Trusted", typeof(string), null!, CultureInfo.InvariantCulture);
        var unknown = conv.Convert("Unknown", typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.NotEqual(system, trusted);
        Assert.NotEqual(system, unknown);
        Assert.NotEqual(trusted, unknown);
    }

    [Fact]
    public void ProcSafetyColor_Unknown_IsMutedNotAWarningColour()
    {
        // The database holds 108 entries while a typical machine runs 200+ processes, so most rows are
        // Unknown. Tinting them amber/red would make a normal machine look alarming and train the user
        // to ignore the column. It uses the app's existing TextMuted grey — "no information", which is
        // what Unknown means — and is asserted to be neither the warning nor the danger colour.
        var unknown = ProcessSafetyPalette.TextColor("Unknown");

        Assert.Equal("#7B8396", unknown);   // a fixed colour, the same on every theme
        var muted = (Color)ColorConverter.ConvertFromString(unknown);

        // Desaturated: no channel dominates the way it does in an amber/red alert.
        var max = Math.Max(muted.R, Math.Max(muted.G, muted.B));
        var min = Math.Min(muted.R, Math.Min(muted.G, muted.B));
        Assert.True(max - min < 0x30, $"spread {max - min:X} is too saturated for a neutral chip");
    }

    [Theory]
    [InlineData("System", "InfoText", "InfoBgSubtle")]
    [InlineData("Trusted", "SuccessText", "SuccessBgSubtle")]
    public void ProcSafetyColor_KnownProvenance_NamesTheThemesStatusBrushes(string provenance, string text, string tint)
    {
        // Known values name a ThemeService.StatusPalette brush, so the chip stays legible on the light presets and
        // follows a theme switch.
        Assert.Equal(text, ProcessSafetyPalette.TextColor(provenance));
        Assert.Equal(tint, ProcessSafetyPalette.BackgroundColor(provenance));
    }

    [Theory]
    [InlineData("System", "will not crash")]
    [InlineData("Trusted", "Safe to end")]
    [InlineData("Unknown", "does not make it harmful")]
    public void ProcSafetyTip_ExplainsWhetherEndingItIsSafe(string provenance, string expected)
    {
        // The chip is three words wide; the tooltip is where the consequence is actually stated. In
        // particular "Windows" must not read as "do not touch" — that was the old, false, refusal.
        var conv = new ProcessSafetyToTooltipConverter();
        var tip = (string)conv.Convert(provenance, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Contains(expected, tip);
    }

    [Fact]
    public void ProcSafety_AllConverters_ConvertBackThrows()
    {
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToColorConverter()
            .ConvertBack("InfoText", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToBackgroundColorConverter()
            .ConvertBack("InfoBgSubtle", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToTextConverter()
            .ConvertBack("Windows", typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToTooltipConverter()
            .ConvertBack("tip", typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProcessManagerView_RendersTheSafetyColumn()
    {
        // The defect was that nothing referenced the value: the database was loaded, assigned to
        // ProcessEntry.SafetyLevel and never displayed. Asserting the ViewModel property alone would
        // pass on the unfixed code, so this checks the shipped markup binds it — and that the column
        // uses the process converters rather than the enum ones.
        var xaml = File.ReadAllText(TestPaths.AppFile("Views", "ProcessManagerView.xaml"));

        Assert.Contains("Header=\"Safety\"", xaml);
        Assert.Contains("ProcSafetyText", xaml);
        Assert.Contains("ProcSafetyColor", xaml);
        Assert.Contains("ProcSafetyBgColor", xaml);
        Assert.Contains("ProcSafetyTip", xaml);
        Assert.Contains("SortMemberPath=\"SafetyLevel\"", xaml);
    }

    [Fact]
    public void ProcessManagerView_ShowsTheDatabaseDescription_NotTheRawFileDescription()
    {
        // Same defect class as the Safety column above, one field over: the database's plain-English
        // description was loaded into ProcessEntry.PlainDescription, used to widen the search filter, and
        // bound by no XAML — the Name column showed the raw exe FileDescription instead. That is jargon
        // at best, and blank for most Windows system processes, because MainModule throws access-denied
        // when unelevated — precisely the processes the database covers best.
        var xaml = File.ReadAllText(TestPaths.AppFile("Views", "ProcessManagerView.xaml"));

        Assert.Contains("Binding PlainDescription", xaml);

        // The raw value stays as the fallback, so a process the database does not know still shows
        // whatever Windows reported rather than an empty line.
        Assert.Contains("Value=\"{Binding Description}\"", xaml);
    }

    [Fact]
    public void ProcessManagerView_ShowsTheDatabaseCategory()
    {
        // The search box already matched against Category, so a user could filter by a word the app
        // never showed them. README advertises the categories alongside the descriptions.
        var xaml = File.ReadAllText(TestPaths.AppFile("Views", "ProcessManagerView.xaml"));

        Assert.Contains("Header=\"Category\"", xaml);
        Assert.Contains("SortMemberPath=\"Category\"", xaml);
    }

    // ---------- FormatConverter ----------

    [Theory]
    [InlineData(3, "Safe ({0})", "Safe (3)")]
    [InlineData("Print Spooler", "Start {0}", "Start Print Spooler")]
    [InlineData(@"D:\logs", "Opens SysManager's own diagnostic logs: {0}",
                @"Opens SysManager's own diagnostic logs: D:\logs")]
    public void Format_PutsTheValueIntoTheTemplate(object value, string template, string expected)
    {
        // A StringFormat on Content, Header or ToolTip is dropped, so the property needs the text itself (#2808).
        var text = new FormatConverter().Convert(value, typeof(object), template, CultureInfo.InvariantCulture);
        Assert.Equal(expected, Assert.IsType<string>(text));
    }

    [Fact]
    public void Format_HandsBackAValueWpfHasNotResolved()
    {
        // Wrapped in the template it would read "Kill {DependencyProperty.UnsetValue}" instead of the fallback.
        var text = new FormatConverter().Convert(DependencyProperty.UnsetValue, typeof(object), "Kill {0}",
                                                 CultureInfo.InvariantCulture);
        Assert.Same(DependencyProperty.UnsetValue, text);
    }

    [Fact]
    public void Format_WithNoTemplate_HandsBackTheValue()
        => Assert.Equal(3, new FormatConverter().Convert(3, typeof(object), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void Format_ConvertBackThrows()
        => Assert.Throws<NotSupportedException>(() => new FormatConverter()
            .ConvertBack("Safe (3)", typeof(int), "Safe ({0})", CultureInfo.InvariantCulture));

    [Fact]
    public void App_RegistersTheProcessSafetyConverters()
    {
        // A binding to an unregistered StaticResource key is a runtime XAML failure, and the view test
        // above only proves the key is USED.
        var xaml = File.ReadAllText(TestPaths.AppFile("App.xaml"));

        Assert.Contains("x:Key=\"ProcSafetyColor\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyBgColor\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyText\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyTip\"", xaml);
    }
}
