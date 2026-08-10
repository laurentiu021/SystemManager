// SysManager · ValueConverters
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Models;

namespace SysManager.Helpers;

public sealed class OutputKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            OutputKind.Error => "OutErrorBrush",
            OutputKind.Warning => "OutWarnBrush",
            OutputKind.Info => "OutInfoBrush",
            OutputKind.Verbose => "OutVerboseBrush",
            OutputKind.Debug => "OutDebugBrush",
            OutputKind.Progress => "OutProgressBrush",
            _ => "OutOutputBrush"
        };
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.White;
    }

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
/// Converts a status colour to a brush. Accepts either a THEME RESOURCE KEY (the
/// <see cref="StatusColors"/> names — "Success", "Warning", "Danger", "Info", "TextMuted") or a
/// literal hex string like "#4CC9F0".
/// <para>The key form is what makes status colours follow the theme. These values used to be
/// dark-calibrated hex constants that <see cref="Services.ThemeService"/> could not recompute, so
/// verdict text on System Health, Disk Health, the Dashboard health score and Cleanup's SFC/DISM
/// results sat below the AA 4.5:1 contrast floor on every light preset. Resolving a key against
/// the application resources picks up the per-mode brush the theme service already maintains —
/// the same lookup <see cref="OutputKindToBrushConverter"/> uses.</para>
/// <para>Literal hex is still honoured, so a caller with a genuinely fixed colour keeps working.</para>
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    // Cache frozen brushes by hex value to reduce GC pressure on frequently-updating bindings.
    // ONLY literal hex is cached. A theme brush must never be: the cache is static and its brushes
    // are frozen, so caching one would keep serving the colour resolved at first render and the
    // status text would stay dark-themed after a switch to a light preset. A resource lookup is a
    // dictionary hit, so there is nothing to gain by caching it anyway.
    private static readonly ConcurrentDictionary<string, SolidColorBrush> _cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Brushes.Gray;

        // A leading '#' is the only thing that marks a literal colour; anything else is a key.
        if (!s.StartsWith('#'))
        {
            // Unknown key, or no Application at all (unit tests): fall back to Gray rather than
            // throw — a converter exception would blank the verdict text it is meant to colour.
            return Application.Current?.TryFindResource(s) is Brush themed ? themed : Brushes.Gray;
        }

        return _cache.GetOrAdd(s, static hex =>
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                return Brushes.Gray;
            }
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps process status text ("Running" / "Not responding") to a coloured brush.
/// Green for running, red for not responding, grey for anything else.
/// </summary>
public sealed class ProcessStatusToBrushConverter : IValueConverter
{
    private static readonly Brush RunningBrush = CreateFrozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush NotRespondingBrush = CreateFrozen(Color.FromRgb(0xEF, 0x44, 0x44));

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            if (status.Contains("Not responding", StringComparison.OrdinalIgnoreCase))
                return NotRespondingBrush;
            if (status.Contains("Running", StringComparison.OrdinalIgnoreCase))
                return RunningBrush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SafetyLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeBrush = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush CautionBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));

    static SafetyLevelToBrushConverter()
    {
        SafeBrush.Freeze();
        CautionBrush.Freeze();
        CriticalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Resolve the theme-derived *Text brush (recomputed per mode by ThemeService.StatusPalette)
        // so the Safety chip text/dot stays WCAG-AA-legible on light presets, where the old static
        // dark-calibrated pale red/amber washed out on the near-white chip. Fall back to the frozen
        // dark brushes only when there is no live Application (e.g. design-time / unit tests).
        var (key, fallback) = value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => ("SuccessText", SafeBrush),
            SafetyLevel.Caution => ("WarningText", CautionBrush),
            SafetyLevel.Critical => ("DangerText", CriticalBrush),
            _ => ("DangerText", CriticalBrush)
        } : ("DangerText", CriticalBrush);
        return Application.Current?.TryFindResource(key) is Brush b ? b : fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SafetyLevelToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush SafeBg = new(Color.FromArgb(0x20, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush CautionBg = new(Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush CriticalBg = new(Color.FromArgb(0x20, 0xEF, 0x44, 0x44));

    static SafetyLevelToBackgroundConverter()
    {
        SafeBg.Freeze();
        CautionBg.Freeze();
        CriticalBg.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SafetyLevel level ? level switch
        {
            SafetyLevel.Safe => SafeBg,
            SafetyLevel.Caution => CautionBg,
            SafetyLevel.Critical => CriticalBg,
            _ => CriticalBg
        } : CriticalBg;
    }

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
            SafetyLevel.Critical => "Critical",
            _ => "Critical"
        } : "Critical";
    }

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
/// Maps <see cref="SysManager.Services.ProcessSafety"/> (as a string) to the chip's text/dot brush.
/// </summary>
public sealed class ProcessSafetyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.ResolveText(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="SysManager.Services.ProcessSafety"/> (as a string) to the chip's background tint.
/// </summary>
public sealed class ProcessSafetyToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ProcessSafetyPalette.ResolveBackground(value as string);

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
    private static readonly SolidColorBrush UnknownText = Frozen(Color.FromRgb(0x7B, 0x83, 0x96));
    private static readonly SolidColorBrush UnknownBg = Frozen(Color.FromArgb(0x20, 0x7B, 0x83, 0x96));
    private static readonly SolidColorBrush SystemText = Frozen(Color.FromRgb(0x7D, 0xD3, 0xFC));
    private static readonly SolidColorBrush SystemBg = Frozen(Color.FromArgb(0x1A, 0x38, 0xBD, 0xF8));
    private static readonly SolidColorBrush TrustedText = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush TrustedBg = Frozen(Color.FromArgb(0x1A, 0x22, 0xC5, 0x5E));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Theme resource key + hardcoded fallback for the chip's text/dot colour. A null key means the
    /// value is intentionally theme-independent.
    /// </summary>
    /// <remarks>
    /// Unknown is deliberately neutral grey, NOT a warning colour. The database holds 108 entries
    /// while a typical machine runs 200+ processes, so most rows are Unknown; tinting them amber
    /// would make the whole grid look alarming and teach the user to ignore the column. Grey reads
    /// as "no information", which is what it means.
    /// </remarks>
    public static (string? Key, Brush Fallback) TextBrushKey(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) => ("InfoText", SystemText),
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) => ("SuccessText", TrustedText),
        _ => (null, UnknownText)
    };

    /// <summary>Theme resource key + hardcoded fallback for the chip's background tint.</summary>
    public static (string? Key, Brush Fallback) BackgroundBrushKey(string? safety) => safety switch
    {
        var s when Is(s, nameof(Services.ProcessSafety.System)) => ("InfoBgSubtle", SystemBg),
        var s when Is(s, nameof(Services.ProcessSafety.Trusted)) => ("SuccessBgSubtle", TrustedBg),
        _ => (null, UnknownBg)
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

    public static Brush ResolveText(string? safety) => Live(TextBrushKey(safety));

    public static Brush ResolveBackground(string? safety) => Live(BackgroundBrushKey(safety));

    // Prefer the live theme brush (recomputed per preset by ThemeService.StatusPalette) so the chip
    // stays legible on light themes; fall back to the frozen dark values when there is no
    // Application, i.e. design-time and unit tests.
    private static Brush Live((string? Key, Brush Fallback) spec)
        => spec.Key is not null && Application.Current?.TryFindResource(spec.Key) is Brush b
            ? b
            : spec.Fallback;

    private static bool Is(string? value, string name)
        => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
