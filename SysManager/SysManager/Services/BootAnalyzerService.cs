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
/// Reading that log requires administrator. Without elevation the read is refused and reported as
/// failed, not as an empty history. The event-ID→kind mapping and XML field parsing are pure static
/// methods so they can be unit-tested without the live event log.
/// </summary>
public sealed class BootAnalyzerService
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private static readonly XNamespace EvtNs = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>How many reads in a row may fail before a read gives up.</summary>
    /// <remarks>More than one, because a single failed read can be transient. Few, because an error that repeats
    /// repeats on every call.</remarks>
    internal const int MaxConsecutiveReadFailures = 3;

    private readonly Func<string, IBootEventReader> _open;

    public BootAnalyzerService() : this(OpenLog) { }

    /// <summary>Test seam: the same service reading from <paramref name="open"/> instead of the live event log.</summary>
    /// <param name="open">Opens a reader for an XPath query over the Diagnostics-Performance log, newest first.</param>
    internal BootAnalyzerService(Func<string, IBootEventReader> open)
        => _open = open ?? throw new ArgumentNullException(nameof(open));

    /// <summary>
    /// Reads up to <paramref name="maxBoots"/> recent boot summaries, newest first. Null when the log could not be
    /// read; empty when it was read and holds none.
    /// </summary>
    public Task<IReadOnlyList<BootRecord>?> ReadBootsAsync(int maxBoots = 20, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<BootRecord>?>(() => ReadEvents("*[System[(EventID=100)]]", maxBoots, ct)?
            .Select(e => ParseBoot(e.When, e.Xml))
            .OfType<BootRecord>()
            .ToList(), ct);

    /// <summary>
    /// Reads recent boot-degradation events (slow apps/drivers/services), newest first. Null when the log could not
    /// be read; empty when it was read and holds none.
    /// </summary>
    public Task<IReadOnlyList<BootDegradation>?> ReadDegradationsAsync(int max = 60, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<BootDegradation>?>(() => ReadEvents("*[System[(EventID>=101 and EventID<=110)]]", max, ct)?
            .Select(e => ParseDegradation(e.When, e.Id, e.Xml))
            .OfType<BootDegradation>()
            .ToList(), ct);

    /// <summary>
    /// Reads up to <paramref name="max"/> events matching <paramref name="xpath"/>, newest first. Null when nothing
    /// could be read: the log refused, does not exist, or failed before its first event.
    /// </summary>
    /// <remarks>
    /// A failed read ends after <see cref="MaxConsecutiveReadFailures"/> attempts in a row (#2500). It used to be
    /// retried with no bound, and the counter that ends the loop only moved when an event came back. So an error
    /// that repeats spun a thread at full speed until the user pressed Cancel. Such errors are real: when the log
    /// rolls over or is cleared during the read, every later call fails the same way, and this log is 1 MB and
    /// wraps. What was read before the failure is kept, so a read that stops part-way still shows what it reached.
    /// <para>Null and empty are different answers. Null is a read that failed, which the tab reports as such. Empty
    /// is a log that was read and has nothing in it yet, which is normal on a new PC. The tab used to say "No boot
    /// performance events found yet" for both.</para>
    /// </remarks>
    private List<BootEvent>? ReadEvents(string xpath, int max, CancellationToken ct)
    {
        IBootEventReader reader;
        try { reader = _open(xpath); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Boot analyzer: log access denied: {Error}", ex.Message); return null; }
        catch (EventLogNotFoundException ex) { Log.Debug("Boot analyzer: log not found: {Error}", ex.Message); return null; }
        catch (EventLogException ex) { Log.Debug("Boot analyzer: query failed: {Error}", ex.Message); return null; }

        using (reader)
        {
            List<BootEvent> events = [];
            var failures = 0;
            while (events.Count < max)
            {
                ct.ThrowIfCancellationRequested();

                BootEvent? next;
                try
                {
                    next = reader.ReadNext();
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
                {
                    if (++failures < MaxConsecutiveReadFailures)
                    {
                        Log.Debug("Boot analyzer: read failed, retrying: {Error}", ex.Message);
                        continue;
                    }

                    Log.Warning("Boot analyzer: read stopped after {Failures} failures in a row: {Error}", failures, ex.Message);
                    return events.Count > 0 ? events : null;
                }

                failures = 0;
                if (next is not { } ev) break;
                events.Add(ev);
            }

            return events;
        }
    }

    /// <summary>One event's fields, as the parsers read them.</summary>
    internal readonly record struct BootEvent(DateTime When, int Id, XElement? Xml);

    /// <summary>Reads the events of one query, one at a time, newest first.</summary>
    internal interface IBootEventReader : IDisposable
    {
        /// <summary>
        /// The next event, or null when there are no more. Throws <see cref="EventLogException"/> for a read that
        /// failed, as <see cref="EventLogReader.ReadEvent()"/> does.
        /// </summary>
        BootEvent? ReadNext();
    }

    private static IBootEventReader OpenLog(string xpath) => new LogReader(xpath);

    /// <summary>The live Diagnostics-Performance log.</summary>
    private sealed class LogReader(string xpath) : IBootEventReader
    {
        private readonly EventLogReader _reader =
            new(new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true });

        public BootEvent? ReadNext()
        {
            using var record = _reader.ReadEvent();
            if (record is null) return null;

            XElement? xml = null;
            try { xml = XElement.Parse(record.ToXml()); }
            catch (System.Xml.XmlException) { /* kept with no payload, which the parsers treat as nothing to show */ }
            return new BootEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, xml);
        }

        public void Dispose() => _reader.Dispose();
    }

    // ── Pure parsing (unit-tested) ─────────────────────────────────────────────

    /// <summary>
    /// Reads a named value from an event's XML payload. Windows emits Diagnostics-Performance
    /// event 100 in the standard <c>&lt;EventData&gt;&lt;Data Name="BootTime"&gt;…&lt;/Data&gt;</c>
    /// form (verified against a live event), which the primary lookup handles. A defensive
    /// fallback also resolves a directly-named leaf element (e.g. <c>&lt;BootTime&gt;…&lt;/BootTime&gt;</c>),
    /// matched by local name and namespace-agnostic, so the reader still works for any event
    /// variant that nests its fields differently.
    /// </summary>
    internal static string? DataValue(XElement? eventXml, string name)
    {
        if (eventXml is null) return null;

        // 1) Standard shape: <Data Name="name">value</Data>.
        var data = eventXml.Descendants(EvtNs + "Data")
            .FirstOrDefault(d => (string?)d.Attribute("Name") == name);
        if (data is not null) return data.Value;

        // 2) Fallback: a directly-named leaf element <name>value</name> (match by local name,
        //    ignoring namespace; skip container elements that have their own children).
        var named = eventXml.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements);
        return named?.Value;
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
