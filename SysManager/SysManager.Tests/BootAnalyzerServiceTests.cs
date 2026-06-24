// SysManager · BootAnalyzerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Xml.Linq;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BootAnalyzerService"/>'s pure event-XML parsing. Synthetic
/// Diagnostics-Performance event payloads are fed in directly, so no live event log (or
/// admin) is needed. The live reader path is not unit-tested.
/// </summary>
public class BootAnalyzerServiceTests
{
    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static XElement Event(int eventId, params (string Name, string Value)[] data)
    {
        XNamespace ns = Ns;
        var ev = new XElement(ns + "Event",
            new XElement(ns + "System", new XElement(ns + "EventID", eventId)));
        var dataEl = new XElement(ns + "EventData");
        foreach (var (name, value) in data)
            dataEl.Add(new XElement(ns + "Data", new XAttribute("Name", name), value));
        ev.Add(dataEl);
        return ev;
    }

    // ---------- DataValue ----------

    [Fact]
    public void DataValue_ReadsNamedData()
    {
        var ev = Event(100, ("BootTime", "42000"), ("MainPathBootTime", "30000"));
        Assert.Equal("42000", BootAnalyzerService.DataValue(ev, "BootTime"));
        Assert.Equal("30000", BootAnalyzerService.DataValue(ev, "MainPathBootTime"));
        Assert.Null(BootAnalyzerService.DataValue(ev, "Missing"));
    }

    [Fact]
    public void DataValue_NullXml_ReturnsNull()
        => Assert.Null(BootAnalyzerService.DataValue(null, "BootTime"));

    // ---------- KindForEventId ----------

    [Theory]
    [InlineData(101, "Application")]
    [InlineData(102, "Driver")]
    [InlineData(103, "Service")]
    [InlineData(104, "Device")]
    [InlineData(109, "Background")]
    [InlineData(999, "Component")]
    public void KindForEventId_MapsKnownIds(int id, string expected)
        => Assert.Equal(expected, BootAnalyzerService.KindForEventId(id));

    // ---------- ParseBoot ----------

    [Fact]
    public void ParseBoot_ReadsDurations()
    {
        var when = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Local);
        var ev = Event(100, ("BootTime", "42000"), ("MainPathBootTime", "30000"), ("BootPostBootTime", "12000"));
        var b = BootAnalyzerService.ParseBoot(when, ev);

        Assert.NotNull(b);
        Assert.Equal(42000, b!.BootTimeMs);
        Assert.Equal(30000, b.MainPathBootTimeMs);
        Assert.Equal(12000, b.PostBootTimeMs);
        Assert.Equal("42.0 s", b.BootSecondsDisplay);
    }

    [Fact]
    public void ParseBoot_NoBootTime_ReturnsNull()
        => Assert.Null(BootAnalyzerService.ParseBoot(DateTime.Now, Event(100, ("MainPathBootTime", "30000"))));

    // ---------- ParseDegradation ----------

    [Fact]
    public void ParseDegradation_ReadsNameAndTime()
    {
        var when = new DateTime(2026, 6, 24, 8, 0, 0, DateTimeKind.Local);
        var ev = Event(102, ("Name", "slowdriver.sys"), ("TotalTime", "5200"));
        var d = BootAnalyzerService.ParseDegradation(when, 102, ev);

        Assert.NotNull(d);
        Assert.Equal("Driver", d!.Kind);
        Assert.Equal("slowdriver.sys", d.Name);
        Assert.Equal(5200, d.DurationMs);
        Assert.Equal("5.2 s", d.DurationDisplay);
    }

    [Fact]
    public void ParseDegradation_FallsBackToFriendlyName()
    {
        var ev = Event(101, ("FriendlyName", "Some App"), ("Time", "800"));
        var d = BootAnalyzerService.ParseDegradation(DateTime.Now, 101, ev);
        Assert.NotNull(d);
        Assert.Equal("Some App", d!.Name);
        Assert.Equal(800, d.DurationMs);
        Assert.Equal("800 ms", d.DurationDisplay);
    }

    [Fact]
    public void ParseDegradation_NoName_ReturnsNull()
        => Assert.Null(BootAnalyzerService.ParseDegradation(DateTime.Now, 101, Event(101, ("TotalTime", "500"))));
}
