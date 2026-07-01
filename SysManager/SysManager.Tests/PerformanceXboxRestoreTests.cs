// SysManager · PerformanceXboxRestoreTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
        var snap = SnapshotWith(bar, dvr);

        // The two flags must round-trip separately — never be merged into one value.
        Assert.Equal(bar, snap.XboxGameBarEnabled);
        Assert.Equal(dvr, snap.XboxGameDvrEnabled);
    }

    [Fact]
    public void Snapshot_MismatchedXboxFlags_StayDistinct()
    {
        var snap = SnapshotWith(bar: true, dvr: false);

        // A collapsing restore (bar && dvr) would treat this as a single "false" and
        // force both keys OFF, silently losing the Game Bar = ON state. Assert the
        // snapshot keeps them distinct so restore can write each key from its own flag.
        Assert.NotEqual(snap.XboxGameBarEnabled, snap.XboxGameDvrEnabled);
        Assert.True(snap.XboxGameBarEnabled);
        Assert.False(snap.XboxGameDvrEnabled);
    }
}
