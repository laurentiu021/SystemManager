// SysManager · DarkModeSchedule
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Models;

/// <summary>
/// Persisted dark-mode schedule. Times are stored as "HH:mm" strings for a stable,
/// culture-independent JSON shape. The schedule only runs while SysManager (or its
/// tray) is running — it is not a Windows service.
/// </summary>
public sealed class DarkModeSchedule
{
    /// <summary>When the dark theme turns on until the user sets a time.</summary>
    internal const string DefaultDarkStart = "19:00";

    /// <summary>When the light theme turns on until the user sets a time.</summary>
    internal const string DefaultLightStart = "07:00";

    /// <summary>How a time is written in the schedule: hours and minutes, on the 24-hour clock.</summary>
    internal const string TimeFormat = "HH:mm";

    public bool Enabled { get; set; }

    /// <summary>Time the dark theme turns on, "HH:mm".</summary>
    public string DarkStart { get; set; } = DefaultDarkStart;

    /// <summary>Time the light theme turns on, "HH:mm".</summary>
    public string LightStart { get; set; } = DefaultLightStart;

    /// <summary>Also switch the taskbar/Start (system theme), not just apps.</summary>
    public bool ApplyToSystem { get; set; } = true;

    public TimeOnly DarkStartTime => TryReadTime(DarkStart) ?? ReadDefault(DefaultDarkStart);
    public TimeOnly LightStartTime => TryReadTime(LightStart) ?? ReadDefault(DefaultLightStart);

    /// <summary><paramref name="value"/> as the schedule reads a time, or null when it cannot read it.</summary>
    internal static TimeOnly? TryReadTime(string? value) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : null;

    private static TimeOnly ReadDefault(string value) => TimeOnly.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture);
}
