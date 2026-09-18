// SysManager · DiagnosticsBundleServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Text;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The diagnostics bundle contains what it says it contains, and does not contain what it says it left out.
/// </summary>
/// <remarks>
/// Both halves are the point. The bundle exists so a non-technical user can produce evidence for a bug
/// without navigating a hidden AppData folder and reasoning about log rotation (#1650) — and it is built to
/// be attached to a PUBLIC issue, so what it omits is as load-bearing as what it includes.
/// <para>Every test writes into its own temp directory and points the service at it. The default resolves
/// through <see cref="LogService.LogDir"/>, which is the developer's real log folder.</para>
/// </remarks>
public sealed class DiagnosticsBundleServiceTests : IDisposable
{
    private readonly string _logDir;
    private readonly string _outDir;

    public DiagnosticsBundleServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysManagerBundleTests", Guid.NewGuid().ToString("N"));
        _logDir = Path.Combine(root, "logs");
        _outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        var root = Directory.GetParent(_logDir)!.FullName;
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private string NewLog(string dayStamp, string content)
    {
        var path = Path.Combine(_logDir, $"sysmanager-{dayStamp}.log");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// A service pointed at this test's temp log folder.
    /// </summary>
    /// <remarks>
    /// The <see cref="SystemReportService"/> is constructed but never asked for a report here: every test
    /// below goes through <c>PackAsync</c>, which takes the report as a string. Gathering one reads WMI
    /// twice, which is a system dependency and belongs in the integration suite — and none of the behaviour
    /// tested here (which logs are chosen, which end of an oversized one survives, what the README claims)
    /// depends on hardware.
    /// </remarks>
    private DiagnosticsBundleService NewService()
        => new(new SystemReportService(new SystemInfoService(), new DiskHealthService()), _logDir);

    private const string CannedReport = "SysManager System Report\n  Network\n  Adapter — 10.0.x.x\n";

    // ── Which logs go in ─────────────────────────────────────────────────────

    /// <summary>
    /// The newest three files are chosen, by NAME rather than by timestamp.
    /// </summary>
    /// <remarks>
    /// The names sort chronologically, and a backup tool or a sync client touching a file rewrites its
    /// <c>LastWriteTime</c> — which would silently pick three old logs and omit the one holding the failure.
    /// The files here are written oldest-last on purpose, so an implementation ordering by write time would
    /// return exactly the wrong three and fail this.
    /// </remarks>
    [Fact]
    public void NewestLogs_TakesTheThreeLatestByName_NotByWriteTime()
    {
        foreach (var day in new[] { "20260918", "20260917", "20260916", "20260915", "20260914" })
            NewLog(day, $"entry for {day}");

        var chosen = NewService().NewestLogs().Select(f => f.Name).ToList();

        Assert.Equal(
            ["sysmanager-20260918.log", "sysmanager-20260917.log", "sysmanager-20260916.log"],
            chosen);
    }

    [Fact]
    public void NewestLogs_WithNoLogFolder_ReturnsNothingRatherThanThrowing()
    {
        Directory.Delete(_logDir, recursive: true);

        // A bundle without logs is still worth having — the report and the environment block answer most of
        // a bug report on their own, and failing the export would put the user back where they started.
        Assert.Empty(NewService().NewestLogs());
    }

    [Fact]
    public void NewestLogs_IgnoresFilesThatAreNotRollingLogs()
    {
        NewLog("20260918", "a real log");
        File.WriteAllText(Path.Combine(_logDir, "notes.txt"), "not a log");
        File.WriteAllText(Path.Combine(_logDir, "sysmanager.txt"), "not a log either");

        Assert.Equal(["sysmanager-20260918.log"], NewService().NewestLogs().Select(f => f.Name));
    }

    // ── Truncation ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadTail_OnASmallLog_ReturnsItWhole()
    {
        var path = NewLog("20260918", "line one\nline two\nline three\n");

        var text = DiagnosticsBundleService.ReadTail(new FileInfo(path));

        Assert.NotNull(text);
        Assert.Contains("line one", text, StringComparison.Ordinal);
        Assert.Contains("line three", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left out", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An oversized log is cut from the FRONT, keeping the end.
    /// </summary>
    /// <remarks>
    /// A log is chronological, so the failure being reported is at the tail. Truncating the other way round
    /// would compile, pass a size check, and reliably discard the only part anyone needs — which is why this
    /// asserts the last line survives and the first is gone, rather than just asserting a size.
    /// </remarks>
    [Fact]
    public void ReadTail_OnAnOversizedLog_KeepsTheEndAndSaysWhatItDropped()
    {
        var filler = new StringBuilder();
        filler.AppendLine("FIRST-LINE-MARKER");
        // A second marker a few lines in, NOT on line one. The first version of this test put its only
        // early marker on line one — which the implementation drops anyway, to remove the partial line a
        // byte-offset read starts on — so a mutation that read the whole file and dropped just that line
        // passed. The ritual caught it; this marker and the size assertion below are what closed it.
        filler.AppendLine("EARLY-MARKER-NOT-ON-LINE-ONE");
        while (filler.Length < DiagnosticsBundleService.MaxBytesPerLog * 2)
            filler.AppendLine("2026-09-18 06:00:00.000 [INF] a routine line of the kind a log is full of");
        filler.AppendLine("LAST-LINE-MARKER");
        var path = NewLog("20260918", filler.ToString());
        var fileBytes = new FileInfo(path).Length;

        var text = DiagnosticsBundleService.ReadTail(new FileInfo(path))!;

        Assert.Contains("LAST-LINE-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST-LINE-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EARLY-MARKER-NOT-ON-LINE-ONE", text, StringComparison.Ordinal);
        Assert.Contains("were left out", text, StringComparison.Ordinal);
        // The note replaces the partial first line rather than sitting above it: reading from a byte offset
        // lands mid-line, and half a timestamp reads as corruption rather than as a cut.
        Assert.StartsWith("[SysManager:", text, StringComparison.Ordinal);

        // The size is what actually distinguishes "took the tail" from "read it all". The note adds a couple
        // of hundred characters, so the allowance is small and deliberate rather than round.
        Assert.True(text.Length < DiagnosticsBundleService.MaxBytesPerLog + 400,
            $"the tail read returned {text.Length} characters from a {fileBytes}-byte log — the cap did not "
            + "apply, so the whole file went into the bundle.");
    }

    /// <summary>
    /// The log the sink currently holds open for writing is still readable.
    /// </summary>
    /// <remarks>
    /// Without <c>FileShare.ReadWrite</c> this throws, and the file it throws on is TODAY's — the one most
    /// likely to hold the failure being reported. The bundle would have shipped every log except the one
    /// that mattered, and a size check would not have noticed.
    /// </remarks>
    [Fact]
    public void ReadTail_OnALogSomethingElseIsWritingTo_StillReads()
    {
        var path = NewLog("20260918", "already written\n");
        using var holder = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var text = DiagnosticsBundleService.ReadTail(new FileInfo(path));

        Assert.NotNull(text);
        Assert.Contains("already written", text, StringComparison.Ordinal);
    }

    // ── The README ───────────────────────────────────────────────────────────

    /// <summary>
    /// The note inside says nothing was sent, names every file, and states what was withheld.
    /// </summary>
    /// <remarks>
    /// This is the part a careful user reads before attaching the zip, and the careful users are the ones who
    /// file good bugs. "Redacted" is only reassuring if it says what and why, so the omissions are asserted
    /// as text rather than assumed.
    /// </remarks>
    [Fact]
    public void Readme_SaysNothingWasSent_NamesTheLogs_AndStatesWhatWasWithheld()
    {
        var logs = new List<FileInfo>
        {
            new(NewLog("20260918", "x")),
            new(NewLog("20260917", "y")),
        };

        var readme = DiagnosticsBundleService.Readme(logs);

        Assert.Contains("NOTHING HAS BEEN SENT ANYWHERE.", readme, StringComparison.Ordinal);
        Assert.Contains("sysmanager-20260918.log", readme, StringComparison.Ordinal);
        Assert.Contains("sysmanager-20260917.log", readme, StringComparison.Ordinal);
        Assert.Contains("2 most recent", readme, StringComparison.Ordinal);
        Assert.Contains("(MAC) addresses are not included", readme, StringComparison.Ordinal);
        Assert.Contains("Windows user name is removed", readme, StringComparison.Ordinal);
        // The logs record what the user did in the app. Saying so is what lets them decide.
        Assert.Contains("which tabs you opened", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_WithNoLogs_SaysSoRatherThanPromisingAFolder()
    {
        var readme = DiagnosticsBundleService.Readme([]);

        Assert.Contains("no log files", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("most recent daily log files", readme, StringComparison.Ordinal);
    }

    // ── The archive ──────────────────────────────────────────────────────────

    /// <summary>
    /// The written zip holds every promised entry, and the log entries carry the log text.
    /// </summary>
    /// <remarks>
    /// End to end through the real <c>ZipArchive</c> rather than asserting on the pieces, because the entry
    /// NAMES are part of the contract the README states — a reader told to look in <c>logs/</c> has to find
    /// it there.
    /// </remarks>
    [Fact]
    public async Task PackAsync_ProducesAZipHoldingTheReportEnvironmentAndLogs()
    {
        NewLog("20260918", "MARKER-TODAY");
        NewLog("20260917", "MARKER-YESTERDAY");
        var zipPath = Path.Combine(_outDir, "bundle.zip");

        var contents = await NewService().PackAsync(zipPath, "ENVIRONMENT-MARKER", CannedReport);

        Assert.Equal(2, contents.LogFilesIncluded);
        Assert.True(contents.TotalBytes > 0);
        Assert.True(File.Exists(zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("README.txt", names);
        Assert.Contains("environment.txt", names);
        Assert.Contains("system-report.txt", names);
        Assert.Contains("logs/sysmanager-20260918.log", names);
        Assert.Contains("logs/sysmanager-20260917.log", names);

        Assert.Contains("ENVIRONMENT-MARKER", ReadEntry(zip, "environment.txt"), StringComparison.Ordinal);
        Assert.Contains("MARKER-TODAY", ReadEntry(zip, "logs/sysmanager-20260918.log"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackAsync_OverAnExistingFile_ReplacesIt()
    {
        var zipPath = Path.Combine(_outDir, "twice.zip");
        await File.WriteAllTextAsync(zipPath, "not a zip at all");

        await NewService().PackAsync(zipPath, "env", CannedReport);

        // Would throw "not a Zip archive" if the old bytes had survived at the front.
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Contains("README.txt", zip.Entries.Select(e => e.FullName));
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── The masking rule itself ──────────────────────────────────────────────

    /// <summary>
    /// Masking keeps the network half of an address and drops the host half.
    /// </summary>
    /// <remarks>
    /// "Is there a private address, a static one, or a <c>169.254</c> self-assigned one" is the actual signal
    /// in a report attached to a "no internet" complaint; the host number answers nothing.
    /// <para>The last two rows are the reason this masks the DATA rather than pattern-matching the finished
    /// report: a four-part version string has four valid octets, so a generic IPv4 regex over the rendered
    /// text would have quietly rewritten the version line while claiming to protect an address.</para>
    /// </remarks>
    [Theory]
    [InlineData("192.168.1.42", "192.168.x.x")]
    [InlineData("10.0.0.7", "10.0.x.x")]
    [InlineData("169.254.13.201", "169.254.x.x")]
    [InlineData("", "")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("not an address", "not an address")]
    public void MaskHostPart_KeepsTheNetworkAndDropsTheHost(string input, string expected)
    {
        Assert.Equal(expected, SystemReportService.MaskHostPart(input));
    }
}
