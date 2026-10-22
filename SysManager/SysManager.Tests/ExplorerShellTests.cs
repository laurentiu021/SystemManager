// SysManager · ExplorerShellTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for the parts of <see cref="ExplorerShell"/> that can be tested: the cache sweep, against a
/// temp folder, and which Explorer processes a stop ends, against stand-in processes.
/// </summary>
/// <remarks>
/// <see cref="ExplorerShell.Stop()"/>/<see cref="ExplorerShell.Start"/> themselves are deliberately NOT
/// exercised — they end and relaunch the real desktop, which no test may do. Splitting the sweep out behind a
/// directory parameter, and the stop behind a list of processes and a session, is what makes the parts with
/// actual logic (which patterns, what is counted, what happens to a locked file, which shells are ended)
/// provable without touching the shell.
/// </remarks>
public class ExplorerShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sysmanager-explorershell-" + Guid.NewGuid().ToString("N"));

    public ExplorerShellTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a locked file from the sharing test — the temp folder is disposable */ }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, int bytes = 16)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void CacheFilePatterns_CoverBothCacheFamilies()
    {
        Assert.Contains("thumbcache_*.db", ExplorerShell.CacheFilePatterns);
        Assert.Contains("iconcache_*.db", ExplorerShell.CacheFilePatterns);
    }

    /// <summary>A property, so a caller cannot mutate the list every other caller relies on.</summary>
    [Fact]
    public void CacheFilePatterns_HandsOutACopy()
    {
        var first = ExplorerShell.CacheFilePatterns;
        first[0] = "mutated";

        Assert.DoesNotContain("mutated", ExplorerShell.CacheFilePatterns);
    }

    [Fact]
    public void DeleteCacheFiles_RemovesBothFamiliesAndCountsTheBytes()
    {
        Write("thumbcache_256.db", 100);
        Write("iconcache_32.db", 50);

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(2, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(150, sweep.BytesFreed);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// The load-bearing negative: Explorer's folder holds state Windows does NOT rebuild, so anything
    /// outside the two patterns must survive.
    /// </summary>
    [Fact]
    public void DeleteCacheFiles_LeavesEverythingElseAlone()
    {
        Write("thumbcache_256.db");
        var keep = new[]
        {
            Write("iconcache.db"),            // no underscore-suffix — not the pattern
            Write("thumbcache.db"),
            Write("startupInfo.bin"),
            Write("notacache.txt"),
            Write("thumbcache_256.db.bak"),   // pattern must anchor at the END
        };

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(1, sweep.Deleted);
        foreach (var path in keep)
        {
            Assert.True(File.Exists(path), $"{Path.GetFileName(path)} was deleted and should not have been.");
        }
    }

    /// <summary>
    /// A file another process holds open is counted as FAILED, never as freed — the whole point of the
    /// operation is that Explorer locks these, and reporting a byte it did not reclaim would be the one
    /// lie a repair tool must not tell.
    /// </summary>
    [Fact]
    public void DeleteCacheFiles_CountsALockedFileAsFailedNotFreed()
    {
        Write("thumbcache_free.db", 100);
        var locked = Write("thumbcache_locked.db", 999);

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var sweep = ExplorerShell.DeleteCacheFiles(_dir);

            Assert.Equal(1, sweep.Deleted);
            Assert.Equal(1, sweep.Failed);
            Assert.Equal(100, sweep.BytesFreed);   // the locked file's 999 bytes are NOT counted
        }

        Assert.True(File.Exists(locked), "the locked file should still be there");
    }

    /// <summary>Read-only is cleared first — <c>File.Delete</c> refuses one, and Windows marks some.</summary>
    [Fact]
    public void DeleteCacheFiles_RemovesAReadOnlyCacheFile()
    {
        var path = Write("iconcache_16.db", 40);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(1, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteCacheFiles_OnAnEmptyFolder_ReportsNothing()
    {
        var sweep = ExplorerShell.DeleteCacheFiles(_dir);

        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(0, sweep.BytesFreed);
    }

    /// <summary>A missing folder is not an error — it means Windows has already rebuilt everything.</summary>
    [Fact]
    public void DeleteCacheFiles_OnAMissingFolder_ReturnsEmptyRatherThanThrowing()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        var sweep = ExplorerShell.DeleteCacheFiles(missing);

        Assert.Equal(0, sweep.Deleted);
        Assert.Equal(0, sweep.Failed);
    }

    [Fact]
    public void CacheDirectory_IsExplorersOwnFolderUnderTheUsersProfile()
    {
        var dir = ExplorerShell.CacheDirectory;

        Assert.EndsWith(Path.Combine("Microsoft", "Windows", "Explorer"), dir, StringComparison.Ordinal);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            dir,
            StringComparison.Ordinal);
    }

    /// <summary>A stand-in Explorer: records what was done to it, and can refuse a step.</summary>
    private sealed class FakeShell(int id, int session) : ExplorerShell.IShellProcess
    {
        public Exception? SessionFails { get; init; }
        public Exception? KillFails { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public int Id => id;
        public int SessionId => SessionFails is { } failure ? throw failure : session;

        public void Kill()
        {
            if (KillFails is { } failure) throw failure;
            Killed = true;
        }

        public bool WaitForExit(int milliseconds) => true;
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A stop ends the Explorer in SysManager's own session and leaves another account's alone (#2821).
    /// </summary>
    /// <remarks>
    /// Run as administrator, SysManager can end another session's processes, so the old loop over every process
    /// named explorer ended the shell of every account signed in to the PC. Every process is disposed either way.
    /// </remarks>
    [Fact]
    public void Stop_EndsTheExplorerInItsOwnSession_AndLeavesAnotherAccountsAlone()
    {
        var own = new FakeShell(100, session: 2);
        var other = new FakeShell(200, session: 3);
        var console = new FakeShell(300, session: 1);

        ExplorerShell.Stop([own, other, console], ownSession: 2);

        Assert.True(own.Killed);
        Assert.False(other.Killed);
        Assert.False(console.Killed);
        Assert.All(new[] { own, other, console }, shell => Assert.True(shell.Disposed));
    }

    /// <summary>An Explorer whose session cannot be read is left running, and the loop goes on.</summary>
    [Fact]
    public void Stop_LeavesAnExplorerWhoseSessionCannotBeRead_AndGoesOn()
    {
        var unreadable = new FakeShell(100, session: 2)
        {
            SessionFails = new System.ComponentModel.Win32Exception(5, "Access is denied."),
        };
        var exited = new FakeShell(200, session: 2) { SessionFails = new InvalidOperationException("exited") };
        var own = new FakeShell(300, session: 2);

        ExplorerShell.Stop([unreadable, exited, own], ownSession: 2);

        Assert.False(unreadable.Killed);
        Assert.False(exited.Killed);
        Assert.True(own.Killed);
        Assert.All(new[] { unreadable, exited, own }, shell => Assert.True(shell.Disposed));
    }

    /// <summary>One Explorer that will not end does not keep the next one in the session running.</summary>
    [Fact]
    public void Stop_GoesOnPastAnExplorerThatWillNotEnd()
    {
        var refuses = new FakeShell(100, session: 2)
        {
            KillFails = new System.ComponentModel.Win32Exception(5, "Access is denied."),
        };
        var gone = new FakeShell(200, session: 2) { KillFails = new InvalidOperationException("exited") };
        var own = new FakeShell(300, session: 2);

        ExplorerShell.Stop([refuses, gone, own], ownSession: 2);

        Assert.True(own.Killed);
        Assert.All(new[] { refuses, gone, own }, shell => Assert.True(shell.Disposed));
    }
}
