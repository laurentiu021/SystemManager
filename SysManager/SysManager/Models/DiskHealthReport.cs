// SysManager · DiskHealthReport
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.Helpers;

namespace SysManager.Models;

/// <summary>
/// Friendly, per-disk health report derived from Windows Storage reliability
/// counters (SMART-sourced). All fields nullable because not every driver
/// exposes every counter.
/// </summary>
public sealed partial class DiskHealthReport : ObservableObject
{
    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private string _mediaType = "";       // HDD / SSD / NVMe
    [ObservableProperty] private string _busType = "";
    [ObservableProperty] private double _sizeGB;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(HealthPercentColorHex))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthGauge))]
    private string _healthStatus = "";    // Healthy / Warning / Unhealthy
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(HealthPercentColorHex))]
    [NotifyPropertyChangedFor(nameof(TemperatureColorHex))]
    [NotifyPropertyChangedFor(nameof(TemperatureGauge))]
    [NotifyPropertyChangedFor(nameof(TemperatureDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthGauge))]
    private double? _temperatureC;

    [ObservableProperty] private double? _temperatureMaxC;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(HealthPercentColorHex))]
    [NotifyPropertyChangedFor(nameof(WearGauge))]
    [NotifyPropertyChangedFor(nameof(WearColorHex))]
    [NotifyPropertyChangedFor(nameof(WearDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthGauge))]
    private int? _wearPercent;            // 0 = new, 100 = worn out (SSD only)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerOnDisplay))]
    private long? _powerOnHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(HealthPercentColorHex))]
    [NotifyPropertyChangedFor(nameof(ReadErrorsDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthGauge))]
    private long? _readErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthPercent))]
    [NotifyPropertyChangedFor(nameof(HealthPercentColorHex))]
    [NotifyPropertyChangedFor(nameof(WriteErrorsDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthDisplay))]
    [NotifyPropertyChangedFor(nameof(HealthGauge))]
    private long? _writeErrors;
    [ObservableProperty] private long? _startStopCount;
    [ObservableProperty] private string _verdict = "";         // plain-English summary
    [ObservableProperty] private string _verdictColorHex = StatusColors.Neutral;

    /// <summary>
    /// Overall health score 0–100 (100 = perfect). Computed from wear, temperature,
    /// and error counts. Returns null when no SMART data is available.
    /// </summary>
    public int? HealthPercent
    {
        get
        {
            int score = 100;

            if (WearPercent.HasValue)
                score -= Math.Clamp(WearPercent.Value, 0, 100);

            if (TemperatureC.HasValue)
            {
                if (TemperatureC.Value > 70) score -= 30;
                else if (TemperatureC.Value > 60) score -= 15;
                else if (TemperatureC.Value > 50) score -= 5;
            }

            if (ReadErrors is > 0) score -= (int)Math.Min(ReadErrors.Value * 5L, 20L);
            if (WriteErrors is > 0) score -= (int)Math.Min(WriteErrors.Value * 5L, 20L);

            // Windows' own verdict is a CEILING, not a tie-break. A drive it has flagged as failing
            // cannot report a healthy score just because wear and temperature happen to look fine —
            // HealthStatus covers conditions SMART counters do not (predictive-failure flags,
            // reallocated-sector thresholds, driver-reported media errors). Before this, the gauge
            // showed 100% on a drive whose own verdict line read "Drive is failing".
            int? ceiling = StatusCeiling(HealthStatus);

            if (!WearPercent.HasValue && !TemperatureC.HasValue && ReadErrors is null && WriteErrors is null)
                return ceiling;

            return Math.Clamp(ceiling is { } cap ? Math.Min(score, cap) : score, 0, 100);
        }
    }

    /// <summary>
    /// The highest score a drive may report given Windows' own <c>HealthStatus</c>, or null when the
    /// status is absent or unrecognised (in which case it constrains nothing).
    /// <para>Declared once and used for both the no-SMART fallback and the cap above.
    /// <c>HealthScoreService</c> used to carry a second copy of this table that disagreed on
    /// <c>Warning</c> (50 there, 60 here), which is how a duplicated mapping drifts.</para>
    /// </summary>
    internal static int? StatusCeiling(string? status) => status switch
    {
        "Healthy" => 100,
        "Warning" => 60,
        "Unhealthy" => 20,
        _ => null,
    };

    /// <summary>The health score as the card shows it, or "—" when there is nothing to score it on.</summary>
    /// <remarks>
    /// The card bound <see cref="HealthPercent"/> through <c>StringFormat={}{0}%</c> with a <c>FallbackValue</c> of
    /// "—". A fallback is for a binding that fails, not for a value that is null, so a drive with nothing to score read
    /// a bare "%". The same holds for every figure on the card below.
    /// </remarks>
    public string HealthDisplay => HealthPercent is { } health ? $"{health}%" : "—";

    /// <summary>The health score as the bar's width: an empty bar when there is nothing to score it on.</summary>
    public int HealthGauge => HealthPercent ?? 0;

    /// <summary>Color hex for the health percentage gauge.</summary>
    public string HealthPercentColorHex => HealthPercent switch
    {
        null => StatusColors.Neutral,
        >= 80 => StatusColors.Good,
        >= 50 => StatusColors.Warning,
        >= 20 => StatusColors.Elevated,
        _ => StatusColors.Bad
    };

    /// <summary>Color hex for the temperature reading.</summary>
    public string TemperatureColorHex => TemperatureC switch
    {
        null => StatusColors.Neutral,
        <= 40 => StatusColors.Good,
        <= 50 => StatusColors.Warning,
        <= 60 => StatusColors.Elevated,
        _ => StatusColors.Bad
    };

    /// <summary>The temperature as the card shows it, or "—" when the drive does not report it.</summary>
    public string TemperatureDisplay => TemperatureC is { } celsius
        ? string.Create(CultureInfo.InvariantCulture, $"{celsius:F0} °C")
        : "—";

    /// <summary>Temperature as a 0–100 gauge value (0 °C = 0, 80 °C = 100).</summary>
    public int TemperatureGauge => TemperatureC.HasValue
        ? Math.Clamp((int)(TemperatureC.Value / 80.0 * 100), 0, 100)
        : 0;

    /// <summary>
    /// Wear as a 0–100 gauge value (inverted: 0 wear = 100% life remaining), and an empty bar when the drive does not
    /// report its wear.
    /// </summary>
    /// <remarks>
    /// Unknown wear used to count as 100, so a drive that reports none, as hard disks and many SSDs do not, read
    /// "LIFE REMAINING 100%" with a full bar: a healthy new drive, from no measurement at all (#2600).
    /// </remarks>
    public int WearGauge => WearPercent.HasValue
        ? Math.Clamp(100 - WearPercent.Value, 0, 100)
        : 0;

    /// <summary>The life remaining as the card shows it, or "—" when the drive does not report its wear.</summary>
    public string WearDisplay => WearPercent.HasValue ? $"{WearGauge}%" : "—";

    /// <summary>Color hex for the wear gauge.</summary>
    public string WearColorHex => WearPercent switch
    {
        null => StatusColors.Neutral,
        <= 20 => StatusColors.Good,
        <= 50 => StatusColors.Warning,
        <= 80 => StatusColors.Elevated,
        _ => StatusColors.Bad
    };

    /// <summary>The read error count as the card shows it, or "—" when the drive does not report it.</summary>
    public string ReadErrorsDisplay => ReadErrors?.ToString(CultureInfo.InvariantCulture) ?? "—";

    /// <summary>The write error count as the card shows it, or "—" when the drive does not report it.</summary>
    public string WriteErrorsDisplay => WriteErrors?.ToString(CultureInfo.InvariantCulture) ?? "—";

    /// <summary>Friendly power-on time display.</summary>
    public string PowerOnDisplay => PowerOnHours switch
    {
        null => "—",
        < 24 => $"{PowerOnHours}h",
        < 8760 => $"{PowerOnHours / 24}d {PowerOnHours % 24}h",
        _ => string.Create(CultureInfo.InvariantCulture, $"{PowerOnHours.Value / 8760.0:F1}y")
    };
}
