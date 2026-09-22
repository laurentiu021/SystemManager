// SysManager · ResourceHistoryTimeAxisTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The time axis never prints a label for a value that cannot be a sample time.
/// </summary>
/// <remarks>
/// Reported externally in #2371, with a screenshot: on a machine with no temperature sensors the Resource
/// History temperature chart showed <c>01-01 00:00</c> repeated across its whole X axis. The guard was
/// <c>v &gt; 0</c>, which admits any positive tick count — and a few ticks is a date in the year 1, which
/// formats as exactly that. With no samples the axis has no range to work from, so it laid its ticks near
/// zero and every one of them rendered the same year-1 string.
/// <para>Hiding the empty chart is the primary fix and lives in the view. This is the second line: whatever
/// range the axis invents, a label that cannot be a real sample time renders as nothing.</para>
/// </remarks>
public class ResourceHistoryTimeAxisTests
{
    [Fact]
    public void AYearOneTick_RendersNothingRatherThanZeroOneZeroOne()
    {
        // The reported symptom, as a value. 1 tick is 0001-01-01 00:00:00, and "01-01 00:00" is what the old
        // expression produced for it — which is why the axis looked populated while holding no data at all.
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(1));
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-621355968000000000)]     // the Unix epoch expressed as a negative offset, if anything got that wrong
    public void AZeroOrNegativeTick_RendersNothing(double ticks)
    {
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(ticks));
    }

    [Fact]
    public void AnythingBefore2000_RendersNothing()
    {
        // The floor is a plausibility check, not a calendar boundary: no sample can predate the app, so
        // anything from the last century is a degenerate axis value rather than a real time.
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(new DateTime(1999, 12, 31, 23, 59, 59).Ticks));
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(new DateTime(1970, 1, 1).Ticks));
    }

    [Fact]
    public void ARealSampleTime_StillRenders()
    {
        // The other half. Tightening the guard must not stop the axis labelling actual data — which is the
        // way a fix for this could silently make the chart worse rather than better.
        var when = new DateTime(2026, 9, 21, 14, 5, 0);

        Assert.Equal("09-21 14:05", ResourceHistoryViewModel.TimeAxisLabel(when.Ticks));
    }

    [Fact]
    public void TheBoundaryItselfRenders()
    {
        // Inclusive at the floor, so the one instant the rule names is labelled rather than dropped.
        Assert.Equal("01-01 00:00", ResourceHistoryViewModel.TimeAxisLabel(new DateTime(2000, 1, 1).Ticks));
    }

    [Fact]
    public void MaxValue_RendersNothing()
    {
        // Kept from the original guard: DateTime.MaxValue.Ticks itself is out of range for the constructor's
        // exclusive upper bound, and a chart asked for an enormous range must not throw from a labeller.
        Assert.Equal("", ResourceHistoryViewModel.TimeAxisLabel(DateTime.MaxValue.Ticks));
        Assert.Null(Record.Exception(() => ResourceHistoryViewModel.TimeAxisLabel(double.MaxValue)));
    }
}
