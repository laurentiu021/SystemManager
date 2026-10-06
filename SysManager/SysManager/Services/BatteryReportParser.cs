// SysManager · BatteryReportParser
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads the capacity history out of the XML that <c>powercfg /batteryreport /xml</c> writes (#1513).
/// </summary>
/// <remarks>
/// <para>Windows keeps this history itself: a <c>History</c> section with one <c>HistoryEntry</c> per week for
/// older periods and one per day for the last few, each carrying <c>DesignCapacity</c>,
/// <c>FullChargeCapacity</c> and a <c>BatteryChanged</c> flag. So a chart can show months on its first day,
/// instead of starting empty and filling up from the release that ships it.</para>
/// <para>Pure, so the reading is testable on any machine: a desktop's report has the same entries, all with a
/// capacity of 0, which are dropped here rather than plotted as an empty battery.</para>
/// <para>Elements are matched by local name, the way <see cref="StartupService"/> reads task definitions, so a
/// change of the schema URI between Windows versions does not blank the chart. No DTD and no resolver: the file
/// is ours, written to a name we chose, but nothing about reading it needs either.</para>
/// </remarks>
public static class BatteryReportParser
{
    /// <summary>
    /// The history of the battery fitted now, oldest first, or null when <paramref name="xml"/> is not a battery
    /// report at all.
    /// </summary>
    /// <remarks>
    /// Entries with no capacity are left out. When an entry is flagged as a battery change, everything before it
    /// belongs to the old battery and is dropped too: a replacement would otherwise draw as a recovery.
    /// </remarks>
    public static IReadOnlyList<BatteryCapacityPoint>? ParseHistory(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            Log.Debug("Battery report is not readable XML: {Error}", ex.Message);
            return null;
        }

        if (doc.Root is not { Name.LocalName: "BatteryReport" } root) return null;

        var entries = root.Elements()
            .Where(e => e.Name.LocalName == "History")
            .SelectMany(h => h.Elements().Where(e => e.Name.LocalName == "HistoryEntry"))
            .Select(ReadEntry)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .OrderBy(e => e.Point.Date)
            .ToList();

        var newestChange = entries.FindLastIndex(e => e.BatteryChanged);
        if (newestChange > 0) entries.RemoveRange(0, newestChange);

        return [.. entries.Select(e => e.Point).Where(p => p.DesignCapacityMWh > 0 && p.FullChargeCapacityMWh > 0)];
    }

    private static (BatteryCapacityPoint Point, bool BatteryChanged)? ReadEntry(XElement entry)
    {
        var start = (string?)entry.Attribute("LocalStartDate");
        if (start is null
            || !DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !long.TryParse((string?)entry.Attribute("DesignCapacity"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var design)
            || !long.TryParse((string?)entry.Attribute("FullChargeCapacity"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var full))
            return null;

        var changed = (string?)entry.Attribute("BatteryChanged");
        var batteryChanged = changed is "1" || string.Equals(changed, "true", StringComparison.OrdinalIgnoreCase);
        return (new BatteryCapacityPoint(date, design, full), batteryChanged);
    }
}
