// SysManager · ThemeService — runtime theme switching with persistence
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Serilog;

namespace SysManager.Services;

public sealed class ThemeService
{
    private static ThemeService? _instance;
    public static ThemeService Instance => _instance ??= new ThemeService();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SysManager", "theme.json");

    public event Action? ThemeChanged;

    public ThemePreset CurrentTheme { get; private set; } = ThemePreset.Defaults["midnight-indigo"];
    public string CurrentPresetId { get; private set; } = "midnight-indigo";
    public string CurrentMode { get; private set; } = "dark";
    public double ShadePosition { get; private set; } = 0.5;

    private ThemePreset _baseTheme = ThemePreset.Defaults["midnight-indigo"];

    private static readonly Dictionary<string, string> DarkToLight = new()
    {
        ["midnight-indigo"] = "clean-indigo",
        ["deep-ocean"] = "sky-breeze",
        ["dark-forest"] = "mint-fresh",
        ["neon-rose"] = "soft-blossom",
        ["violet-night"] = "lavender",
        ["warm-ember"] = "warm-sand",
    };

    private static readonly Dictionary<string, string> LightToDark =
        DarkToLight.ToDictionary(kv => kv.Value, kv => kv.Key);

    private ThemeService() { }

    public void Initialize()
    {
        Load();
        Apply(CurrentTheme);
    }

    public string GetCompanionPreset(string targetMode)
    {
        if (targetMode == "dark" && LightToDark.TryGetValue(CurrentPresetId, out var darkId))
            return darkId;
        if (targetMode == "light" && DarkToLight.TryGetValue(CurrentPresetId, out var lightId))
            return lightId;
        return targetMode == "dark" ? "midnight-indigo" : "clean-indigo";
    }

    public void SetPreset(string id)
    {
        if (!ThemePreset.Defaults.TryGetValue(id, out var preset)) return;
        CurrentPresetId = id;
        _baseTheme = preset;
        CurrentMode = preset.IsDark ? "dark" : "light";
        ApplyShade();
        Save();
    }

    public void SetAccent(Color accent)
    {
        _baseTheme = _baseTheme with { Accent = accent };
        ApplyShade();
        Save();
    }

    public void SetShade(double position)
    {
        ShadePosition = Math.Clamp(position, 0, 1);
        ApplyShade();
        Save();
    }

    public void SetCustom(Color accent, Color background, Color surface, Color text)
    {
        CurrentMode = "custom";
        CurrentPresetId = "custom";
        _baseTheme = new ThemePreset(
            Id: "custom",
            Name: "Custom",
            IsDark: background.R + background.G + background.B < 384,
            Accent: accent,
            Background: background,
            Surface: surface,
            Surface2: Lerp(surface, background, 0.5),
            Border: Lerp(surface, text, 0.15),
            TextPrimary: text,
            TextSecondary: Lerp(text, background, 0.3),
            TextMuted: Lerp(text, background, 0.55));
        CurrentTheme = new ThemePreset(
            Id: "custom",
            Name: "Custom",
            IsDark: background.R + background.G + background.B < 384,
            Accent: accent,
            Background: background,
            Surface: surface,
            Surface2: Lerp(surface, background, 0.5),
            Border: Lerp(surface, text, 0.15),
            TextPrimary: text,
            TextSecondary: Lerp(text, background, 0.3),
            TextMuted: Lerp(text, background, 0.55));
        Apply(CurrentTheme);
        Save();
    }

    private void ApplyShade()
    {
        var t = _baseTheme;
        var offset = (ShadePosition - 0.5) * 0.12;

        CurrentTheme = t with
        {
            Background = ShiftLightness(t.Background, offset),
            Surface = ShiftLightness(t.Surface, offset),
            Surface2 = ShiftLightness(t.Surface2, offset),
            Border = ShiftLightness(t.Border, offset)
        };
        Apply(CurrentTheme);
    }

    public void Apply(ThemePreset theme)
    {
        var res = Application.Current.Resources;
        SetBrush(res, "Surface0", theme.Background);
        SetBrush(res, "Surface1", theme.Surface);
        SetBrush(res, "Surface2", theme.Surface2);
        SetBrush(res, "Surface3", Lerp(theme.Surface2, theme.TextPrimary, 0.05));
        SetBrush(res, "Surface4", Lerp(theme.Surface2, theme.TextPrimary, 0.1));
        SetBrush(res, "Border1", theme.Border);
        SetBrush(res, "Border2", Lerp(theme.Border, theme.TextPrimary, 0.08));
        SetBrush(res, "BorderAccent", Lerp(theme.Border, theme.Accent, 0.2));
        SetBrush(res, "TextPrimary", theme.TextPrimary);
        SetBrush(res, "TextSecondary", theme.TextSecondary);
        SetBrush(res, "TextMuted", theme.TextMuted);
        SetBrush(res, "TextDisabled", Lerp(theme.TextMuted, theme.Background, 0.4));
        SetBrush(res, "Accent", theme.Accent);
        SetBrush(res, "AccentHover", Lighten(theme.Accent, 0.15));
        SetBrush(res, "AccentPressed", Darken(theme.Accent, 0.12));
        SetBrush(res, "AccentSoft", Color.FromArgb(24, theme.Accent.R, theme.Accent.G, theme.Accent.B));

        SetColor(res, "Surface0Color", theme.Background);
        SetColor(res, "Surface1Color", theme.Surface);
        SetColor(res, "Surface2Color", theme.Surface2);
        SetColor(res, "Surface3Color", Lerp(theme.Surface2, theme.TextPrimary, 0.05));
        SetColor(res, "Surface4Color", Lerp(theme.Surface2, theme.TextPrimary, 0.1));
        SetColor(res, "AccentColor", theme.Accent);
        SetColor(res, "AccentHoverColor", Lighten(theme.Accent, 0.15));
        SetColor(res, "AccentPressedColor", Darken(theme.Accent, 0.12));

        ThemeChanged?.Invoke();
    }

    private static void SetBrush(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    private static void SetColor(ResourceDictionary res, string key, Color color)
    {
        res[key] = color;
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color Lighten(Color c, double amount)
    {
        return Color.FromArgb(c.A,
            (byte)Math.Min(255, c.R + (255 - c.R) * amount),
            (byte)Math.Min(255, c.G + (255 - c.G) * amount),
            (byte)Math.Min(255, c.B + (255 - c.B) * amount));
    }

    private static Color Darken(Color c, double amount)
    {
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - amount)),
            (byte)(c.G * (1 - amount)),
            (byte)(c.B * (1 - amount)));
    }

    private static Color ShiftLightness(Color c, double amount)
    {
        if (amount >= 0)
            return Lighten(c, amount);
        return Darken(c, -amount);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var data = new ThemeSettings(CurrentPresetId, CurrentMode, ShadePosition,
                CurrentTheme.Accent.ToString(), CurrentTheme.Background.ToString(),
                CurrentTheme.Surface.ToString(), CurrentTheme.TextPrimary.ToString());
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { Log.Debug("Theme save failed: {Error}", ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var data = JsonSerializer.Deserialize<ThemeSettings>(json);
            if (data is null) return;

            CurrentMode = data.Mode;
            ShadePosition = data.ShadePosition;
            CurrentPresetId = data.PresetId;

            if (data.PresetId == "custom")
            {
                var accent = (Color)ColorConverter.ConvertFromString(data.Accent);
                var bg = (Color)ColorConverter.ConvertFromString(data.Background);
                var surface = (Color)ColorConverter.ConvertFromString(data.Surface);
                var text = (Color)ColorConverter.ConvertFromString(data.Text);
                SetCustom(accent, bg, surface, text);
            }
            else if (ThemePreset.Defaults.TryGetValue(data.PresetId, out var preset))
            {
                _baseTheme = preset;
                CurrentTheme = preset;
                ApplyShade();
            }
        }
        catch (Exception ex) { Log.Debug("Theme load failed: {Error}", ex.Message); }
    }

    private sealed record ThemeSettings(
        string PresetId, string Mode, double ShadePosition,
        string Accent, string Background, string Surface, string Text);
}

public sealed record ThemePreset(
    string Id,
    string Name,
    bool IsDark,
    Color Accent,
    Color Background,
    Color Surface,
    Color Surface2,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted)
{
    public static readonly Dictionary<string, ThemePreset> Defaults = new()
    {
        ["midnight-indigo"] = new("midnight-indigo", "Midnight Indigo", true,
            C("#6366F1"), C("#070A0F"), C("#0E1218"), C("#151A23"), C("#1F2633"),
            C("#F1F3F7"), C("#A3ADBF"), C("#6B7489")),
        ["deep-ocean"] = new("deep-ocean", "Deep Ocean", true,
            C("#3B82F6"), C("#050D1A"), C("#0A1628"), C("#0F1D33"), C("#1A2D4D"),
            C("#E2E8F0"), C("#94A3B8"), C("#64748B")),
        ["dark-forest"] = new("dark-forest", "Dark Forest", true,
            C("#10B981"), C("#020F0A"), C("#021A12"), C("#03261A"), C("#0A3D2A"),
            C("#D1FAE5"), C("#6EE7B7"), C("#34D399")),
        ["neon-rose"] = new("neon-rose", "Neon Rose", true,
            C("#EC4899"), C("#120508"), C("#1A0A0F"), C("#240E16"), C("#3D1525"),
            C("#FDF2F8"), C("#F9A8D4"), C("#F472B6")),
        ["violet-night"] = new("violet-night", "Violet Night", true,
            C("#A855F7"), C("#0A0515"), C("#0F0A1A"), C("#160F26"), C("#2D1B4E"),
            C("#F3E8FF"), C("#C4B5FD"), C("#8B5CF6")),
        ["warm-ember"] = new("warm-ember", "Warm Ember", true,
            C("#F59E0B"), C("#0F0A04"), C("#1A1008"), C("#24180C"), C("#3D2A12"),
            C("#FEF3C7"), C("#FCD34D"), C("#FBBF24")),
        ["clean-indigo"] = new("clean-indigo", "Clean Indigo", false,
            C("#6366F1"), C("#FFFFFF"), C("#F8FAFC"), C("#F1F5F9"), C("#E2E8F0"),
            C("#1E1B4B"), C("#4338CA"), C("#6366F1")),
        ["sky-breeze"] = new("sky-breeze", "Sky Breeze", false,
            C("#0EA5E9"), C("#F8FAFC"), C("#F0F9FF"), C("#E0F2FE"), C("#BAE6FD"),
            C("#0C4A6E"), C("#0369A1"), C("#0284C7")),
        ["warm-sand"] = new("warm-sand", "Warm Sand", false,
            C("#D97706"), C("#FFFBEB"), C("#FEF3C7"), C("#FDE68A"), C("#FCD34D"),
            C("#451A03"), C("#78350F"), C("#92400E")),
        ["mint-fresh"] = new("mint-fresh", "Mint Fresh", false,
            C("#16A34A"), C("#F0FDF4"), C("#DCFCE7"), C("#BBF7D0"), C("#86EFAC"),
            C("#14532D"), C("#166534"), C("#15803D")),
        ["soft-blossom"] = new("soft-blossom", "Soft Blossom", false,
            C("#DB2777"), C("#FDF2F8"), C("#FCE7F3"), C("#FBCFE8"), C("#F9A8D4"),
            C("#500724"), C("#831843"), C("#9D174D")),
        ["lavender"] = new("lavender", "Lavender", false,
            C("#7C3AED"), C("#FAF5FF"), C("#F3E8FF"), C("#E9D5FF"), C("#D8B4FE"),
            C("#2E1065"), C("#4C1D95"), C("#5B21B6")),
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
