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

    // ── The five that followed: Process Manager, App Alerts, File Lock, Shortcut Cleaner, Bandwidth ──

    /// <summary>
    /// Raw start time beside the display string, and an em dash rather than year one when Windows would not
    /// say — the same reason the column itself needs it (#2224).
    /// </summary>
    [Fact]
    public void ProcessToCsv_CarriesRawValuesBesideTheDisplayedOnes()
    {
        var csv = ProcessManagerService.ToCsv([
            new ProcessEntry
            {
                Pid = 4242, Name = "target.exe", PlainDescription = "A test process",
                // 3.7, not a .x5 value: "F1" rounds midpoints to even, so 3.25 formats as 3.2 and a test
                // asserting 3.3 fails on the formatter being right.
                MemoryBytes = 52_428_800, CpuPercent = 3.7, ThreadCount = 7, Status = "Running",
                StartTime = new DateTime(2026, 3, 9, 14, 5, 7), Category = "System",
                SafetyLevel = "Known app", FilePath = @"C:\Program Files\Test\target.exe",
            }]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("PID,Name,Description,Memory,Memory (bytes),CPU %,Threads,Status,Started,", lines[0],
            StringComparison.Ordinal);
        Assert.Contains("50.0 MB,52428800,3.7,7,Running,2026-03-09 14:05:07,2026-03-09 14:05:07,", lines[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessToCsv_WhenWindowsWithheldTheStartTime_LeavesTheRawColumnEmpty()
    {
        var csv = ProcessManagerService.ToCsv([new ProcessEntry { Pid = 4, Name = "System" }]);

        // Display column is the em dash; the raw column is empty rather than 0001-01-01.
        Assert.Contains(",—,,", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("0001", csv, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unchecked signature must export as blank, not as the enum's name. <c>Unknown</c> means "not
    /// checked", and writing that word into a file reads as a verdict the app never reached.
    /// </summary>
    [Fact]
    public void ProcessToCsv_LeavesTheSignatureBlankWhenNothingWasChecked()
    {
        // Asserted on the signature COLUMN, not on the whole row: Category and SafetyLevel both default to
        // the literal "Unknown" and are legitimately exported that way, so a row-wide DoesNotContain would
        // fail on two columns that are correct.
        const int signature = 12;   // PID,Name,Description,Memory,Memory(bytes),CPU,Threads,Status,Started,
                                    // Started(raw),Category,Safety,Signature,Path
        var row = ProcessManagerService.ToCsv([new ProcessEntry { Pid = 4, Name = "System" }])
            .Split("\r\n")[1].Split(',');
        Assert.Equal("", row[signature]);

        var verified = ProcessManagerService.ToCsv([
            new ProcessEntry { Pid = 9, Name = "signed.exe", Signature = SignatureTrust.Verified,
                               SignatureDetail = "Windows can confirm this comes from Contoso Ltd" }])
            .Split("\r\n")[1].Split(',');
        Assert.Equal("Verified", verified[signature]);
    }

    [Fact]
    public void AlertsToCsv_WritesTheHeaderAndTheAcknowledgedFlag()
    {
        var csv = AppAlertService.ToCsv([
            new AppInstallEntry { Name = "Contoso Toolbar", Publisher = "Contoso, Inc.",
                                  DetectedAt = new DateTime(2026, 3, 9, 14, 5, 7), Source = "Registry",
                                  InstallPath = @"C:\Program Files\Contoso", IsAcknowledged = true }]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("App,Publisher,Detected,Source,Install path,Acknowledged", lines[0]);
        // The publisher's comma must not split the row.
        Assert.Equal(@"Contoso Toolbar,""Contoso, Inc."",2026-03-09 14:05:07,Registry,C:\Program Files\Contoso,yes",
            lines[1]);
    }

    /// <summary>
    /// The critical flag gets its own column because it is the one row nobody should be told to end, and that
    /// warning has to survive into a file someone else may act on.
    /// </summary>
    [Fact]
    public void LockersToCsv_MarksACriticalProcessInItsOwnColumn()
    {
        var csv = FileLockService.ToCsv([
            new FileLocker(4, "System", "RmCritical", null),
            new FileLocker(4242, "notepad.exe", "RmMainWindow", new DateTime(2026, 3, 9, 14, 5, 7)),
        ]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("PID,Process,Type,Started,Started (raw),Critical", lines[0]);
        Assert.EndsWith(",yes", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",no", lines[2], StringComparison.Ordinal);
        // No start time available: em dash in the display column, empty in the raw one.
        Assert.Equal("4,System,RmCritical,—,,yes", lines[1]);
    }

    /// <summary>Both paths, because one names what would be deleted and the other is the evidence why.</summary>
    [Fact]
    public void ShortcutsToCsv_CarriesBothTheShortcutAndItsMissingTarget()
    {
        var csv = ShortcutCleanerService.ToCsv([
            new BrokenShortcut { Name = "Old Game", Location = "Desktop",
                                 ShortcutPath = @"C:\Users\me\Desktop\Old Game.lnk",
                                 TargetPath = @"D:\Games\Old, Game\game.exe", IsSelected = true }]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Name,Location,Shortcut path,Missing target,Selected", lines[0]);
        Assert.Contains(@"C:\Users\me\Desktop\Old Game.lnk", lines[1], StringComparison.Ordinal);
        Assert.Contains(@"""D:\Games\Old, Game\game.exe""", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BandwidthToCsv_CarriesRatesAndTotalsWithRawNumbers()
    {
        var csv = BandwidthHistoryService.ToCsv([
            new ProcessNetworkUsage
            {
                ProcessId = 4242, ProcessName = "browser.exe", ConnectionCount = 12,
                DownBytesPerSec = 1_048_576, UpBytesPerSec = 131_072,
                TotalDownBytes = 52_428_800, TotalUpBytes = 1_048_576,
                RemoteSummary = "contoso.example, 3 more",
            }]);

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("PID,Process,Connections,Down,Down (bytes/s),Up,Up (bytes/s),Total,Total (bytes),Remote",
            lines[0]);
        Assert.Contains(",1048576,", lines[1], StringComparison.Ordinal);
        // Total bytes is the sum of both directions, and the remote summary's comma stays quoted.
        Assert.Contains(",53477376,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("\"contoso.example, 3 more\"", lines[1], StringComparison.Ordinal);
    }

    // ── Every formatter refuses null rather than throwing something unhelpful deep inside ──

    [Fact]
    public void EveryFormatter_RejectsANullSequence()
    {
        Assert.Throws<ArgumentNullException>(() => PrivacyMonitorService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => SettingsWatchdogService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => DiskAnalyzerService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => ProcessManagerService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => AppAlertService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => FileLockService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => ShortcutCleanerService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => BandwidthHistoryService.ToCsv(null!));
        Assert.Throws<ArgumentNullException>(() => Csv.AppendRow(null!, "a"));
    }

    /// <summary>
    /// Every export starts with a header row, so a file opened months later says what its columns are.
    /// </summary>
    /// <remarks>
    /// Written as one test over all eight rather than eight assertions, because the thing being pinned is the
    /// convention, and a ninth export added without a header should fail this rather than pass unnoticed.
    /// </remarks>
    [Fact]
    public void EveryExport_StartsWithAHeaderRow()
    {
        var empty = new (string Name, string Csv)[]
        {
            ("Privacy", PrivacyMonitorService.ToCsv([])),
            ("SettingsDrift", SettingsWatchdogService.ToCsv([])),
            ("DiskUsage", DiskAnalyzerService.ToCsv([])),
            ("Processes", ProcessManagerService.ToCsv([])),
            ("AppAlerts", AppAlertService.ToCsv([])),
            ("FileLocks", FileLockService.ToCsv([])),
            ("BrokenShortcuts", ShortcutCleanerService.ToCsv([])),
            ("Bandwidth", BandwidthHistoryService.ToCsv([])),
        };

        foreach (var (name, csv) in empty)
        {
            Assert.EndsWith("\r\n", csv, StringComparison.Ordinal);
            var header = csv.Split("\r\n")[0];
            Assert.False(string.IsNullOrWhiteSpace(header), $"{name} exported an empty header row");
            Assert.Contains(',', header);   // a single-column export would be a formatting mistake
            Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
