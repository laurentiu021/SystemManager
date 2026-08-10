// SysManager · ActivityLogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
    public void Load_WhenTheFileIsMalformed_StartsEmptyRatherThanThrowing()
    {
        File.WriteAllText(StoreFile, "{ not json");

        var log = new ActivityLogService(_dir);

        Assert.Empty(log.GetRecent(10));
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
