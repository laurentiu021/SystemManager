// SysManager · BootAnalyzerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Xml.Linq;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BootAnalyzerService"/>'s pure event-XML parsing. Synthetic
/// Diagnostics-Performance event payloads are fed in directly, so no live event log (or
/// admin) is needed. The read loop around them is driven through the reader seam in
/// <see cref="BootAnalyzerReadTests"/>.
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

    // Windows emits event 100 in the <EventData><Data Name=...> form (verified against a live
    // event), which the tests above cover. These two also exercise the defensive fallback for
    // a hypothetical variant that nests the fields as directly-named child elements under a
    // <UserData> block, so DataValue stays robust if a build ever uses that shape.
    private const string ProviderNs = "http://manifests.microsoft.com/win/2004/08/windows/diagnosis";

    private static XElement UserDataEvent(int eventId, params (string Name, string Value)[] fields)
    {
        XNamespace ns = Ns;
        XNamespace pn = ProviderNs;
        var inner = new XElement(pn + "EventXML");
        foreach (var (name, value) in fields)
            inner.Add(new XElement(pn + name, value));
        return new XElement(ns + "Event",
            new XElement(ns + "System", new XElement(ns + "EventID", eventId)),
            new XElement(ns + "UserData", inner));
    }

    [Fact]
    public void DataValue_ReadsUserDataNamedChildren()
    {
        var ev = UserDataEvent(100, ("BootTime", "55000"), ("MainPathBootTime", "40000"), ("BootPostBootTime", "15000"));
        Assert.Equal("55000", BootAnalyzerService.DataValue(ev, "BootTime"));
        Assert.Equal("40000", BootAnalyzerService.DataValue(ev, "MainPathBootTime"));
        Assert.Null(BootAnalyzerService.DataValue(ev, "Missing"));
    }

    [Fact]
    public void ParseBoot_ReadsUserDataShape()
    {
        var when = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Local);
        var ev = UserDataEvent(100, ("BootTime", "55000"), ("MainPathBootTime", "40000"), ("BootPostBootTime", "15000"));
        var b = BootAnalyzerService.ParseBoot(when, ev);

        Assert.NotNull(b);
        Assert.Equal(55000, b!.BootTimeMs);
        Assert.Equal(40000, b.MainPathBootTimeMs);
        Assert.Equal(15000, b.PostBootTimeMs);
    }

    // ---------- KindForEventId ----------

    /// <summary>
    /// Each event ID gets the kind the provider's own message gives it, quoted beside it (#2770). They were mapped in
    /// pairs, so 104 to 110 all read as something else. Session manager initialization counts as the core system: the
    /// Session Manager is the first user-mode process Windows starts.
    /// </summary>
    [Theory]
    [InlineData(101, "Application")]          // "This application took longer than usual to start up"
    [InlineData(102, "Driver")]               // "This driver took longer to initialize"
    [InlineData(103, "Service")]              // "This startup service took longer than expected to startup"
    [InlineData(104, "Core system")]          // "Core system took longer to initialize"
    [InlineData(105, "Prefetching")]          // "Foreground optimizations (prefetching) took longer to complete"
    [InlineData(106, "Prefetching")]          // "Background optimizations (prefetching) took longer to complete"
    [InlineData(107, "Group Policy")]         // "Application of machine policy caused a slow down"
    [InlineData(108, "Group Policy")]         // "Application of user policy caused a slow down"
    [InlineData(109, "Device")]               // "This device took longer to initialize"
    [InlineData(110, "Core system")]          // "Session manager initialization caused a slow down"
    [InlineData(999, "Component")]
    public void KindForEventId_IsWhatTheProvidersMessageSays(int id, string expected)
        => Assert.Equal(expected, BootAnalyzerService.KindForEventId(id));

    /// <summary>
    /// The delay is the event's DegradationTime, what the component added, not its TotalTime (#2770). Session manager
    /// start-up, measured at a TotalTime of 10,453 ms and a DegradationTime of 453 ms, read as 10.5 s.
    /// </summary>
    [Fact]
    public void ParseDegradation_ReadsTheDelayItAdded_NotItsTotalTime()
    {
        var ev = Event(110, ("Name", "SMSSInit"), ("TotalTime", "10453"), ("DegradationTime", "453"));

        var d = BootAnalyzerService.ParseDegradation(DateTime.Now, 110, ev);

        Assert.NotNull(d);
        Assert.Equal(("Core system", 453L), (d!.Kind, d.DurationMs));
        Assert.Equal("453 ms", d.DurationDisplay);
    }

    /// <summary>
    /// One event per ID, with the Name, TotalTime and DegradationTime every one of them carries, through to the row: the
    /// kind, the delay and the link (#2770). Only an application and a service link anywhere, because no tab here
    /// changes what the others name.
    /// </summary>
    [Theory]
    [InlineData(101, "Application", "nav-startup")]
    [InlineData(102, "Driver", "")]
    [InlineData(103, "Service", "nav-services")]
    [InlineData(104, "Core system", "")]
    [InlineData(105, "Prefetching", "")]
    [InlineData(106, "Prefetching", "")]
    [InlineData(107, "Group Policy", "")]
    [InlineData(108, "Group Policy", "")]
    [InlineData(109, "Device", "")]
    [InlineData(110, "Core system", "")]
    public void ParseDegradation_GivesEachEventItsKindDelayAndLink(int id, string kind, string navTargetId)
    {
        var ev = Event(id, ("Name", $"component-{id}"), ("TotalTime", "3000"), ("DegradationTime", "1000"));

        var d = BootAnalyzerService.ParseDegradation(DateTime.Now, id, ev);

        Assert.NotNull(d);
        Assert.Equal(($"component-{id}", kind, 1000L, navTargetId), (d!.Name, d.Kind, d.DurationMs, d.NavTargetId));
    }

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
