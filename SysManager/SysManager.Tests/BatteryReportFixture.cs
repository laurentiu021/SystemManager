// SysManager · BatteryReportFixture
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;

namespace SysManager.Tests;

/// <summary>
/// Battery reports in the shape <c>powercfg /batteryreport /xml</c> writes them, for the parser and service tests.
/// </summary>
/// <remarks>
/// The structure is a real report's: the declaration, the stylesheet instruction Windows puts before the root, the
/// 2012 schema namespace, CRLF line ends, and the attributes a <c>HistoryEntry</c> carries. None of the values are.
/// A real report's system section names the machine it came from, so nothing else of one belongs in a test.
/// </remarks>
internal static class BatteryReportFixture
{
    /// <summary>The schema URI Windows writes on the report's root element.</summary>
    public const string SchemaUri = "http://schemas.microsoft.com/battery/2012";

    /// <summary>A report whose History section holds <paramref name="entries"/>.</summary>
    public static string Report(params string[] entries) => ReportUnder(SchemaUri, entries);

    /// <summary>The same report under <paramref name="schemaUri"/>, as a later Windows might write it.</summary>
    public static string ReportUnder(string schemaUri, params string[] entries) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
        + "<?xml-stylesheet type='text/xsl' href='C:\\battery-stylesheet.xsl'?>\r\n"
        + $"<BatteryReport xmlns=\"{schemaUri}\">\r\n"
        + "  <Batteries>\r\n  </Batteries>\r\n"
        + "  <History>\r\n"
        + string.Concat(entries.Select(e => e + "\r\n"))
        + "  </History>\r\n"
        + "</BatteryReport>\r\n";

    /// <summary>
    /// One week-long history entry starting at <paramref name="localStart"/>, with the attribute layout Windows uses.
    /// </summary>
    public static string Entry(DateTime localStart, long design, long full, string batteryChanged = "0") =>
        string.Create(CultureInfo.InvariantCulture, $"""
            <HistoryEntry
                  StartDate="{localStart.AddHours(-3):yyyy-MM-ddTHH:mm:ss}Z"
                  LocalStartDate="{localStart:yyyy-MM-ddTHH:mm:ss}"
                  EndDate="{localStart.AddDays(7).AddHours(-3):yyyy-MM-ddTHH:mm:ss}Z"
                  LocalEndDate="{localStart.AddDays(7):yyyy-MM-ddTHH:mm:ss}"
                  DesignCapacity="{design}"
                  FullChargeCapacity="{full}"
                  CycleCount="0"
                  ActiveAcTime="PT15H23M56S"
                  ActiveDcTime="PT0S"
                  BatteryChanged="{batteryChanged}"
                  />
            """);
}
