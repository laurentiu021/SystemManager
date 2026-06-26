// SysManager · DarkModeSchedule
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Persisted dark-mode schedule. Times are stored as "HH:mm" strings for a stable,
/// culture-independent JSON shape. The schedule only runs while SysManager (or its
/// tray) is running — it is not a Windows service.
/// </summary>
public sealed class DarkModeSchedule
{
    public bool Enabled { get; set; }

    /// <summary>Time the dark theme turns on, "HH:mm".</summary>
    public string DarkStart { get; set; } = "19:00";

    /// <summary>Time the light theme turns on, "HH:mm".</summary>
    public string LightStart { get; set; } = "07:00";

    /// <summary>Also switch the taskbar/Start (system theme), not just apps.</summary>
    public bool ApplyToSystem { get; set; } = true;

    public TimeOnly DarkStartTime => ParseOrDefault(DarkStart, new TimeOnly(19, 0));
    public TimeOnly LightStartTime => ParseOrDefault(LightStart, new TimeOnly(7, 0));

    private static TimeOnly ParseOrDefault(string value, TimeOnly fallback)
        => TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : fallback;
}
