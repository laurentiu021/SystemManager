// SysManager · DiagnosticsBundleService — one zip a user can attach to a bug report
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Collects the evidence a bug report needs into a single zip the user chooses where to save.
/// </summary>
/// <remarks>
/// Producing that evidence used to take four manual steps: copy the environment block, export the full
/// report, then leave the app, open <c>%LOCALAPPDATA%\SysManager\logs\</c> in Explorer and work out which of
/// up to fourteen daily rolling files covers the moment the bug happened. The target user cannot do the last
/// two, so their bug was simply unreportable (#1650).
/// <para><b>Nothing is uploaded, ever.</b> This writes a file to a path the user picked and stops. There is
/// no network code in this class and there is not going to be any — the bundle is something they attach if
/// they choose to, which is what keeps it inside the no-telemetry promise rather than a hole in it.</para>
/// <para>The report inside is the SHARABLE one, with the adapter MAC and the IPv4 host part removed. A MAC
/// address is permanent and cannot be un-published, and this bundle is built specifically to be posted in a
/// public thread — so redacting it is not caution, it is the difference between a safe artifact and a trap.
/// The logs need no such treatment: the sink strips the Windows user name from every line, message,
/// property and exception text alike, before it reaches disk.</para>
/// </remarks>
public sealed partial class DiagnosticsBundleService
{
    /// <summary>
    /// How many of the newest rolling log files go in.
    /// </summary>
    /// <remarks>
    /// Three, against the fourteen the sink retains. A bug being reported now is in today's file or
    /// yesterday's; the third is there for "it started happening at the weekend". Fourteen would make the
    /// zip large enough that GitHub's attachment limit becomes the reason the bug goes unreported, which is
    /// the problem this class exists to remove.
    /// </remarks>
    internal const int NewestLogFiles = 3;

    /// <summary>
    /// The most bytes taken from any one log file.
    /// </summary>
    /// <remarks>
    /// Taken from the END of the file, not the beginning. A log is chronological, so the failure being
    /// reported is at the tail — truncating from the front would reliably discard the only part that
    /// matters. The header line of the truncated entry says how much was dropped, so nobody reads a partial
    /// file believing it is whole.
    /// </remarks>
    internal const long MaxBytesPerLog = 512 * 1024;

    /// <summary>
    /// The most bytes <see cref="BuildErrorsText"/> collects before it stops reading further logs.
    /// </summary>
    /// <remarks>
    /// Smaller than <see cref="MaxBytesPerLog"/>: an [ERR] or [FTL] line is rare, so this much of them
    /// is a lot of distinct failures, and the point of errors.txt is reach — every retained log, not
    /// just the newest three — not completeness past what a reader could get through.
    /// </remarks>
    internal const long MaxErrorBytes = 256 * 1024;

    private readonly SystemReportService _report;
    private readonly string _logDir;

    /// <summary>Creates a bundle writer over a report source and a log directory.</summary>
    /// <param name="report">Supplies the sharable system report.</param>
    /// <param name="logDir">
    /// Where the rolling logs live. Overridable so a test can point it at a temp directory: the default
    /// resolves through <see cref="LogService.LogDir"/>, and a test that used it would read — and a careless
    /// one could write — the developer's own log folder.
    /// </param>
    public DiagnosticsBundleService(SystemReportService report, string? logDir = null)
    {
        _report = report;
        _logDir = logDir ?? LogService.LogDir;
    }

    /// <summary>What went into the bundle, so the caller can say so rather than guess.</summary>
    public sealed record BundleContents(int LogFilesIncluded, long TotalBytes);

    /// <summary>
    /// Writes the bundle to <paramref name="zipPath"/>, replacing any file already there.
    /// </summary>
    /// <param name="zipPath">The destination the user chose.</param>
    /// <param name="environment">
    /// The environment block the About tab already builds for the clipboard. Passed in rather than rebuilt
    /// here because it reads WMI on the UI side and this class has no business doing that twice.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<BundleContents> WriteAsync(string zipPath, string environment, CancellationToken ct = default)
        // GenerateReportAsync, not a separate sharable variant: redaction moved into the report service's
        // single data path, so every format is redacted and the variant this used to call was identical to
        // the ordinary one (#2352). One method is one fewer thing that can drift.
        => await PackAsync(zipPath, environment,
                           await _report.GenerateReportAsync(ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Packages an already-gathered report. Split out so the packaging is testable in the unit suite.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteAsync"/> reads WMI twice through the report service, which makes it a system
    /// dependency and therefore an integration test — and the parts most likely to be wrong here (which logs
    /// are chosen, which end of an oversized one survives, what the README claims) have nothing to do with
    /// hardware. Internal rather than public so the redaction decision stays on the one path a caller can
    /// reach: a public overload taking a report would let the next caller hand it the full one.
    /// </remarks>
    internal async Task<BundleContents> PackAsync(
        string zipPath, string environment, string report, CancellationToken ct = default)
    {
        var logs = NewestLogs();

        // Built in memory and written once. Creating the archive directly at the destination would leave a
        // half-written zip behind if the report or a log read threw part-way, and a corrupt file the user
        // then attaches is worse than no file at all.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, "README.txt", Readme(logs), ct).ConfigureAwait(false);
            await WriteEntryAsync(zip, "environment.txt", environment, ct).ConfigureAwait(false);
            await WriteEntryAsync(zip, "system-report.txt", report, ct).ConfigureAwait(false);
            await WriteEntryAsync(zip, "errors.txt", BuildErrorsText(_logDir, ct), ct).ConfigureAwait(false);

            foreach (var log in logs)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadTail(log) is not { } text) continue;
                await WriteEntryAsync(zip, $"logs/{log.Name}", text, ct).ConfigureAwait(false);
            }
        }

        var bytes = buffer.ToArray();
        // Atomic, like every other user-data write in the app. A torn zip is the worst possible outcome here:
        // the user attaches it, the person reading the bug cannot open it, and the round trip that this whole
        // feature exists to shorten gets longer instead.
        await Helpers.AtomicFile.WriteAllBytesAsync(zipPath, bytes, ct).ConfigureAwait(false);

        Log.Information("Diagnostics bundle written: {LogCount} log files, {Bytes} bytes", logs.Count, bytes.Length);
        return new BundleContents(logs.Count, bytes.Length);
    }

    /// <summary>
    /// The newest <see cref="NewestLogFiles"/> rolling logs, newest first, or an empty list when the folder
    /// does not exist yet.
    /// </summary>
    /// <remarks>
    /// Ordered by the name rather than by <c>LastWriteTime</c>. The names are
    /// <c>sysmanager-YYYYMMDD.log</c>, so they sort chronologically as strings, and a timestamp can be
    /// rewritten by a backup tool or a sync client touching the file — which would silently pick the wrong
    /// three.
    /// </remarks>
    internal List<FileInfo> NewestLogs()
    {
        if (!Directory.Exists(_logDir)) return [];

        try
        {
            return [.. new DirectoryInfo(_logDir)
                .EnumerateFiles("sysmanager-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(NewestLogFiles)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A bundle without logs is still worth having: the report and the environment block answer most
            // of a bug report on their own, and failing the whole export because one folder is unreadable
            // would put the user back where they started.
            Log.Warning("Diagnostics bundle could not list the log folder: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The last <see cref="MaxBytesPerLog"/> bytes of a log, or the whole file when it is smaller. Null when
    /// the file cannot be read.
    /// </summary>
    /// <remarks>
    /// Opened with <c>FileShare.ReadWrite</c> because the sink holds the current day's file open for
    /// writing; without it every bundle would silently omit the one log most likely to contain the failure.
    /// <para>The first partial line is dropped after a truncation. Reading from a byte offset lands
    /// mid-line, and half a timestamp at the top of a file reads as corruption rather than as a cut.</para>
    /// </remarks>
    internal static string? ReadTail(FileInfo log)
    {
        try
        {
            using var stream = new FileStream(
                log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var skipped = 0L;
            if (stream.Length > MaxBytesPerLog)
            {
                skipped = stream.Length - MaxBytesPerLog;
                stream.Seek(skipped, SeekOrigin.Begin);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            if (skipped == 0) return text;

            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0) text = text[(firstBreak + 1)..];

            // One interpolated string, not a concatenation: `string.Create(provider, $"…" + "…")` binds the
            // argument as a plain string and stops resolving to the interpolated-handler overload.
            var note = string.Create(
                CultureInfo.InvariantCulture,
                $"[SysManager: the first {skipped / 1024} KB of this log were left out to keep the bundle small enough to attach. What follows is the most recent activity.]");
            return note + Environment.NewLine + text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Diagnostics bundle skipped a log file: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Every <c>[ERR]</c> and <c>[FTL]</c> line and its continuation lines (an exception's stack trace,
    /// or a multi-line message), across EVERY retained log — not only the <see cref="NewestLogFiles"/>
    /// whole copies above, which can leave an early or old error out entirely (#2861).
    /// </summary>
    /// <remarks>
    /// Newest file first, read line by line rather than loaded whole: a retained file can be up to
    /// <see cref="LogService.MaxLogFileBytes"/> (10 MB), and only the rare <c>[ERR]</c>/<c>[FTL]</c>
    /// lines are kept. A line that does not start a new timestamped entry is a continuation of
    /// whichever entry came before it — that is how Serilog renders an exception's stack trace and any
    /// embedded newline in a message, with no timestamp of its own (<see cref="LogService"/>'s sink).
    /// Stops once <see cref="MaxErrorBytes"/> is reached, so one log full of the same repeated failure
    /// cannot crowd out a different one in an older file.
    /// </remarks>
    internal static string BuildErrorsText(string logDir, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        if (!Directory.Exists(logDir)) return "No log files were found to check for errors.";

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(logDir, "sysmanager-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Diagnostics bundle could not list the log folder for errors.txt: {Error}", ex.Message);
            return "The log folder could not be read.";
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length >= MaxErrorBytes) break;
            AppendErrorLines(file, sb);
        }

        return sb.Length > 0 ? sb.ToString() : "No [ERR] or [FTL] lines were found in any retained log.";
    }

    private static void AppendErrorLines(string path, StringBuilder sb)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var collecting = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var entryStart = LogEntryStart().Match(line);
                if (entryStart.Success)
                    collecting = entryStart.Groups[1].Value is "ERR" or "FTL";

                if (!collecting) continue;

                sb.AppendLine(line);
                if (sb.Length >= MaxErrorBytes) return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Diagnostics bundle skipped a log file while building errors.txt: {Error}", ex.Message);
        }
    }

    /// <summary>The start of a log line Serilog's sink writes — any level, not only ERR/FTL.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(\w{3})\] ")]
    private static partial Regex LogEntryStart();

    /// <summary>
    /// The note that ships inside the zip, naming every file and stating plainly that nothing was sent.
    /// </summary>
    /// <remarks>
    /// Written for the person who opens the zip before attaching it, and that reader is the point: a bundle
    /// whose contents you have to take on trust is one a careful user will not send, and the careful users
    /// are the ones who file good bugs. It also says what was removed, because "redacted" is only reassuring
    /// if it says what and why.
    /// </remarks>
    internal static string Readme(IReadOnlyList<FileInfo> logs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SysManager diagnostics bundle");
        var created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        sb.AppendLine($"Created {created} by SysManager v{UpdateService.CurrentVersion.ToString(3)}");
        sb.AppendLine();
        sb.AppendLine("NOTHING HAS BEEN SENT ANYWHERE.");
        sb.AppendLine("SysManager wrote this file where you asked it to and stopped. It has no server to send");
        sb.AppendLine("anything to. Attach it to a bug report if you want to; delete it if you do not.");
        sb.AppendLine();
        sb.AppendLine("What is in here");
        sb.AppendLine("  environment.txt    SysManager and Windows versions, architecture, whether SysManager");
        sb.AppendLine("                     was running as administrator, your Windows language, and your CPU");
        sb.AppendLine("                     and memory.");
        sb.AppendLine("  system-report.txt  The full system report: operating system, processor, memory,");
        sb.AppendLine("                     graphics, motherboard, drives and their health, network adapters.");
        sb.AppendLine("  errors.txt         Every line the log marked [ERR] or [FTL], with its continuation");
        sb.AppendLine("                     lines, from EVERY log file still kept on this PC — not only the");
        sb.AppendLine("                     ones below — so an older error is not left out.");

        if (logs.Count == 0)
        {
            sb.AppendLine("  (no log files)     SysManager found no log files to include.");
        }
        else
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  logs/              The {logs.Count} most recent daily log files:"));
            foreach (var log in logs) sb.AppendLine($"                       {log.Name}");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"                     A log larger than {MaxBytesPerLog / 1024} KB is cut to its most recent"));
            sb.AppendLine("                     activity, and says so at the top of the file.");
        }

        sb.AppendLine();
        sb.AppendLine("What has been left out on purpose");
        sb.AppendLine("  Your network adapters' hardware (MAC) addresses are not included, and the last two");
        sb.AppendLine("  parts of each local IP address are replaced with x. A MAC address identifies your");
        sb.AppendLine("  hardware permanently and cannot be taken back once it is posted in public, and");
        sb.AppendLine("  neither it nor your exact IP answers any question a bug report asks.");
        sb.AppendLine();
        sb.AppendLine("  Your Windows user name is removed from every log line before it is written to disk,");
        sb.AppendLine("  so file paths in the logs read [user] rather than naming you.");
        sb.AppendLine();
        sb.AppendLine("Worth a look before you attach it");
        sb.AppendLine("  The logs record what you did in SysManager — which tabs you opened, which cleanups");
        sb.AppendLine("  you ran, which programs you removed. That is what makes them useful for finding a");
        sb.AppendLine("  bug. Open them if you would rather check first.");
        return sb.ToString();
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }
}
