// SysManager · FriendlyEventEntryExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Tests;

public class FriendlyEventEntryExtendedTests
{
    [Fact]
    public void PropertyChangedFires_ForAllFields()
    {
        var e = new FriendlyEventEntry();
        var raised = new HashSet<string>();
        ((INotifyPropertyChanged)e).PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName != null) raised.Add(ev.PropertyName);
        };
        // A fixed date, not DateTime.Now: the generated setter skips PropertyChanged when the new
        // value equals the current one, so reading the wall clock made this assertion depend on
        // machine time rather than on a value the test controls.
        e.Timestamp = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        e.LogName = "System";
        e.ProviderName = "x";
        e.EventId = 42;
        e.Severity = EventSeverity.Critical;
        e.SeverityLabel = "Critical";
        e.Message = "m";
        e.FullMessage = "full";
        e.Xml = "<x/>";
        e.MachineName = "pc";
        e.UserName = "u";
        e.RecordId = 99;
        e.Explanation = "exp";
        e.Recommendation = "rec";

        Assert.Contains(nameof(e.Timestamp), raised);
        Assert.Contains(nameof(e.LogName), raised);
        Assert.Contains(nameof(e.ProviderName), raised);
        Assert.Contains(nameof(e.EventId), raised);
        Assert.Contains(nameof(e.Severity), raised);
        Assert.Contains(nameof(e.SeverityLabel), raised);
        Assert.Contains(nameof(e.Message), raised);
        Assert.Contains(nameof(e.FullMessage), raised);
        Assert.Contains(nameof(e.Xml), raised);
        Assert.Contains(nameof(e.MachineName), raised);
        Assert.Contains(nameof(e.UserName), raised);
        Assert.Contains(nameof(e.RecordId), raised);
        Assert.Contains(nameof(e.Explanation), raised);
        Assert.Contains(nameof(e.Recommendation), raised);
    }

    [Fact]
    public void SeverityIcon_UnknownSeverity_GivesFallback()
    {
        var e = new FriendlyEventEntry { Severity = (EventSeverity)999 };
        Assert.Equal("•", e.SeverityIcon);
    }

    [Fact]
    public void SeverityColor_UnknownSeverity_GivesFallback()
    {
        var e = new FriendlyEventEntry { Severity = (EventSeverity)999 };
        Assert.Equal(StatusColors.Neutral, e.SeverityColor);
    }

    /// <summary>
    /// The extreme timestamps a real event log can hand us must not produce nonsense in the two strings
    /// the user actually sees.
    /// <para>Replaces an assertion that stored <c>DateTime.MinValue</c>/<c>MaxValue</c> and read them back
    /// — a round-trip through a generated setter, proving only that a <c>DateTime</c> field holds a
    /// <c>DateTime</c>. The reachable question is what <c>RelativeTime</c> and <c>FullTimestamp</c> DO with
    /// those values, since both are computed from <c>Timestamp</c> and <c>RelativeTime</c> subtracts it
    /// from <c>DateTime.Now</c>.</para>
    /// <para>This pins the behaviour rather than changing it: MinValue is special-cased to an em dash, and
    /// a FUTURE stamp (a clock correction, or a log copied from a machine running ahead) yields "just now",
    /// because the negative span falls into the first branch. Imprecise but harmless — and far better than
    /// printing "in -3d". Asserted so a future reordering of those branches cannot silently start showing
    /// the user a negative duration.</para>
    /// </summary>
    [Fact]
    public void ExtremeTimestamps_ProduceSaneDisplayStrings()
    {
        var min = new FriendlyEventEntry { Timestamp = DateTime.MinValue };
        Assert.Equal("—", min.RelativeTime);
        Assert.Equal("0001-01-01 00:00:00", min.FullTimestamp);

        var future = new FriendlyEventEntry { Timestamp = DateTime.MaxValue };
        Assert.Equal("just now", future.RelativeTime);
        Assert.Equal("9999-12-31 23:59:59", future.FullTimestamp);
        // The point of the assertion: no negative duration ever reaches the user.
        Assert.DoesNotContain("-", future.RelativeTime);
    }

    [Fact]
    public void RecordId_VeryLarge_IsStored()
    {
        var e = new FriendlyEventEntry { RecordId = long.MaxValue };
        Assert.Equal(long.MaxValue, e.RecordId);
    }

    [Fact]
    public void MachineAndUser_AreOptional()
    {
        var e = new FriendlyEventEntry();
        Assert.Null(e.MachineName);
        Assert.Null(e.UserName);
    }
}
