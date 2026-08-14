// SysManager · PerformanceXboxRestoreTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.Json;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression guard for the Xbox Game Bar restore bug: snapshot restore used to
/// collapse the two INDEPENDENT registry values — AppCaptureEnabled (Game Bar
/// overlay) and GameDVR_Enabled (per-game DVR) — into a single value via
/// <c>bar &amp;&amp; dvr</c>, which left one key in the wrong state on restore whenever
/// the user had them set differently (e.g. Bar ON / DVR OFF).
///
/// The registry writes themselves are system-level (integration), but the bug was
/// in the value MAPPING, which is pure: the snapshot must carry both flags
/// independently and restore must feed each to its own key. These tests pin that
/// the snapshot preserves the two flags separately for every combination — a
/// re-introduction of a single collapsed bool would break the round-trip.
/// </summary>
public class PerformanceXboxRestoreTests
{
    private static PerformanceService.OriginalSnapshot SnapshotWith(bool bar, bool dvr) =>
        new(
            PowerPlanGuid: "scheme",
            PowerPlanName: "Balanced",
            UiEffectsEnabled: true,
            GameModeEnabled: true,
            XboxGameBarEnabled: bar,
            XboxGameDvrEnabled: dvr,
            GpuDynamicPstate: false,
            ProcessorMinPercentAc: 5,
            NvidiaSubKey: null);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]   // the bug scenario: Game Bar ON, per-game DVR OFF
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Snapshot_PreservesBothXboxFlagsIndependently(bool bar, bool dvr)
    {
        // Through the JSON the snapshot is actually PERSISTED as, not straight back out of the record.
        // Reading positional-record properties back asserts the compiler's auto-property storage, so it
        // cannot fail at runtime — the previous version of this test would have stayed green if the two
        // flags were collapsed into one, which is precisely the regression the class doc claims to pin.
        // The snapshot survives an app restart via SaveSnapshot/LoadSnapshot, so the serialized shape is
        // the real contract: a field dropped or merged there loses the user's state for good.
        var restored = RoundTrip(SnapshotWith(bar, dvr));

        Assert.Equal(bar, restored.XboxGameBarEnabled);
        Assert.Equal(dvr, restored.XboxGameDvrEnabled);
    }

    [Fact]
    public void Snapshot_MismatchedXboxFlags_SurviveSeparately()
    {
        // The bug scenario end to end: Game Bar ON, per-game DVR OFF. A collapsing restore
        // (bar && dvr) treats this as a single "false" and forces both keys OFF, silently losing the
        // Game Bar = ON state. Asserting after a real serialize/deserialize means a merged or missing
        // field fails here instead of passing on record storage.
        var restored = RoundTrip(SnapshotWith(bar: true, dvr: false));

        Assert.True(restored.XboxGameBarEnabled);
        Assert.False(restored.XboxGameDvrEnabled);
    }

    /// <summary>
    /// Serializes and deserializes the snapshot the way <c>SaveSnapshot</c>/<c>LoadSnapshot</c> do.
    /// <para>Both properties carry <c>[property: JsonRequired]</c>, so a field removed from the record
    /// makes the deserialize throw rather than silently yielding <c>false</c> — the assertions above
    /// therefore fail loudly rather than passing on a default.</para>
    /// </summary>
    private static PerformanceService.OriginalSnapshot RoundTrip(
        PerformanceService.OriginalSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot);
        return JsonSerializer.Deserialize<PerformanceService.OriginalSnapshot>(json)
               ?? throw new InvalidOperationException("The snapshot did not survive the round-trip.");
    }
}
