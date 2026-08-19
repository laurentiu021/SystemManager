// SysManager · LogServiceSinkScrubbingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using Serilog;
using Serilog.Core;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Proves the log sink actually applies <see cref="LogService.SanitizePath"/> to what it writes.
/// <para>The existing <c>LogServiceSanitize*Tests</c> cover the regex itself; these cover the
/// wiring, which is where the defect was. <c>SanitizePath</c> existed and worked, but only 15 of
/// 75 path-logging call sites called it — so whether a user name reached the log depended on
/// which service happened to fail. Sanitizing at the sink is what makes that unconditional, and
/// only an end-to-end assertion over the written file can show it.</para>
/// <para>Each test writes through the real sink type into a temp file and reads the bytes back,
/// rather than asserting on an in-memory structure: the exception text is rendered by the sink,
/// so it is invisible to any check that inspects log-event properties.</para>
/// </summary>
public sealed class LogServiceSinkScrubbingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public LogServiceSinkScrubbingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerSinkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "probe.log");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    /// <summary>The real, private sink type from the app assembly — not a re-implementation.</summary>
    private ILogEventSink NewSink()
    {
        var sinkType = typeof(LogService).GetNestedType("UserPathScrubbingSink", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UserPathScrubbingSink not found — was it renamed?");
        return (ILogEventSink)Activator.CreateInstance(
            sinkType, [_logPath, RollingInterval.Infinite, 14])!;
    }

    private string WriteAndRead(Action<ILogger> write)
    {
        var sink = NewSink();
        using (var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger())
            write(logger);
        (sink as IDisposable)?.Dispose();
        return File.ReadAllText(_logPath);
    }

    /// <summary>This machine's Windows user name — what must never appear in the output.</summary>
    private static string CurrentUserName =>
        Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static string UserPath(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. parts]);

    [Fact]
    public void Sink_ScrubsAPathPassedAsAProperty()
    {
        // The shape of the ~60 call sites that never called SanitizePath themselves.
        var text = WriteAndRead(log =>
            log.Information("New app folder detected: {Path}", UserPath("AppData", "Local", "Thing")));

        Assert.DoesNotContain(CurrentUserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[user]", text);
    }

    [Fact]
    public void Sink_ScrubsAPathInsideExceptionText()
    {
        // The case a per-call-site fix cannot reach, and the one my first attempt at this fix
        // (a property enricher) missed: Serilog renders the exception separately from the
        // message properties, so the path never passes through a property at all.
        var text = WriteAndRead(log =>
        {
            try { throw new IOException($"Cannot open {UserPath("Documents", "x.txt")}"); }
            catch (IOException ex) { log.Warning(ex, "Failed to read {File}", "x.txt"); }
        });

        Assert.Contains("Cannot open", text);          // the exception is still logged
        Assert.DoesNotContain(CurrentUserName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sink_ScrubsAPathAppearingOnlyInTheMessageText()
    {
        // Some call sites interpolate the path into the message rather than passing it as a
        // property. Those are covered too, because the scrub runs over the rendered line.
        var text = WriteAndRead(log =>
            log.Information("Copied to " + UserPath("Desktop", "report.txt")));

        Assert.DoesNotContain(CurrentUserName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sink_LeavesNonPathTextAlone()
    {
        var text = WriteAndRead(log =>
        {
            log.Information("Blocked application: {ExeName}", "notepad.exe");
            log.Information("Pruned {Count} superseded update download(s)", 3);
        });

        Assert.Contains("notepad.exe", text);
        Assert.Contains("Pruned 3 superseded update download(s)", text);
    }

    [Fact]
    public void Sink_PreservesTheTimestampAndLevelFormat()
    {
        // Scrubbing must not quietly change the log format — the file is read by a human and
        // referenced in support instructions.
        var text = WriteAndRead(log =>
        {
            log.Information("info line");
            log.Warning("warning line");
            log.Debug("debug line");
        });

        Assert.Contains("[INF]", text);
        Assert.Contains("[WRN]", text);
        Assert.Contains("[DBG]", text);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INF\] info line", text);
    }

    [Fact]
    public void Sink_TreatsBracesInTextAsLiteralNotAsATemplate()
    {
        // The scrubbed line is re-written through the inner logger, so any braces in the text
        // must not be re-parsed as a message template (which would corrupt or drop the output).
        var text = WriteAndRead(log =>
            log.Information("Value was {Value}", "{not a template}"));

        Assert.Contains("{not a template}", text);
    }

    [Fact]
    public void Sink_ScrubsEveryOccurrenceOnOneLine()
    {
        var text = WriteAndRead(log =>
            log.Information("Moved {From} to {To}", UserPath("a.txt"), UserPath("b.txt")));

        Assert.DoesNotContain(CurrentUserName, text, StringComparison.OrdinalIgnoreCase);
        // Both paths were rewritten, not just the first.
        Assert.Equal(2, text.Split("[user]").Length - 1);
    }

    [Fact]
    public void Sink_HonoursTheOuterLoggerLevelFilter()
    {
        // The inner logger runs at Verbose so it never re-filters, which means the outer
        // logger's level must still be what decides. Otherwise raising MinimumLevel would
        // silently stop working.
        var sink = NewSink();
        using (var logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger())
        {
            logger.Debug("should not appear");
            logger.Warning("should appear");
        }
        (sink as IDisposable)?.Dispose();

        var text = File.ReadAllText(_logPath);
        Assert.DoesNotContain("should not appear", text);
        Assert.Contains("should appear", text);
    }

    [Fact]
    public void Sink_IsSafeUnderConcurrentWrites()
    {
        // The formatter and the "{Line}" template are shared statics — they were allocated per
        // event before, which cost an allocation and a constant-template re-parse on every log
        // call. Both are stateless, but that has to be proven rather than assumed: logging happens
        // from background scans, poll timers and the UI thread at once, so a shared mutable buffer
        // here would interleave or drop lines.
        const int writes = 400;

        var text = WriteAndRead(log =>
            Parallel.For(0, writes, i => log.Information("Line {Index} at {Path}", i, UserPath($"f{i}.txt"))));

        var lines = text.Split('\n').Where(l => l.Contains("[INF]")).ToArray();
        Assert.Equal(writes, lines.Length);
        Assert.DoesNotContain(CurrentUserName, text, StringComparison.OrdinalIgnoreCase);
        // Every line is individually intact — no two events rendered into each other.
        Assert.All(lines, line => Assert.Matches(@"\[INF\] Line \d+ at ", line));
    }

    // ── The startup line names the build ─────────────────────────────────────
    // release.yml greps this line to prove the published binary reports the tag it was built from.
    // It has to be read out of the log because the value at risk is the managed assembly version —
    // what UpdateService.CurrentVersion returns, and so what About, the update check, the
    // bug-report URL, the profile export and the system report all show. That version is absent
    // from the Win32 version resource and AssemblyName.GetAssemblyName cannot read it out of a
    // single-file bundle (it throws), so a launch is the only place it is observable. That makes
    // the rendered shape of this one line a contract with CI, not just cosmetics.

    [Fact]
    public void TheStartupLine_RendersTheVersionUnquoted()
    {
        // `{Message:lj}` renders string properties literally, so the line reads
        // "SysManager 1.65.19 started" and not "SysManager \"1.65.19\" started". The release gate
        // matches the bare form; a quoted version would break it, and would read as a placeholder
        // rather than a build in an attached support log.
        var text = WriteAndRead(log => log.Information(LogService.StartupMessage, "1.65.19"));

        Assert.Contains("SysManager 1.65.19 started", text);
        Assert.DoesNotContain("\"1.65.19\"", text);
    }

    // ── The log file is bounded ──────────────────────────────────────────────
    // The sink passed no fileSizeLimitBytes and no rollOnFileSizeLimit, so it took Serilog's
    // defaults: 1 GB per file with rolling OFF. The only bound on the folder was the 14-FILE count,
    // so one daily file could grow to a gigabyte. Debug is a real volume tier here (290 Log.Debug
    // call sites) and the documented support path is "attach the log" — an unattachable file breaks
    // the evidence trail exactly when it is needed.

    [Fact]
    public void TheSizeLimit_IsSmallEnoughToAttachToABugReport()
    {
        // 25 MB is GitHub's per-file attachment ceiling. A log the user cannot upload is the failure
        // this bound exists to prevent, so the intent is pinned rather than just the number.
        Assert.True(LogService.MaxLogFileBytes <= 25L * 1024 * 1024,
            $"A {LogService.MaxLogFileBytes / 1024 / 1024} MB log file is too large to attach to an issue.");
        Assert.True(LogService.MaxLogFileBytes >= 1024 * 1024,
            "Too small to hold useful context for a crash report.");
    }

    [Fact]
    public void TheWholeLogFolder_IsBounded()
    {
        // The point of the pair: per-file ceiling × retained count is a predictable worst case,
        // which is what the 14-file-count-alone version never gave.
        var worstCase = LogService.MaxLogFileBytes * LogService.RetainedFileCount;
        Assert.True(worstCase <= 250L * 1024 * 1024,
            $"Worst-case log folder is {worstCase / 1024 / 1024} MB — too much for a low-end laptop.");
    }

    [Fact]
    public void TheSink_KeepsEveryFileUnderTheLimit()
    {
        // Asserting the constants alone would pass even if the sink never received them — the defect
        // was a missing ARGUMENT, not a wrong number. So drive the real sink hard and check the files
        // it actually produced.
        //
        // RollingInterval.Infinite so the only thing that could create a second file is the size
        // limit: with a daily interval a date change could produce one and this would pass for the
        // wrong reason.
        var sinkType = typeof(LogService).GetNestedType("UserPathScrubbingSink", BindingFlags.NonPublic)!;
        var path = Path.Combine(_dir, "roll.log");
        var sink = (ILogEventSink)Activator.CreateInstance(
            sinkType, [path, RollingInterval.Infinite, LogService.RetainedFileCount])!;

        using (var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger())
        {
            var filler = new string('x', 1024);   // ~1 KB per line
            for (int i = 0; i < 2000; i++) logger.Information("{Index} {Filler}", i, filler);
        }
        (sink as IDisposable)?.Dispose();

        var written = Directory.GetFiles(_dir, "roll*.log");
        Assert.NotEmpty(written);
        Assert.All(written, f =>
            Assert.True(new FileInfo(f).Length <= LogService.MaxLogFileBytes + 64 * 1024,
                $"{Path.GetFileName(f)} is {new FileInfo(f).Length} bytes, over the {LogService.MaxLogFileBytes}-byte limit."));
    }
}
