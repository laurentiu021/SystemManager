// SysManager · CsvExportTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="Csv"/> and the three <c>ToCsv</c> formatters behind the Export CSV buttons.
/// </summary>
/// <remarks>
/// The formatters are pure static methods precisely so they can be tested without a file dialog, which is the
/// shape <c>ResourceHistoryService.ToCsv</c> established. What is NOT covered here is the command itself:
/// <c>SaveFileDialog.ShowDialog()</c> needs a window, so the export commands are exercised by the guard that
/// checks each one is bound in its view, not by these tests.
/// </remarks>
public class CsvExportTests
{
    // ── Csv.Field: the escaping the older exporter did not need ──
    //
    // ResourceHistoryService.ToCsv writes its fields raw, and that is safe there because every one is a
    // number or a fixed-format timestamp. These three exports carry app names, setting descriptions and
    // folder paths, so the separator can legitimately appear inside a value.

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Grieg, Peer Gynt", "\"Grieg, Peer Gynt\"")]
    [InlineData("say \"hello\"", "\"say \"\"hello\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData("C:\\Users\\me\\Music", "C:\\Users\\me\\Music")]
    public void Field_QuotesOnlyWhatHasTo(string? input, string expected)
        => Assert.Equal(expected, Csv.Field(input));

    /// <summary>
    /// A folder name containing a comma must survive as ONE column. This is the whole reason the helper
    /// exists: without quoting the row silently gains a column and every value after it shifts left, which a
    /// spreadsheet reads without complaining.
    /// </summary>
    [Fact]
    public void AppendRow_KeepsACommaInsideOneField()
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, "Grieg, Peer Gynt", "1024");

        Assert.Equal("\"Grieg, Peer Gynt\",1024\r\n", sb.ToString());
        // Three commas would mean four columns; the quoted one must not count as a separator.
        Assert.Equal(1, sb.ToString().Count(c => c == ',') - 1);
    }

    /// <summary>CRLF, because this is a file format rather than console output. RFC 4180 names it.</summary>
    [Fact]
    public void AppendRow_TerminatesWithCrLf()
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, "a");
        Assert.EndsWith("\r\n", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendRow_WithNoFields_StillEndsTheRow()
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb);
        Assert.Equal("\r\n", sb.ToString());
    }

    // ── PrivacyMonitorService.ToCsv ──

    [Fact]
    public void PrivacyToCsv_WritesAHeaderAndOneRowPerEntry()
    {
        var csv = PrivacyMonitorService.ToCsv([
            new PrivacyAccessEntry("Camera", "Contoso Meet", new DateTime(2026, 3, 9, 14, 5, 7), false),
            new PrivacyAccessEntry("Microphone", "Contoso Meet", null, true),
        ]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Capability,App,Last used,Last used (raw),In use now", lines[0]);
        Assert.Contains("Camera,Contoso Meet,2026-03-09 14:05,2026-03-09 14:05:07,no", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// "In use now" is not a timestamp, so the raw column must be empty rather than invented — and the
    /// readable column must still say what the tab says.
    /// </summary>
    [Fact]
    public void PrivacyToCsv_WhenStillInUse_LeavesTheRawTimestampEmpty()
    {
        var csv = PrivacyMonitorService.ToCsv([new PrivacyAccessEntry("Location", "Maps", null, true)]);

        var row = csv.Split("\r\n")[1];
        Assert.Equal("Location,Maps,In use now,,yes", row);
    }

    [Fact]
    public void PrivacyToCsv_WithNoEntries_IsHeaderOnly()
    {
        var csv = PrivacyMonitorService.ToCsv([]);
        Assert.Equal("Capability,App,Last used,Last used (raw),In use now\r\n", csv);
    }

    /// <summary>An app name with a comma in it stays one column.</summary>
    [Fact]
    public void PrivacyToCsv_QuotesAnAppNameContainingAComma()
    {
        var csv = PrivacyMonitorService.ToCsv([
            new PrivacyAccessEntry("Camera", "Acme, Inc. Camera", null, false)]);

        Assert.Contains("\"Acme, Inc. Camera\"", csv, StringComparison.Ordinal);
    }

    // ── SettingsWatchdogService.ToCsv ──

    /// <summary>A watched setting whose raw 0/3 read back as words, which is what the export must carry.</summary>
    private static WatchedSetting TelemetrySetting() => new(
        Key: "telemetry",
        Name: "Telemetry",
        Description: "How much diagnostic data Windows sends",
        Category: "Privacy",
        RegistryPath: @"HKLM\Software\Policies\Microsoft\Windows\DataCollection",
        ValueName: "AllowTelemetry",
        ValueLabels: new Dictionary<int, string> { [0] = "Off", [3] = "Full" });

    [Fact]
    public void DriftToCsv_WritesTheLabelsTheTabShows()
    {
        var csv = SettingsWatchdogService.ToCsv([new SettingDrift(TelemetrySetting(), 0, 3)]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Setting,Category,Description,Was,Now,Can restore", lines[0]);
        Assert.StartsWith("Telemetry,Privacy,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",yes", lines[1], StringComparison.Ordinal);
        // The point of exporting labels rather than the raw integers: "Off"/"Full", not 0/3.
        Assert.Contains(",Off,Full,", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A drift the app cannot undo is the one worth keeping a note of, so the column has to distinguish it.
    /// </summary>
    [Fact]
    public void DriftToCsv_RecordsWhetherEachRowCanBeRestored()
    {
        var csv = SettingsWatchdogService.ToCsv([
            new SettingDrift(TelemetrySetting(), 0, 3, CanRestore: false)]);

        Assert.EndsWith(",no\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void DriftToCsv_WithNoDrifts_IsHeaderOnly()
    {
        var csv = SettingsWatchdogService.ToCsv([]);
        Assert.Equal("Setting,Category,Description,Was,Now,Can restore\r\n", csv);
    }

    // ── DiskAnalyzerService.ToCsv ──

    /// <summary>
    /// Both the formatted size and the raw byte count, because "9.8 GB" sorts below "10 MB" as text — so a
    /// spreadsheet given only the formatted column cannot answer "what is biggest", which is the question.
    /// </summary>
    [Fact]
    public void DiskUsageToCsv_CarriesTheRawByteCountAlongsideTheFormattedSize()
    {
        var csv = DiskAnalyzerService.ToCsv([
            new DiskUsageEntry { Name = "Music", FullPath = @"C:\Users\me\Music", SizeBytes = 52_428_800,
                                 Percentage = 12.34, FileCount = 7, FolderCount = 2 }]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Name,Full path,Size,Size (bytes),Share %,Files,Folders,Access denied", lines[0]);
        Assert.Equal(@"Music,C:\Users\me\Music,50.0 MB,52428800,12.3,7,2,no", lines[1]);
    }

    /// <summary>The export most likely to meet a comma, since folder names are chosen by whoever made them.</summary>
    [Fact]
    public void DiskUsageToCsv_QuotesAFolderPathContainingAComma()
    {
        var csv = DiskAnalyzerService.ToCsv([
            new DiskUsageEntry { Name = "Grieg, Peer Gynt", FullPath = @"C:\Music\Grieg, Peer Gynt",
                                 SizeBytes = 1024 }]);

        var row = csv.Split("\r\n")[1];
        Assert.StartsWith("\"Grieg, Peer Gynt\",\"C:\\Music\\Grieg, Peer Gynt\",", row, StringComparison.Ordinal);
        // Eight columns, so seven separators — the two commas inside the quoted fields must not add any.
        Assert.Equal(7, row.Split('"')
            .Where((_, i) => i % 2 == 0)
            .Sum(outside => outside.Count(c => c == ',')));
    }

    [Fact]
    public void DiskUsageToCsv_MarksAnAccessDeniedFolder()
    {
        var csv = DiskAnalyzerService.ToCsv([
            new DiskUsageEntry { Name = "System Volume Information", SizeBytes = 0, IsAccessDenied = true }]);

        Assert.EndsWith(",yes\r\n", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskUsageToCsv_WithNoEntries_IsHeaderOnly()
    {
        var csv = DiskAnalyzerService.ToCsv([]);
        Assert.Equal("Name,Full path,Size,Size (bytes),Share %,Files,Folders,Access denied\r\n", csv);
    }

    // ── Every formatter refuses null rather than throwing something unhelpful deep inside ──

    [Fact]
    public void EveryFormatter_RejectsANullSequence()
    {
        Assert.Throws<ArgumentNullException>(() => PrivacyMonitorService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => SettingsWatchdogService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => DiskAnalyzerService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => Csv.AppendRow(null!, "a"));
    }
}
