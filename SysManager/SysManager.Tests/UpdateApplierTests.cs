// SysManager · UpdateApplierTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the in-process self-update applier that replaced the external
/// updater .cmd script (updater TOCTOU hardening). The previous design wrote a
/// batch file to a writable folder and ran it via cmd.exe, leaving a window for
/// a same-user process to swap the script before it executed. The applier runs
/// from inside the downloaded executable instead — there is no on-disk script to
/// tamper with. These tests pin the argument round-trip, quote rejection, and
/// the atomic copy-into-place behaviour.
/// </summary>
public class UpdateApplierTests
{
    /// <summary>
    /// The updates directory every <see cref="UpdateApplier.ApplyCopy"/> call in this file must be
    /// pointed at. Omitting it is not a harmless default: ApplyCopy retains the outgoing build, and
    /// with no override that copy lands in the REAL <c>%LocalAppData%\SysManager\updates</c> and
    /// overwrites the developer's own rollback build with a temp fixture (#1772).
    /// </summary>
    private static string Updates(DirectoryInfo dir) => Path.Combine(dir.FullName, "updates");

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    [Fact]
    public void BuildArguments_RoundTripsThroughTryParse()
    {
        const string target = @"C:\Program Files\SysManager\SysManager.exe";
        var argString = UpdateApplier.BuildArguments(target, 4321);

        // The OS splits the command line and strips the quotes around the path,
        // so simulate that by parsing the unquoted tokens the way Main receives them.
        var args = new[] { UpdateApplier.ApplyUpdateArg, target, "4321" };
        Assert.True(UpdateApplier.TryParseArgs(args, out var parsedTarget, out var pid));
        Assert.Equal(target, parsedTarget);
        Assert.Equal(4321, pid);

        // The built string carries the sentinel and a quoted path.
        Assert.StartsWith(UpdateApplier.ApplyUpdateArg, argString);
        Assert.Contains($"\"{target}\"", argString);
    }

    [Fact]
    public void BuildArguments_RejectsQuoteInPath()
    {
        Assert.Throws<InvalidOperationException>(
            () => UpdateApplier.BuildArguments(@"C:\evil"" & calc & "".exe", 1));
    }

    public static IEnumerable<object[]> MalformedArgs() => new[]
    {
        new object[] { Array.Empty<string>() },                          // empty
        new object[] { new[] { "--apply-update" } },                     // missing target + pid
        new object[] { new[] { "--apply-update", @"C:\x.exe" } },        // missing pid
        new object[] { new[] { "--other", @"C:\x.exe", "123" } },        // wrong sentinel
        new object[] { new[] { "--apply-update", @"C:\x.exe", "0" } },   // non-positive pid
        new object[] { new[] { "--apply-update", @"C:\x.exe", "abc" } }, // non-numeric pid
        new object[] { new[] { "--apply-update", "", "123" } },          // blank target
    };

    [Theory]
    [MemberData(nameof(MalformedArgs))]
    public void TryParseArgs_RejectsMalformedInput(string[] args)
    {
        Assert.False(UpdateApplier.TryParseArgs(args, out _, out _));
    }

    [Fact]
    public void TryParseArgs_IsCaseInsensitiveOnSentinel()
    {
        var args = new[] { "--APPLY-UPDATE", @"C:\x.exe", "7" };
        Assert.True(UpdateApplier.TryParseArgs(args, out var target, out var pid));
        Assert.Equal(@"C:\x.exe", target);
        Assert.Equal(7, pid);
    }

    [Fact]
    public void ApplyCopy_ReplacesTargetWithSourceContents()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            var ok = UpdateApplier.ApplyCopy(
                source, target, maxAttempts: 1, delayMs: 0, updatesDir: Updates(dir));

            Assert.True(ok);
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
            // The staging sibling is moved (not left behind) on success.
            Assert.False(File.Exists(target + ".new"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyCopy_CreatesTargetWhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");

            var ok = UpdateApplier.ApplyCopy(
                source, target, maxAttempts: 1, delayMs: 0, updatesDir: Updates(dir));

            Assert.True(ok);
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApplyCopy_LeavesTargetIntactWhenSourceMissing()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "does-not-exist.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(target, "OLD-BUILD");

            // A missing source is non-recoverable: the early-exit guard returns false
            // immediately, so even a large maxAttempts must NOT spin the retry/backoff
            // loop (it would otherwise misread FileNotFoundException as a transient lock).
            // delayMs is large on purpose — if the guard regressed and the loop ran, the
            // test would hang noticeably rather than return instantly.
            var ok = UpdateApplier.ApplyCopy(
                source, target, maxAttempts: 10, delayMs: 10_000, updatesDir: Updates(dir));

            // A failed copy must never destroy the working executable.
            Assert.False(ok);
            Assert.Equal("OLD-BUILD", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".new"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ── Rollback: retaining the outgoing build ───────────────────────────────────────────────
    //
    // The atomic move that makes an INTERRUPTED update safe also destroyed the previous executable, so
    // a SUCCESSFUL update into a broken build left nothing to go back to. The docs were precise about
    // the interruption case and silent about this one. This project has shipped two launch-blocking
    // regressions, so it is not hypothetical — and the updater was the only mutating feature in the app
    // without the snapshot-style reversibility everything else already has.

    [Fact]
    public void ApplyCopy_RetainsTheOutgoingBuild()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierKeep_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Path.Combine(dir.FullName, "updates");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            UpdateApplier.PreserveCurrentBuild(target, updates);
            Assert.True(UpdateApplier.ApplyCopy(source, target, updatesDir: updates));

            // The update landed…
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
            // …and the build it replaced is still recoverable.
            var previous = UpdateApplier.PreviousBuildPath(updates);
            Assert.True(File.Exists(previous));
            Assert.Equal("OLD-BUILD", File.ReadAllText(previous));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void PreserveCurrentBuild_KeepsExactlyOneGeneration()
    {
        // Retention is "current + one previous", not a history: each copy overwrites the last, so the
        // folder can never accumulate full-size executables.
        var dir = Directory.CreateTempSubdirectory("ApplierOneGen_");
        try
        {
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Path.Combine(dir.FullName, "updates");

            File.WriteAllText(target, "BUILD-1");
            UpdateApplier.PreserveCurrentBuild(target, updates);
            File.WriteAllText(target, "BUILD-2");
            UpdateApplier.PreserveCurrentBuild(target, updates);

            Assert.Single(Directory.GetFiles(updates, "SysManager-previous.exe"));
            Assert.Equal("BUILD-2", File.ReadAllText(UpdateApplier.PreviousBuildPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void PreserveCurrentBuild_FirstInstall_DoesNothing()
    {
        // Nothing at the target yet (a fresh install), so there is nothing to retain — and certainly no
        // reason to throw.
        var dir = Directory.CreateTempSubdirectory("ApplierFirst_");
        try
        {
            var target = Path.Combine(dir.FullName, "not-there-yet.exe");
            var updates = Path.Combine(dir.FullName, "updates");

            var ex = Record.Exception(() => UpdateApplier.PreserveCurrentBuild(target, updates));

            Assert.Null(ex);
            Assert.False(File.Exists(UpdateApplier.PreviousBuildPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void PreserveCurrentBuild_IsBestEffort_AndNeverBlocksAnUpdate()
    {
        // Retaining a copy is a safety net, not the job. If the folder cannot be written — full disk,
        // permissions, a file where the directory should be — the update must still proceed rather than
        // fail because the net could not be stretched. Simulated by putting a FILE where the updates
        // directory needs to be, so CreateDirectory fails.
        var dir = Directory.CreateTempSubdirectory("ApplierBestEffort_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var blocked = Path.Combine(dir.FullName, "updates");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");
            File.WriteAllText(blocked, "not a directory");

            var ex = Record.Exception(() => UpdateApplier.PreserveCurrentBuild(target, blocked));
            Assert.Null(ex);                                      // swallowed, logged, not thrown

            // Same blocked directory: the update must survive the retention failing INSIDE ApplyCopy,
            // not merely when PreserveCurrentBuild is called separately beforehand.
            Assert.True(UpdateApplier.ApplyCopy(source, target, updatesDir: blocked));
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void PreviousBuildPath_LivesInTheUpdatesFolder_NotBesideThePortableExe()
    {
        // Keeping it beside the .exe would break the "single portable file" identity: copy the app to a
        // USB stick and you would get two executables, one of them an old version.
        var path = UpdateApplier.PreviousBuildPath();

        Assert.Equal("SysManager-previous.exe", Path.GetFileName(path));
        Assert.Equal("updates", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Contains("SysManager", path);
    }

    [Fact]
    public void ApplyCopy_RetentionHonoursTheRedirectedUpdatesDirectory()
    {
        // The bug this pins: ApplyCopy accepted no updatesDir, so the retention step inside it always
        // resolved the REAL profile no matter where the caller pointed source/target. A test could
        // redirect every path it knew about and still write an executable into the user's own
        // %LocalAppData%\SysManager\updates. Accepting the parameter is not enough — it has to reach
        // PreserveCurrentBuild, which is what this asserts.
        var dir = Directory.CreateTempSubdirectory("ApplierRedirect_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            // No separate PreserveCurrentBuild call — ApplyCopy's own retention must do the work.
            Assert.True(UpdateApplier.ApplyCopy(source, target, maxAttempts: 1, delayMs: 0, updatesDir: updates));

            Assert.Equal("OLD-BUILD", File.ReadAllText(UpdateApplier.PreviousBuildPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_DoesNotTouchTheRealProfile()
    {
        // The end-to-end guarantee, stated against the actual user path rather than a proxy: running
        // the applier in a test must leave the developer's own retained build exactly as it was. This
        // is the assertion that was failing silently — the file was being overwritten with a 9-byte
        // fixture while every other test in the file passed.
        var real = UpdateApplier.PreviousBuildPath();
        var existedBefore = File.Exists(real);
        var hashBefore = existedBefore ? Sha256(real) : null;

        var dir = Directory.CreateTempSubdirectory("ApplierNoLeak_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            UpdateApplier.ApplyCopy(source, target, maxAttempts: 1, delayMs: 0, updatesDir: Updates(dir));
        }
        finally { dir.Delete(recursive: true); }

        // Either it never existed and still does not, or it existed and is byte-identical. A length
        // check would pass on a same-size replacement, so compare the content hash.
        Assert.Equal(existedBefore, File.Exists(real));
        if (existedBefore)
            Assert.Equal(hashBefore, Sha256(real));
    }

    [Fact]
    public void PruneOldDownloads_DoesNotDeleteTheRetainedBuild()
    {
        // SysManager-previous.exe matches the SysManager-*.exe pruning pattern, so without an explicit
        // exclusion the very next update download would delete the one copy that makes rollback
        // possible — silently, because pruning is best-effort and logs at Debug.
        var dir = Directory.CreateTempSubdirectory("ApplierPrune_");
        try
        {
            var keep = Path.Combine(dir.FullName, "SysManager-9.9.9.exe");
            var previous = Path.Combine(dir.FullName, UpdateApplier.PreviousBuildFileName);
            var stale = Path.Combine(dir.FullName, "SysManager-1.0.0.exe");
            File.WriteAllText(keep, "current");
            File.WriteAllText(previous, "rollback");
            File.WriteAllText(stale, "superseded");

            UpdateService.PruneOldDownloads(dir.FullName, keep);

            Assert.True(File.Exists(previous), "the retained previous build must survive pruning");
            Assert.True(File.Exists(keep));
            Assert.False(File.Exists(stale));   // control: pruning still works
        }
        finally { dir.Delete(recursive: true); }
    }
}
