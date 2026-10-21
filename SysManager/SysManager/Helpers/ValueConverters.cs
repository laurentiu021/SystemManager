// SysManager · ValueConverters
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Models;

namespace SysManager.Helpers;

/// <summary>
/// Maps a console line's <see cref="OutputKind"/> to the name of the theme brush it is written in, for
/// <see cref="ThemeBrush"/>.
/// </summary>
public sealed class OutputKindToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OutputKind.Error => "OutErrorBrush",
        OutputKind.Warning => "OutWarnBrush",
        OutputKind.Info => "OutInfoBrush",
        OutputKind.Verbose => "OutVerboseBrush",
        OutputKind.Debug => "OutDebugBrush",
        OutputKind.Progress => "OutProgressBrush",
        _ => "OutOutputBrush",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// BooleanToVisibility with optional inversion via ConverterParameter="Inverse".
/// Truthiness rules: a bool is itself; null is false; a non-empty string is true;
/// a numeric value is true only when non-zero; any other non-null object is true.
/// The numeric rule lets a collection's <c>.Count</c> drive visibility directly
/// (e.g. an empty-state message bound to <c>Items.Count</c> with <c>Inverse</c>),
/// which is the common "show this when the list is empty" pattern.
/// </summary>
public sealed class FlexibleBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            bool b => b,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            sbyte or byte or short or ushort or int or uint or long or ulong => System.Convert.ToInt64(value) != 0,
            float or double or decimal => System.Convert.ToDecimal(value) != 0m,
            _ => true
        };
        var invert = parameter as string == "Inverse";
        if (invert) truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean value. Use for IsEnabled bindings where the source
/// property indicates a "busy" state and the target should be disabled.
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class BoolInverterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class BoolToElevationBadgeBrushConverter : IValueConverter
{
    private static readonly Brush ElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)));
    private static readonly Brush NotElevatedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? ElevatedBrush : NotElevatedBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Maps process status text ("Running" / "Not responding") to the colour its chip is drawn in, for
/// <see cref="ThemeBrush"/>: the theme's green for running, its red for not responding, grey for anything else.
/// </summary>
/// <remarks>
/// The theme's status text brushes, the ones the Safety chip beside it uses. It returned #22C55E and #EF4444 on every
/// theme, which on the light presets' grey chip came to between 1.5 and 1.9:1 for "Running", the state of nearly every
/// row (#2818).
/// </remarks>
public sealed class ProcessStatusToColorConverter : IValueConverter
{
    /// <summary>The grey for any other status, the same on every theme.</summary>
    internal const string OtherStatus = "#808080";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        string s when s.Contains("Not responding", StringComparison.OrdinalIgnoreCase) => "DangerText",
        string s when s.Contains("Running", StringComparison.OrdinalIgnoreCase) => "SuccessText",
        _ => OtherStatus,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="SafetyLevel"/> to the theme brush its chip's text and dot are drawn in, for
/// <see cref="ThemeBrush"/>.
/// </summary>
/// <remarks>
/// The theme's *Text brushes, which <see cref="Services.ThemeService.StatusPalette"/> sets per mode, so the chip stays
/// legible on the light presets, where the dark palette's pale red and amber washed out on the near-white chip.
/// </remarks>
public sealed class SafetyLevelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SafetyLevel.Safe => "SuccessText",
        SafetyLevel.Caution => "WarningText",
        // The app's "no information" grey, as Process Manager's Not recognised chip uses (#1512).
        SafetyLevel.NotRated => "TextMuted",
        _ => "DangerText",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="SafetyLevel"/> to its chip's tint, for <see cref="ThemeBrush"/>: a fixed colour, the same on every
/// theme, at an eighth of full strength.
/// </summary>
public sealed class SafetyLevelToBackgroundColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SafetyLevel.Safe => "#2022C55E",
        SafetyLevel.Caution => "#20F59E0B",
        SafetyLevel.NotRated => "#207B8396",
        _ => "#20EF4444",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SafetyLevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => "Safe",
            SafetyLevel.Caution => "Caution",
            SafetyLevel.NotRated => "Not rated",
            SafetyLevel.Critical => "Critical",
            _ => "Critical"
        } : "Critical";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="MaintenanceAction"/> to the plain-language label the user should see.
/// </summary>
/// <remarks>
/// The Scheduled Maintenance action picker binds a list of enum values, so without this it rendered
/// each one's <c>ToString()</c> — the dropdown offered "Cleanup" and "TrimRam" while the confirmation
/// dialog that followed said "Purge standby memory" (#1524). Delegates to
/// <see cref="MaintenanceSchedule.LabelFor"/> so the picker and the dialog cannot disagree; the label
/// text itself lives in the model, once.
/// </remarks>
public sealed class MaintenanceActionToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MaintenanceAction action ? MaintenanceSchedule.LabelFor(action) : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Puts the bound value into the template in <c>ConverterParameter</c>, such as <c>'Safe ({0})'</c>, and returns the
/// text.
/// </summary>
/// <remarks>
/// For the properties typed <c>object</c>: <c>Content</c>, <c>Header</c> and <c>ToolTip</c>. WPF applies a binding's
/// <c>StringFormat</c> only when the target property holds a string, so on these it dropped the format and the control
/// showed the bare value: Services' filter chips read only their counts, and the row menus read only the name of the
/// service or the program, "Kill" included (#2808). This gives the property the text itself, so what is on screen and
/// the name UI Automation reads from it are one string. <c>ContentStringFormat</c> and <c>HeaderStringFormat</c> would
/// not: they format only what is drawn, and a menu item would still be announced by the bare name.
/// <para>A value WPF has not resolved yet is handed back as it is, so the binding shows its fallback rather than the
/// template around "{DependencyProperty.UnsetValue}".</para>
/// </remarks>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == DependencyProperty.UnsetValue || parameter is not string template
            ? value
            : string.Format(culture, template, value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Process provenance (Process Manager) ─────────────────────────────────────────────────────
// Deliberately SEPARATE from the SafetyLevel* converters above. Those switch on the
// Models.SafetyLevel enum (Safe/Caution/Critical) used by Services and Windows Features;
// ProcessEntry.SafetyLevel is a STRING carrying ProcessSafety (System/Trusted/Unknown), so
// binding it to those converters would miss every arm and fall through to `_ => Critical`,
// painting all ~200 rows red. Different domain, different scale, own converters.

/// <summary>
/// Maps <see cref="SysManager.Services.ProcessSafety"/> (as a string) to the colour of the chip's text and dot, for
/// <see cref="ThemeBrush"/>.
/// </summary>
public sealed class ProcessSafetyToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.TextColor(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="SysManager.Services.ProcessSafety"/> (as a string) to the chip's tint, for <see cref="ThemeBrush"/>.
/// </summary>
public sealed class ProcessSafetyToBackgroundColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.BackgroundColor(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="SysManager.Services.ProcessSafety"/> (as a string) to the label the user reads.
/// The raw enum names are developer-facing; "Windows" / "Known app" / "Not recognised" say what
/// the value actually means to someone deciding whether to end a process.
/// </summary>
public sealed class ProcessSafetyToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.Label(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Explains a provenance value on hover. The chip is only three words wide; the tooltip is where the
/// user learns whether ending the process is actually safe.
/// </summary>
public sealed class ProcessSafetyToTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.Tooltip(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The colour and wording decisions for process provenance, in one place so the three converters
/// cannot drift apart, and so the mapping is unit-testable without a WPF Application.
/// </summary>
public static class ProcessSafetyPalette
{
    // TextMuted / Surface3 are the app's existing "no emphasis" pair, so an unrecognised process
    // looks like ordinary de-emphasised text rather than a bespoke grey invented for this column.
    private const string UnknownText = "#7B8396";
    private const string UnknownBg = "#207B8396";

    /// <summary>
    /// The colour of the chip's text and dot, for <see cref="ThemeBrush"/>: the name of a theme brush, or a fixed
    /// "#" colour for a value that is the same on every theme.
    /// </summary>
    /// <remarks>
    /// Unknown is deliberately neutral grey, NOT a warning colour. The database holds 108 entries
    /// while a typical machine runs 200+ processes, so most rows are Unknown; tinting them amber
    /// would make the whole grid look alarming and teach the user to ignore the column. Grey reads
    /// as "no information", which is what it means.
    /// </remarks>
    public static string TextColor(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) => "InfoText",
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) => "SuccessText",
        _ => UnknownText,
    };

    /// <summary>The colour of the chip's tint and border, in the same form as <see cref="TextColor"/>.</summary>
    public static string BackgroundColor(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) => "InfoBgSubtle",
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) => "SuccessBgSubtle",
        _ => UnknownBg,
    };

    /// <summary>The user-facing label for a provenance value.</summary>
    public static string Label(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) => "Windows",
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) => "Known app",
        _ => "Not recognised"
    };

    /// <summary>The chip tooltip — says what the label means and whether ending it is safe.</summary>
    public static string Tooltip(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) =>
            "Ships with Windows. Ending it will not crash your PC, but a feature may stop working " +
            "until you sign out or restart.",
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) =>
            "A well-known application. Safe to end, though you may lose unsaved work.",
        _ =>
            "Not in the built-in database — that on its own does not make it harmful, only unrecognised. " +
            "Use Open to see where it runs from before ending it."
    };

    private static bool Is(string? value, string name)
        => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Maps <see cref="Models.SignatureTrust"/> to the colour of the pill's text and dot, for <see cref="ThemeBrush"/>.
/// </summary>
public sealed class SignatureTrustToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => SignatureTrustPalette.TextColor(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps <see cref="Models.SignatureTrust"/> to the pill's tint, for <see cref="ThemeBrush"/>.</summary>
public sealed class SignatureTrustToBackgroundColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => SignatureTrustPalette.BackgroundColor(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="Models.SignatureTrust"/> to the words on the pill. The enum names are
/// developer-facing; "Verified" / "Unsigned" / "Check failed" say what the value means to someone
/// deciding whether to turn a startup entry off.
/// </summary>
public sealed class SignatureTrustToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => SignatureTrustPalette.Label(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The colour and wording decisions for signature trust, in one place so the three converters cannot
/// drift apart, and so the mapping is unit-testable without a WPF Application.
/// </summary>
/// <remarks>
/// Shaped after <see cref="ProcessSafetyPalette"/> rather than invented: the same theme brushes and fixed
/// colours, and the same reasoning about neutrality.
/// </remarks>
public static class SignatureTrustPalette
{
    // The same "no emphasis" pair the process chip uses for an unrecognised program, so an unsigned
    // startup entry looks like ordinary de-emphasised text rather than a bespoke grey.
    private const string NeutralText = "#7B8396";
    private const string NeutralBg = "#207B8396";

    /// <summary>
    /// The colour of the pill's text and dot, for <see cref="ThemeBrush"/>: the name of a theme brush, or a fixed
    /// "#" colour for a value that is the same on every theme.
    /// </summary>
    /// <remarks>
    /// <b>Unsigned is grey, not amber</b>, for the reason <see cref="ProcessSafetyPalette"/> gives about
    /// unrecognised processes: most startup entries on an ordinary machine are unsigned, so an amber row
    /// for each of them would make the whole column read as a list of problems and teach the user to stop
    /// looking at it. Grey reads as "nothing to check", which is what it means. Amber is kept for
    /// <see cref="Models.SignatureTrust.Invalid"/> — a file that IS signed and whose signature does not
    /// hold up, which is the one state here worth a second look. Not red: red belongs to actions that
    /// destroy something, and this column destroys nothing.
    /// </remarks>
    public static string TextColor(object? trust) => Trust(trust) switch
    {
        Models.SignatureTrust.Verified => "SuccessText",
        Models.SignatureTrust.Invalid => "WarningText",
        _ => NeutralText,
    };

    /// <summary>The colour of the pill's tint and border, in the same form as <see cref="TextColor"/>.</summary>
    public static string BackgroundColor(object? trust) => Trust(trust) switch
    {
        Models.SignatureTrust.Verified => "SuccessBgSubtle",
        Models.SignatureTrust.Invalid => "WarningBgSubtle",
        _ => NeutralBg,
    };

    /// <summary>The words on the pill. Empty for <see cref="Models.SignatureTrust.Unknown"/>.</summary>
    /// <remarks>
    /// "Check failed" rather than "Invalid": the file may be perfectly fine and the machine's certificate
    /// store out of date, and the column must not accuse. What the user needs to know is that the answer
    /// did not come back, which is what the tooltip then explains.
    /// </remarks>
    public static string Label(object? trust) => Trust(trust) switch
    {
        Models.SignatureTrust.Verified => "Verified",
        Models.SignatureTrust.Unsigned => "Unsigned",
        Models.SignatureTrust.Invalid => "Check failed",
        _ => "",
    };

    // Accepts the enum, its name, or null, because a DataGrid cell can hand over any of the three while a
    // row is being recycled.
    private static Models.SignatureTrust Trust(object? value) => value switch
    {
        Models.SignatureTrust t => t,
        string s when Enum.TryParse<Models.SignatureTrust>(s, ignoreCase: true, out var parsed) => parsed,
        _ => Models.SignatureTrust.Unknown,
    };
}
