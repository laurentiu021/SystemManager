// SysManager · BatteryReportParserTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using static SysManager.Tests.BatteryReportFixture;

namespace SysManager.Tests;

/// <summary>
/// <see cref="BatteryReportParser"/> reads the capacity history out of the XML <c>powercfg /batteryreport</c> writes
/// (#1513).
/// </summary>
public sealed class BatteryReportParserTests
{
    private static readonly DateTime Start = new(2026, 4, 6, 3, 21, 6);

    [Fact]
    public void AReport_GivesItsHistory_OldestFirst()
    {
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start.AddDays(14), 57000, 54150),
            Entry(Start, 57000, 57000),
            Entry(Start.AddDays(7), 57000, 55860)));

        Assert.NotNull(history);
        Assert.Equal(new[] { Start, Start.AddDays(7), Start.AddDays(14) }, history.Select(p => p.Date));
        Assert.Equal(new[] { 100d, 98d, 95d }, history.Select(p => p.HealthPercent));
        Assert.All(history, p => Assert.Equal(57000, p.DesignCapacityMWh));
        Assert.Equal(new[] { 57000L, 55860, 54150 }, history.Select(p => p.FullChargeCapacityMWh));
    }

    [Fact]
    public void ADesktopsEntries_HaveNoCapacity_AndAreLeftOut()
    {
        // A PC with no battery writes the same History section with every capacity 0. That is no history, not an
        // empty battery, so it must not come back as entries at 0%.
        var history = BatteryReportParser.ParseHistory(Report(Entry(Start, 0, 0), Entry(Start.AddDays(7), 0, 0)));

        Assert.NotNull(history);
        Assert.Empty(history);
    }

    [Fact]
    public void AnEntryMissingEitherCapacity_IsLeftOut()
    {
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start, 57000, 0),
            Entry(Start.AddDays(7), 0, 55000),
            Entry(Start.AddDays(14), 57000, 55000)));

        Assert.Equal(Start.AddDays(14), Assert.Single(history!).Date);
    }

    [Fact]
    public void ABatteryChange_StartsTheHistoryAtTheNewBattery()
    {
        // Kept, the old battery's entries would draw the replacement as a recovery.
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start, 57000, 45600),
            Entry(Start.AddDays(7), 57000, 45030),
            Entry(Start.AddDays(14), 50000, 50000, batteryChanged: "1"),
            Entry(Start.AddDays(21), 50000, 49500)));

        Assert.NotNull(history);
        Assert.Equal(new[] { Start.AddDays(14), Start.AddDays(21) }, history.Select(p => p.Date));
        Assert.Equal(new[] { 100d, 99d }, history.Select(p => p.HealthPercent));
    }

    [Fact]
    public void OnlyTheNewestBatteryChangeCounts()
    {
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start, 57000, 45600),
            Entry(Start.AddDays(7), 50000, 50000, batteryChanged: "1"),
            Entry(Start.AddDays(14), 50000, 49000),
            Entry(Start.AddDays(21), 52000, 52000, batteryChanged: "1"),
            Entry(Start.AddDays(28), 52000, 51480)));

        Assert.Equal(new[] { Start.AddDays(21), Start.AddDays(28) }, history!.Select(p => p.Date));
    }

    [Fact]
    public void ABatteryChange_IsFoundInDateOrder_NotFileOrder()
    {
        // The cut is made after sorting, so a change listed first in the file still drops what came before it in time.
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start.AddDays(14), 50000, 50000, batteryChanged: "1"),
            Entry(Start, 57000, 45600),
            Entry(Start.AddDays(21), 50000, 49500)));

        Assert.Equal(new[] { Start.AddDays(14), Start.AddDays(21) }, history!.Select(p => p.Date));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void TheBatteryChangedFlag_IsReadAsAnXmlBoolean(string flag, bool cuts)
    {
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start, 57000, 45600),
            Entry(Start.AddDays(7), 50000, 50000, batteryChanged: flag)));

        Assert.Equal(cuts ? 1 : 2, history!.Count);
    }

    [Theory]
    [InlineData("LocalStartDate=\"not a date\" DesignCapacity=\"57000\" FullChargeCapacity=\"55000\"")]
    [InlineData("DesignCapacity=\"57000\" FullChargeCapacity=\"55000\"")]
    [InlineData("LocalStartDate=\"2026-04-20T03:21:06\" DesignCapacity=\"57 000\" FullChargeCapacity=\"55000\"")]
    [InlineData("LocalStartDate=\"2026-04-20T03:21:06\" DesignCapacity=\"57000\"")]
    public void AnEntryThatCannotBeRead_IsSkipped_AndTheRestIsKept(string attributes)
    {
        var history = BatteryReportParser.ParseHistory(Report(
            Entry(Start, 57000, 57000),
            $"<HistoryEntry {attributes} BatteryChanged=\"0\"/>"));

        Assert.Equal(Start, Assert.Single(history!).Date);
    }

    [Fact]
    public void ALaterSchemaUri_IsStillRead()
    {
        // Elements are matched by local name, so a new namespace on the root does not blank the chart.
        var history = BatteryReportParser.ParseHistory(ReportUnder("http://schemas.microsoft.com/battery/2030",
            Entry(Start, 57000, 57000), Entry(Start.AddDays(7), 57000, 56430)));

        Assert.Equal(2, history!.Count);
    }

    [Theory]
    [InlineData("<html><body>not a report</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><SleepStudy xmlns=\"http://schemas.microsoft.com/sleepstudy/2012\"/>")]
    public void XmlThatIsNotABatteryReport_IsNull(string xml) => Assert.Null(BatteryReportParser.ParseHistory(xml));

    [Theory]
    [InlineData("")]
    [InlineData("Battery life report saved to file path D:\\report.xml.")]
    [InlineData("<BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\"><History>")]
    public void TextThatIsNotXml_IsNull(string text) => Assert.Null(BatteryReportParser.ParseHistory(text));

    [Fact]
    public void AReportCarryingADocumentTypeDeclaration_IsRefused()
    {
        // No DTD is processed, so an entity in the file is never expanded, whatever it would expand to.
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE BatteryReport [<!ENTITY flag \"1\">]>"
            + "<BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\"><History>"
            + "<HistoryEntry LocalStartDate=\"2026-04-06T03:21:06\" DesignCapacity=\"57000\" FullChargeCapacity=\"57000\""
            + " BatteryChanged=\"&flag;\"/></History></BatteryReport>";

        Assert.Null(BatteryReportParser.ParseHistory(xml));
    }

    [Fact]
    public void Null_IsRejected() => Assert.Throws<ArgumentNullException>(() => BatteryReportParser.ParseHistory(null!));
}
