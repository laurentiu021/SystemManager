// SysManager · BootAnalyzerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads boot-performance history from the Microsoft-Windows-Diagnostics-Performance
/// operational log. Event 100 carries each boot's total/main-path/post-boot durations;
/// events 101–110 name components (apps, drivers, services, devices) that degraded boot.
/// Strictly read-only — it surfaces what Windows already measured; it changes nothing.
///
/// Reading that log requires administrator; without elevation the queries yield nothing
/// (handled gracefully). The event-ID→kind mapping and XML field parsing are pure static
/// methods so they can be unit-tested without the live event log.
/// </summary>
public sealed class BootAnalyzerService
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private static readonly XNamespace EvtNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Reads up to <paramref name="maxBoots"/> recent boot summaries, newest first.</summary>
    public Task<IReadOnlyList<BootRecord>> ReadBootsAsync(int maxBoots = 20, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<BootRecord>>(() =>
        {
            List<BootRecord> boots = [];
            foreach (var (rec, xml) in ReadEvents("*[System[(EventID=100)]]", maxBoots, ct))
            {
                using (rec)
                {
                    var b = ParseBoot(rec.TimeCreated ?? DateTime.MinValue, xml);
                    if (b is not null) boots.Add(b);
                }
            }
            return boots;
        }, ct);

    /// <summary>Reads recent boot-degradation events (slow apps/drivers/services), newest first.</summary>
    public Task<IReadOnlyList<BootDegradation>> ReadDegradationsAsync(int max = 60, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<BootDegradation>>(() =>
        {
            List<BootDegradation> items = [];
            foreach (var (rec, xml) in ReadEvents("*[System[(EventID>=101 and EventID<=110)]]", max, ct))
            {
                using (rec)
                {
                    var id = (int)(rec.Id);
                    var d = ParseDegradation(rec.TimeCreated ?? DateTime.MinValue, id, xml);
                    if (d is not null) items.Add(d);
                }
            }
            return items;
        }, ct);

    private IEnumerable<(EventRecord rec, XElement? xml)> ReadEvents(string xpath, int max, CancellationToken ct)
    {
        EventLogReader? reader = null;
        try
        {
            var q = new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true };
            reader = new EventLogReader(q);
        }
        catch (UnauthorizedAccessException ex) { Log.Debug("Boot analyzer: log access denied: {Error}", ex.Message); yield break; }
        catch (EventLogNotFoundException ex) { Log.Debug("Boot analyzer: log not found: {Error}", ex.Message); yield break; }
        catch (EventLogException ex) { Log.Debug("Boot analyzer: query failed: {Error}", ex.Message); yield break; }

        using (reader)
        {
            var emitted = 0;
            while (!ct.IsCancellationRequested && emitted < max)
            {
                EventRecord? rec;
                try { rec = reader.ReadEvent(); }
                catch (EventLogException) { continue; }
                if (rec is null) yield break;

                XElement? xml = null;
                try { xml = XElement.Parse(rec.ToXml()); }
                catch (System.Xml.XmlException) { /* emit with null xml */ }
                emitted++;
                yield return (rec, xml);
            }
        }
    }

    // ── Pure parsing (unit-tested) ─────────────────────────────────────────────

    /// <summary>Reads a named &lt;Data Name="x"&gt; value from an event's XML payload.</summary>
    internal static string? DataValue(XElement? eventXml, string name)
    {
        if (eventXml is null) return null;
        var data = eventXml.Descendants(EvtNs + "Data")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == name);
        return data?.Value;
    }

    private static long ParseLong(XElement? xml, string name)
        => long.TryParse(DataValue(xml, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Parses an event-100 payload into a <see cref="BootRecord"/>, or null if no boot time.</summary>
    internal static BootRecord? ParseBoot(DateTime when, XElement? xml)
    {
        var boot = ParseLong(xml, "BootTime");
        if (boot <= 0) return null;
        return new BootRecord(when, boot, ParseLong(xml, "MainPathBootTime"), ParseLong(xml, "BootPostBootTime"));
    }

    /// <summary>Maps a Diagnostics-Performance degradation event ID to a component kind.</summary>
    internal static string KindForEventId(int eventId) => eventId switch
    {
        101 or 105 => "Application",
        102 or 106 => "Driver",
        103 or 107 => "Service",
        104 or 108 => "Device",
        109 or 110 => "Background",
        _ => "Component"
    };

    /// <summary>Parses a degradation event (101–110) into a <see cref="BootDegradation"/>, or null.</summary>
    internal static BootDegradation? ParseDegradation(DateTime when, int eventId, XElement? xml)
    {
        var name = DataValue(xml, "Name") ?? DataValue(xml, "FriendlyName") ?? DataValue(xml, "FileName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var ms = ParseLong(xml, "TotalTime");
        if (ms == 0) ms = ParseLong(xml, "Time");
        if (ms == 0) ms = ParseLong(xml, "Degradation");
        return new BootDegradation(when, KindForEventId(eventId), name.Trim(), ms);
    }
}
