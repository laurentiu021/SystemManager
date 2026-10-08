// SysManager · ActivityLogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Two halves.
/// <para>
/// First, the store itself, exercised through the <c>configDir</c> seam so nothing here can touch the
/// user's real <c>activity.json</c>. Before that seam the path was <c>static readonly</c> and derived
/// from <see cref="Environment.SpecialFolder.LocalApplicationData"/> — which ignores the
/// <c>LOCALAPPDATA</c> environment variable — so any test calling <see cref="ActivityLogService.Log"/>
/// would have appended to the user's own history.
/// </para>
/// <para>
/// Second, a source-level guard that every destructive command still logs. <c>Instance</c> is a
/// get-only singleton, so a ViewModel test cannot redirect the store and would write to the real file;
/// asserting on the source is the honest way to pin "these six call the log" without that side effect.
/// </para>
/// </summary>
public sealed class ActivityLogServiceTests : IDisposable
{
    private readonly string _dir;

    public ActivityLogServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private ActivityLogService NewLog() => new(_dir);

    private string StoreFile => Path.Combine(_dir, "activity.json");

    // ── the store ───────────────────────────────────────────────────────────

    [Fact]
    public void Log_WritesTheEntry_AndPersistsIt()
    {
        var log = NewLog();

        log.Log("Deep Cleanup", "Freed 1.2 GB across 400 files");

        var only = Assert.Single(log.GetRecent(10));
        Assert.Equal("Deep Cleanup", only.Action);
        Assert.Equal("Freed 1.2 GB across 400 files", only.Detail);
        Assert.True(File.Exists(StoreFile));

        // A second instance over the same directory must see it — i.e. it really round-trips to disk.
        Assert.Single(new ActivityLogService(_dir).GetRecent(10));
    }

    [Fact]
    public void Log_PutsTheNewestFirst()
    {
        var log = NewLog();

        log.Log("First", "a");
        log.Log("Second", "b");

        var recent = log.GetRecent(10);
        Assert.Equal("Second", recent[0].Action);
        Assert.Equal("First", recent[1].Action);
    }

    [Fact]
    public void Log_KeepsAtMostMaxEntries_DiscardingTheOldest()
    {
        var log = NewLog();

        for (int i = 0; i < ActivityLogService.MaxEntries + 15; i++)
            log.Log($"Action {i}", "d");

        var all = log.GetRecent(int.MaxValue);
        Assert.Equal(ActivityLogService.MaxEntries, all.Count);
        Assert.Equal($"Action {ActivityLogService.MaxEntries + 14}", all[0].Action);   // newest kept
        Assert.DoesNotContain(all, e => e.Action == "Action 0");                       // oldest dropped
    }

    [Fact]
    public void MaxEntries_LeavesRoomForRealActionsAfterNavigationStoppedLogging()
    {
        // The cap used to be 20 while every tab open wrote an entry, so a few minutes of clicking
        // evicted any real action. Navigation no longer logs; this pins the headroom so the cap cannot
        // quietly be lowered back to a value where one busy session buries a Deep Cleanup.
        Assert.True(ActivityLogService.MaxEntries >= 50,
            $"MaxEntries is {ActivityLogService.MaxEntries}; the activity log is the only record of what " +
            "the app changed, so it needs room for more than a handful of actions.");
    }

    [Fact]
    public void MaxEntries_HoldsWhatRecentChangesLooksBackOver()
    {
        // Recent Changes lists SysManager's own actions from this log over as long as 90 days (#1507). At 60 a busy
        // week pushed the start of the period out of the log, and the tab would say nothing was done.
        Assert.True(ActivityLogService.MaxEntries >= 200,
            $"MaxEntries is {ActivityLogService.MaxEntries}; Recent Changes looks back {RecentChangesService.LongestPeriodDays} days.");
    }

    [Fact]
    public void Load_WhenTheFileIsMalformed_StartsEmptyRatherThanThrowing()
    {
        File.WriteAllText(StoreFile, "{ not json");

        var log = new ActivityLogService(_dir);

        Assert.Empty(log.GetRecent(10));
    }

    // ── a file that cannot be read or parsed (#2521) ────────────────────────
    // A failed read gave Log this instance's own list, which it wrote over the file, so a read that failed at
    // startup lost the whole history. The file is held open with delete sharing only while a read must fail: the
    // read fails, and the replace a write ends with would still succeed.

    private FileStream HoldAgainstReads() => new(StoreFile, FileMode.Open, FileAccess.Read, FileShare.Delete);

    [Fact]
    public void Log_WhenNoReadHasWorked_DoesNotReplaceTheHistory()
    {
        NewLog().Log("Scheduled cleanup", "Freed 300 MB");   // written by another run
        var before = File.ReadAllBytes(StoreFile);

        ActivityLogService gui;
        using (HoldAgainstReads())
        {
            gui = NewLog();   // its read at startup fails
            gui.Log("Quick Cleanup", "Freed 12 MB");
        }

        Assert.Equal(before, File.ReadAllBytes(StoreFile));
        gui.Log("Deep Cleanup", "Freed 2 GB");
        Assert.Equal(["Deep Cleanup", "Quick Cleanup", "Scheduled cleanup"],
            new ActivityLogService(_dir).GetRecent(10).Select(e => e.Action));
    }

    [Fact]
    public void Log_WhenTheFileCannotBeRead_ListsTheEntry_AndWritesItWithTheNextLog()
    {
        var log = NewLog();
        log.Log("A", "d");
        log.Log("B", "d");
        var before = File.ReadAllBytes(StoreFile);

        using (HoldAgainstReads())
        {
            log.Log("C", "d");
            Assert.Equal(["C", "B", "A"], log.GetRecent(10).Select(e => e.Action));
        }

        Assert.Equal(before, File.ReadAllBytes(StoreFile));
        log.Log("D", "d");
        Assert.Equal(["D", "C", "B", "A"], new ActivityLogService(_dir).GetRecent(10).Select(e => e.Action));
    }

    [Fact]
    public void Log_OverAFileThatDoesNotParse_KeepsItAside_AndLogs()
    {
        File.WriteAllText(StoreFile, "{ not json");
        var log = NewLog();

        log.Log("A", "d");

        Assert.Equal("{ not json", File.ReadAllText(StoreFile + ".unreadable"));
        Assert.Equal("A", Assert.Single(new ActivityLogService(_dir).GetRecent(10)).Action);
    }

    [Fact]
    public void Log_WhenAFileThatDoesNotParseCannotBeSetAside_WritesNothing_AndListsTheEntry()
    {
        File.WriteAllText(StoreFile, "{ not json");
        // A folder where the set-aside copy would go: the move fails, and a write to the file itself would not.
        Directory.CreateDirectory(StoreFile + ".unreadable");
        var log = NewLog();

        log.Log("A", "d");

        Assert.Equal("{ not json", File.ReadAllText(StoreFile));
        Assert.Equal("A", Assert.Single(log.GetRecent(10)).Action);   // it waits, and is listed meanwhile
    }

    [Fact]
    public void GetRecent_ReturnsAtMostTheRequestedCount()
    {
        var log = NewLog();
        for (int i = 0; i < 8; i++) log.Log($"A{i}", "d");

        Assert.Equal(5, log.GetRecent().Count);      // default is 5, what the Dashboard card shows
        Assert.Equal(3, log.GetRecent(3).Count);
    }

    [Fact]
    public void TwoInstances_OnDifferentDirectories_DoNotSeeEachOther()
    {
        // Proves the seam isolates — the whole reason a test may call Log() at all.
        var otherDir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDir);
        try
        {
            var a = NewLog();
            var b = new ActivityLogService(otherDir);

            a.Log("Only in A", "d");

            Assert.Single(a.GetRecent(10));
            Assert.Empty(b.GetRecent(10));
        }
        finally { Directory.Delete(otherDir, recursive: true); }
    }

    // ── another process writes the same file (#2478) ────────────────────────
    // A command-line or scheduled run is a separate process with its own instance over the same activity.json.
    // Two instances over one directory are exactly that, without the process boundary.

    [Fact]
    public void Log_KeepsAnEntryAnotherProcessWroteAfterThisOneLoaded()
    {
        var gui = NewLog();        // SysManager, open since before the run
        var headless = NewLog();   // the scheduled cleanup, a second process

        headless.Log("Scheduled cleanup", "Freed 300 MB");
        gui.Log("Quick Cleanup", "Freed 12 MB");

        Assert.Equal(["Quick Cleanup", "Scheduled cleanup"],
            new ActivityLogService(_dir).GetRecent(10).Select(e => e.Action));
    }

    [Fact]
    public void GetRecent_ShowsAnEntryAnotherProcessWroteAfterThisOneLoaded()
    {
        var gui = NewLog();
        NewLog().Log("Scheduled cleanup", "Freed 300 MB");

        Assert.Equal("Scheduled cleanup", Assert.Single(gui.GetRecent(10)).Action);
    }

    [Fact]
    public async Task TwoProcessesLoggingAtOnce_LoseNothing()
    {
        // Reading before writing is not enough on its own: two writers that both read before either writes still
        // lose an entry. Each instance has its own in-process lock, so only the store lock stands between these
        // two. The long wait keeps a stalled runner from turning contention into an unlocked write.
        var a = new ActivityLogService(_dir, lockWait: StartLine.Bound);
        var b = new ActivityLogService(_dir, lockWait: StartLine.Bound);
        // Ten each, down from 25 (#2548). The store lock makes the writes take turns, so the race lasts as long as all of
        // them together: 1.3 to 2.6 seconds for 50 on a normal CI run, and more than the 30-second bound on the two runs
        // that failed. Twenty writes fit in the bound on a runner nearly 30 times slower than the slowest normal run, and
        // a missing store lock still loses entries in every run: 20 of 20 at 5, 10 and 25 each alike.
        const int each = 10;
        var took = new LogTimings();

        try
        {
            await StartLine.RaceAsync(
                () => { for (var i = 0; i < each; i++) took.Time("A", () => a.Log("A", $"{i}")); },
                () => { for (var i = 0; i < each; i++) took.Time("B", () => b.Log("B", $"{i}")); });
        }
        finally
        {
            // What each Log took, and when, pass or fail, in the test's output: the report a slow run needs.
            TestContext.Current.TestOutputHelper?.WriteLine(took.Report());
        }

        var all = new ActivityLogService(_dir).GetRecent(int.MaxValue);
        Assert.Equal(2 * each, all.Count);
        Assert.Equal(each, all.Count(e => e.Action == "A"));
    }

    /// <summary>When each Log in a race started and how long it took, per writer.</summary>
    private sealed class LogTimings
    {
        private readonly long _origin = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<(string Writer, double At, double Took)> _calls = new();

        public void Time(string writer, Action log)
        {
            var started = Stopwatch.GetTimestamp();
            log();
            var ended = Stopwatch.GetTimestamp();
            _calls.Enqueue((writer, Stopwatch.GetElapsedTime(_origin, started).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds));
        }

        /// <summary>
        /// Per writer: how many calls, the median and the longest, then every call as start+duration in milliseconds
        /// since the timings began, so the two writers can be laid side by side: one waiting while the other writes
        /// looks different from both being slow.
        /// </summary>
        public string Report() => string.Join(Environment.NewLine, _calls
            .GroupBy(c => c.Writer)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var sorted = g.Select(c => c.Took).Order().ToArray();
                var calls = string.Join(" ", g.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.At:F0}+{c.Took:F0}")));
                return string.Create(CultureInfo.InvariantCulture,
                    $"{g.Key}: {sorted.Length} logs, median {sorted[sorted.Length / 2]:F0} ms, max {sorted[^1]:F0} ms: {calls}");
            }));
    }

    [Fact]
    public void TheLockTimings_AreConstants()
    {
        // The Instance singleton is built by a static initializer declared ABOVE these, and its constructor reads
        // them. As static readonly fields they would still be zero at that moment, and the production instance
        // would get one attempt at the lock instead of a two-second wait. A test instance, built after type
        // initialisation, could not show that, so the shape is what is pinned.
        foreach (var name in new[] { nameof(ActivityLogService.DefaultLockWaitMs), nameof(ActivityLogService.LockRetryDelayMs) })
        {
            var field = typeof(ActivityLogService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(field is { IsLiteral: true }, $"{name} must stay a const: {field?.FieldType.Name ?? "missing"}");
        }
    }

    // ── the six destructive commands must log ───────────────────────────────

    /// <summary>
    /// Source path of a ViewModel in the app project, resolved from the test assembly location so it
    /// works from any working directory.
    /// </summary>
    private static string ViewModelSource(string name)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ActivityLogService).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "SysManager", "ViewModels", name + ".cs");
    }

    [Theory]
    [InlineData("DeepCleanupViewModel")]
    [InlineData("BrowserCleanerViewModel")]
    [InlineData("PrivacyViewModel")]
    [InlineData("ShortcutCleanerViewModel")]
    [InlineData("UninstallerViewModel")]
    [InlineData("FileShredderViewModel")]
    public void EveryDestructiveViewModel_WritesToTheActivityLog(string viewModel)
    {
        // These six are the least reversible operations in the app — a permanent delete, a browser
        // clean that signs the user out, registry privacy writes, an uninstall, an unrecoverable
        // shred — and every one of them used to leave no trace in the app's own history, while merely
        // OPENING a tab logged an entry.
        var path = ViewModelSource(viewModel);
        Assert.True(File.Exists(path), $"could not locate {viewModel}.cs (looked at {path})");

        var source = File.ReadAllText(path);

        Assert.Contains("ActivityLogService.Instance.Log(", source);
    }

    [Fact]
    public void FileShredder_LogsNoFileNameOrPath()
    {
        // THE load-bearing privacy check. activity.json is plain text under %LocalAppData%, so recording
        // the name of a file the user chose to destroy beyond recovery would leave behind exactly the
        // evidence the shred was meant to erase — and it would outlive the file. Only counts and the
        // pass count may appear.
        var source = File.ReadAllText(ViewModelSource("FileShredderViewModel"));

        var call = Regex.Match(source,
            @"ActivityLogService\.Instance\.Log\((?<args>.*?)\);",
            RegexOptions.Singleline);
        Assert.True(call.Success, "FileShredderViewModel does not call the activity log at all");

        var args = call.Groups["args"].Value;
        Assert.DoesNotContain("item.Path", args);
        Assert.DoesNotContain("item.Name", args);
        Assert.DoesNotContain(".Path", args);
        Assert.DoesNotContain(".Name", args);
    }

    [Fact]
    public void Navigation_DoesNotWriteToTheActivityLog()
    {
        // Tab opens are recorded in Serilog instead. If this ever comes back, the 20-entry-eviction
        // problem comes back with it and the destructive entries added here get buried again.
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(ActivityLogService).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(
            Path.Combine(dir!.FullName, "SysManager", "ViewModels", "MainWindowViewModel.cs"));

        Assert.DoesNotContain("Log(\"Opened\"", source);
    }
}
