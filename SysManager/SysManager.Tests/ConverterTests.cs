// SysManager · ConverterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Windows;
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
    // ---------- OutputKindToBrushConverter ----------

    [Theory]
    [InlineData(OutputKind.Error)]
    [InlineData(OutputKind.Warning)]
    [InlineData(OutputKind.Info)]
    [InlineData(OutputKind.Verbose)]
    [InlineData(OutputKind.Debug)]
    [InlineData(OutputKind.Progress)]
    [InlineData(OutputKind.Output)]
    public void OutputKindToBrush_AllKinds_ReturnBrush(OutputKind kind)
    {
        var conv = new OutputKindToBrushConverter();
        var result = conv.Convert(kind, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void OutputKindToBrush_UnknownValue_ReturnsBrush()
    {
        var conv = new OutputKindToBrushConverter();
        var result = conv.Convert(999, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<Brush>(result);
    }

    [Fact]
    public void OutputKindToBrush_ConvertBack_Throws()
    {
        var conv = new OutputKindToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.White, typeof(OutputKind), null!, CultureInfo.InvariantCulture));
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

    // ---------- HexToBrushConverter ----------

    [Fact]
    public void HexToBrush_ValidHex_ReturnsSolidColorBrush()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("#4CC9F0", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x4C, 0xC9, 0xF0), brush.Color);
    }

    [Fact]
    public void HexToBrush_Null_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_EmptyString_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_InvalidHex_ReturnsGray()
    {
        var conv = new HexToBrushConverter();
        var result = conv.Convert("not-a-color", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void HexToBrush_ConvertBack_Throws()
    {
        var conv = new HexToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    // ---------- ProcessStatusToBrushConverter ----------

    [Fact]
    public void StatusBrush_Running_ReturnsGreen()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Running", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), brush.Color);
    }

    [Fact]
    public void StatusBrush_NotResponding_ReturnsRed()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Not responding", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), brush.Color);
    }

    [Fact]
    public void StatusBrush_Unknown_ReturnsGray()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert("Suspended", typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void StatusBrush_Null_ReturnsGray()
    {
        var conv = new ProcessStatusToBrushConverter();
        var result = conv.Convert(null!, typeof(Brush), null!, CultureInfo.InvariantCulture);
        Assert.Equal(Brushes.Gray, result);
    }

    [Fact]
    public void StatusBrush_ConvertBack_Throws()
    {
        var conv = new ProcessStatusToBrushConverter();
        Assert.Throws<NotSupportedException>(() =>
            conv.ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    // ---------- ProcessSafety* (Process Manager provenance chip) ----------
    //
    // These exist as SEPARATE converters from SafetyLevel* because ProcessEntry.SafetyLevel is a
    // STRING (ProcessSafety: System/Trusted/Unknown) while SafetyLevel* switch on the Models
    // SafetyLevel ENUM (Safe/Caution/Critical). Binding the string to the enum converters misses
    // every arm and falls through to `_ => Critical`, i.e. every process rendered red. The first
    // test below is what makes that mistake impossible to reintroduce silently.

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
    public void ProcSafetyBrush_ThreeProvenanceValues_AreVisuallyDistinct()
    {
        // If two of them resolved to the same brush the column would convey nothing.
        var conv = new ProcessSafetyToBrushConverter();
        var system = conv.Convert("System", typeof(Brush), null!, CultureInfo.InvariantCulture);
        var trusted = conv.Convert("Trusted", typeof(Brush), null!, CultureInfo.InvariantCulture);
        var unknown = conv.Convert("Unknown", typeof(Brush), null!, CultureInfo.InvariantCulture);

        Assert.NotEqual(system, trusted);
        Assert.NotEqual(system, unknown);
        Assert.NotEqual(trusted, unknown);
    }

    [Fact]
    public void ProcSafetyBrush_Unknown_IsMutedNotAWarningColour()
    {
        // The database holds 108 entries while a typical machine runs 200+ processes, so most rows are
        // Unknown. Tinting them amber/red would make a normal machine look alarming and train the user
        // to ignore the column. It uses the app's existing TextMuted grey — "no information", which is
        // what Unknown means — and is asserted to be neither the warning nor the danger colour.
        var (key, fallback) = ProcessSafetyPalette.TextBrushKey("Unknown");

        Assert.Null(key);   // deliberately theme-independent
        var muted = Assert.IsType<SolidColorBrush>(fallback);
        Assert.Equal(Color.FromRgb(0x7B, 0x83, 0x96), muted.Color);   // TextMuted

        // Desaturated: no channel dominates the way it does in an amber/red alert.
        var max = Math.Max(muted.Color.R, Math.Max(muted.Color.G, muted.Color.B));
        var min = Math.Min(muted.Color.R, Math.Min(muted.Color.G, muted.Color.B));
        Assert.True(max - min < 0x30, $"spread {max - min:X} is too saturated for a neutral chip");
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Trusted")]
    public void ProcSafetyBrush_KnownProvenance_ResolvesThroughTheThemePalette(string provenance)
    {
        // Known values must go through a ThemeService.StatusPalette key so the chip stays legible on
        // light presets, with a frozen dark brush as the design-time/unit-test fallback.
        var (key, fallback) = ProcessSafetyPalette.TextBrushKey(provenance);

        Assert.NotNull(key);
        Assert.True(fallback.IsFrozen);
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
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToBrushConverter()
            .ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => new ProcessSafetyToBackgroundConverter()
            .ConvertBack(Brushes.Gray, typeof(string), null!, CultureInfo.InvariantCulture));
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
        var xaml = File.ReadAllText(ViewPath("ProcessManagerView.xaml"));

        Assert.Contains("Header=\"Safety\"", xaml);
        Assert.Contains("ProcSafetyText", xaml);
        Assert.Contains("ProcSafetyBrush", xaml);
        Assert.Contains("ProcSafetyBg", xaml);
        Assert.Contains("ProcSafetyTip", xaml);
        Assert.Contains("SortMemberPath=\"SafetyLevel\"", xaml);
    }

    [Fact]
    public void App_RegistersTheProcessSafetyConverters()
    {
        // A binding to an unregistered StaticResource key is a runtime XAML failure, and the view test
        // above only proves the key is USED.
        var xaml = File.ReadAllText(ViewPath("App.xaml", inViews: false));

        Assert.Contains("x:Key=\"ProcSafetyBrush\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyBg\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyText\"", xaml);
        Assert.Contains("x:Key=\"ProcSafetyTip\"", xaml);
    }

    // Walks up from the test binaries to the app project — the .xaml is not copied to the output.
    private static string ViewPath(string fileName, bool inViews = true)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions below would silently test nothing
        var path = inViews
            ? Path.Combine(dir!.FullName, "SysManager", "Views", fileName)
            : Path.Combine(dir!.FullName, "SysManager", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }
}
