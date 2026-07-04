// SysManager · HealthScoreResult — aggregated system health score
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Aggregated health score (0–100) combining disk health, RAM usage,
/// uptime, and battery wear. Higher is better.
/// </summary>
public sealed record HealthScoreResult
{
    /// <summary>Overall score 0–100 (100 = perfect health).</summary>
    public int Score { get; init; }

    /// <summary>Color hex for the gauge arc.</summary>
    public string ColorHex => Score switch
    {
        >= 80 => StatusColors.Good,  // green
        >= 50 => StatusColors.Warning,  // amber
        _ => StatusColors.Bad       // red
    };

    /// <summary>Human-readable label for the score.</summary>
    public string Label => Score switch
    {
        >= 90 => "Excellent",
        >= 80 => "Good",
        >= 60 => "Fair",
        >= 40 => "Needs attention",
        _ => "Poor"
    };

    /// <summary>Top recommendations (max 3).</summary>
    public IReadOnlyList<HealthRecommendation> Recommendations { get; init; }
        = [];

    /// <summary>Individual component scores for breakdown display.</summary>
    public int DiskScore { get; init; } = 100;
    public int RamScore { get; init; } = 100;
    public int UptimeScore { get; init; } = 100;
    public int BatteryScore { get; init; } = 100;
    public bool HasBattery { get; init; }
}

/// <summary>A single health recommendation shown below the gauge.</summary>
public sealed record HealthRecommendation
{
    public required string Message { get; init; }
    public required string Severity { get; init; }  // "warning" or "critical"

    public string IconGlyph => Severity == "critical" ? "\uE783" : "\uE7BA";
    public string ColorHex => Severity == "critical" ? StatusColors.Bad : StatusColors.Warning;
}
