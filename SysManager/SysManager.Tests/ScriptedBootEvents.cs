// SysManager · ScriptedBootEvents
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Stand-ins for the Diagnostics-Performance log, for tests that drive <see cref="BootAnalyzerService"/>'s read loop
/// without the live event log or administrator rights.
/// </summary>
internal static class ScriptedBootEvents
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>An event 100: one boot that took <paramref name="bootMs"/> milliseconds.</summary>
    internal static BootAnalyzerService.BootEvent Boot(int bootMs, DateTime? when = null) =>
        new(when ?? new DateTime(2026, 9, 28, 8, 0, 0), 100, Payload(100, ("BootTime", bootMs.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>An event 101: an application that slowed a boot by <paramref name="ms"/> milliseconds.</summary>
    internal static BootAnalyzerService.BootEvent SlowApp(string name, int ms) =>
        new(new DateTime(2026, 9, 28, 8, 0, 0), 101,
            Payload(101, ("Name", name), ("TotalTime", ms.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>The failure <see cref="EventLogReader"/> raises when the log changed under a running query.</summary>
    internal static EventLogReadingException Stale() =>
        new("The query result is stale or invalid. This may be due to the log being cleared or rolling over.");

    private static XElement Payload(int id, params (string Name, string Value)[] data)
    {
        var eventData = new XElement(Ns + "EventData");
        foreach (var (name, value) in data)
            eventData.Add(new XElement(Ns + "Data", new XAttribute("Name", name), value));
        return new XElement(Ns + "Event", new XElement(Ns + "System", new XElement(Ns + "EventID", id)), eventData);
    }
}

/// <summary>
/// A reader that plays a script, one step per read: each step returns an event, returns null for the end, or
/// throws. The last step repeats once the script runs out, which is how "every read from here on fails" is written.
/// </summary>
/// <remarks>
/// It stops itself after <see cref="Ceiling"/> reads with an exception the read loop does not catch. A loop that
/// retries without a bound then fails its test in milliseconds instead of hanging the run, which is the defect these
/// tests exist to catch (#2500).
/// </remarks>
internal sealed class ScriptedBootReader(params Func<BootAnalyzerService.BootEvent?>[] steps)
    : BootAnalyzerService.IBootEventReader
{
    /// <summary>More reads than any bounded loop makes for these scripts.</summary>
    internal const int Ceiling = 200;

    /// <summary>How many times <see cref="ReadNext"/> was called.</summary>
    public int Reads { get; private set; }

    /// <summary>Whether the service released the reader.</summary>
    public bool Disposed { get; private set; }

    public BootAnalyzerService.BootEvent? ReadNext()
    {
        if (Reads >= Ceiling)
            throw new InvalidOperationException($"the read loop kept going after {Ceiling} reads");
        var step = steps[Math.Min(Reads, steps.Length - 1)];
        Reads++;
        return step();
    }

    public void Dispose() => Disposed = true;

    /// <summary>A step that returns <paramref name="e"/>.</summary>
    internal static Func<BootAnalyzerService.BootEvent?> Returns(BootAnalyzerService.BootEvent e) => () => e;

    /// <summary>A step that ends the log.</summary>
    internal static Func<BootAnalyzerService.BootEvent?> End() => () => null;

    /// <summary>A step that fails the way <see cref="EventLogReader.ReadEvent()"/> does.</summary>
    internal static Func<BootAnalyzerService.BootEvent?> Fails() => () => throw ScriptedBootEvents.Stale();
}
