// SysManager · FriendlyEventEntryExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
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
        e.Timestamp = DateTime.Now;
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
        Assert.Equal("#9AA0A6", e.SeverityColor);
    }

    [Fact]
    public void TimestampsVastlyDifferent_StillStorable()
    {
        var e = new FriendlyEventEntry { Timestamp = DateTime.MinValue };
        Assert.Equal(DateTime.MinValue, e.Timestamp);
        e.Timestamp = DateTime.MaxValue;
        Assert.Equal(DateTime.MaxValue, e.Timestamp);
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
