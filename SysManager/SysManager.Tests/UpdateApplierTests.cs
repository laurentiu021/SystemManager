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
    /// The updates directory every <see cref="UpdateApplier.ApplyCopyFromPath(string, string, int, int, string?, bool)"/> call in this file must be
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

    /// <summary>Keeps a previous build in <paramref name="updates"/> the way an update does: the binary and its checksum.</summary>
    private static void KeepABuild(string updates, string content)
    {
        Directory.CreateDirectory(updates);
        var previous = UpdateApplier.PreviousBuildPath(updates);
        File.WriteAllText(previous, content);
        File.WriteAllText(UpdateApplier.PreviousBuildHashPath(updates), Sha256(previous));
    }

    /// <summary>A SHA-256-shaped value, which is the only shape the hash argument may take.</summary>
    private const string AnyHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void BuildArguments_RoundTripsThroughTryParse()
    {
        const string target = @"C:\Program Files\SysManager\SysManager.exe";
        var argString = UpdateApplier.BuildArguments(target, 4321, AnyHash);

        // The OS splits the command line and strips the quotes around the path,
        // so simulate that by parsing the unquoted tokens the way Main receives them.
        var args = new[] { UpdateApplier.ApplyUpdateArg, target, "4321", AnyHash };
        Assert.True(UpdateApplier.TryParseArgs(args, out var parsedTarget, out var pid, out var hash));
        Assert.Equal(target, parsedTarget);
        Assert.Equal(4321, pid);
        // The hash travels with the launch, because the applier re-checks the bytes it installs against it (#2832).
        Assert.Equal(AnyHash, hash);

        // The built string carries the sentinel, a quoted path and the hash.
        Assert.StartsWith(UpdateApplier.ApplyUpdateArg, argString);
        Assert.Contains($"\"{target}\"", argString);
        Assert.EndsWith(AnyHash, argString);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEFA")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEG")]
    public void BuildArguments_RejectsAHashThatIsNotOne(string hash)
    {
        // Refused on the way out as well as on the way in: a launch with no usable checksum is an install the
        // applier cannot check, which is the whole of #2832. Sixty-three digits, sixty-five, and one that is
        // not hexadecimal.
        Assert.Throws<InvalidOperationException>(
            () => UpdateApplier.BuildArguments(@"C:\x.exe", 1, hash));
    }

    [Fact]
    public void BuildArguments_RejectsQuoteInPath()
    {
        Assert.Throws<InvalidOperationException>(
            () => UpdateApplier.BuildArguments(@"C:\evil"" & calc & "".exe", 1, AnyHash));
    }

    public static IEnumerable<object[]> MalformedArgs() => new[]
    {
        new object[] { Array.Empty<string>() },                          // empty
        new object[] { new[] { "--apply-update" } },                      // missing target + pid
        new object[] { new[] { "--apply-update", @"C:\x.exe" } },         // missing pid
        new object[] { new[] { "--apply-update", @"C:\x.exe", "123" } },  // missing hash
        new object[] { new[] { "--other", @"C:\x.exe", "123", AnyHash } },         // wrong sentinel
        new object[] { new[] { "--apply-update", @"C:\x.exe", "0", AnyHash } },    // non-positive pid
        new object[] { new[] { "--apply-update", @"C:\x.exe", "abc", AnyHash } },  // non-numeric pid
        new object[] { new[] { "--apply-update", "", "123", AnyHash } },            // blank target
        new object[] { new[] { "--apply-update", @"C:\x.exe", "123", "nope" } },  // hash is not a SHA-256
    };

    [Theory]
    [MemberData(nameof(MalformedArgs))]
    public void TryParseArgs_RejectsMalformedInput(string[] args)
    {
        Assert.False(UpdateApplier.TryParseArgs(args, out _, out _, out _));
    }

    [Fact]
    public void TryParseArgs_IsCaseInsensitiveOnSentinel()
    {
        var args = new[] { "--APPLY-UPDATE", @"C:\x.exe", "7", AnyHash };
        Assert.True(UpdateApplier.TryParseArgs(args, out var target, out var pid, out _));
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

            var ok = UpdateApplier.ApplyCopyFromPath(
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

    // ---------- a failed apply has to name the cause it actually hit (#2377) ----------

    [Theory]
    [InlineData(unchecked((int)0x8007_0070))]   // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x8007_0027))]   // ERROR_HANDLE_DISK_FULL
    public void IsOutOfSpace_RecognisesAFullVolume(int hresult)
    {
        // The update needs room for the new build, a staging copy and the retained rollback build — about
        // 250 MB — so a nearly-full drive is a realistic failure, and it arrives as an IOException exactly
        // like a sharing violation does. Both codes, because Windows uses either depending on the call.
        Assert.True(UpdateApplier.IsOutOfSpace(new IOException("no room", hresult)));
    }

    [Fact]
    public void IsOutOfSpace_DoesNotMistakeALockForAFullVolume()
    {
        // The negative half, and the one that matters: a sharing violation IS worth waiting out, and
        // treating it as a full disk would abandon an update that five seconds of patience would complete.
        Assert.False(UpdateApplier.IsOutOfSpace(
            new IOException("in use", unchecked((int)0x8007_0020))));   // ERROR_SHARING_VIOLATION
    }

    [Fact]
    public void ApplyCopy_TargetLockedForTheWholeRun_FailsAndLeavesNoStagingFileBehind()
    {
        // An open handle is the deterministic way to block the move: File.Move needs exclusive access, so
        // any other handle makes it fail. No privileges, no timing.
        //
        // The staging sibling is the thing to assert on. It is now created ONCE, before the retry loop, so
        // the cleanup has to happen after the loop gives up — an earlier draft deleted it inside the loop,
        // which is what made the copy repeat in the first place.
        var dir = Directory.CreateTempSubdirectory("ApplierTest_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            using (var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ok = UpdateApplier.ApplyCopyFromPath(
                    source, target, maxAttempts: 2, delayMs: 0, updatesDir: Updates(dir));

                Assert.False(ok);
            }

            Assert.False(File.Exists(target + ".new"), "the staging copy was left beside the target");

            // And the original build is untouched, which is the whole reason the move is the only step
            // that writes the live path.
            Assert.Equal("OLD-BUILD", File.ReadAllText(target));
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

            var ok = UpdateApplier.ApplyCopyFromPath(
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
            var ok = UpdateApplier.ApplyCopyFromPath(
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

            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates));

            // The update landed…
            Assert.Equal("NEW-BUILD", File.ReadAllText(target));
            // …and the build it replaced is still recoverable, with the checksum Go back proves it against.
            var previous = UpdateApplier.PreviousBuildPath(updates);
            Assert.Equal("OLD-BUILD", File.ReadAllText(previous));
            Assert.Equal(Sha256(previous), File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates)));
            Assert.Empty(Directory.GetFiles(updates, "*.pending*"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_KeepsExactlyOneGeneration()
    {
        // Retention is "current + one previous", not a history: each copy overwrites the last, so the
        // folder can never accumulate full-size executables.
        var dir = Directory.CreateTempSubdirectory("ApplierOneGen_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Path.Combine(dir.FullName, "updates");

            File.WriteAllText(target, "BUILD-1");
            File.WriteAllText(source, "BUILD-2");
            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates));
            File.WriteAllText(source, "BUILD-3");
            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates));

            Assert.Single(Directory.GetFiles(updates, "SysManager-previous.exe"));
            Assert.Equal("BUILD-2", File.ReadAllText(UpdateApplier.PreviousBuildPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_FirstInstall_KeepsNothing()
    {
        // Nothing at the target yet (a fresh install), so there is nothing to retain — and certainly no
        // reason to throw.
        var dir = Directory.CreateTempSubdirectory("ApplierFirst_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "not-there-yet.exe");
            var updates = Path.Combine(dir.FullName, "updates");
            File.WriteAllText(source, "NEW-BUILD");

            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates));

            Assert.False(File.Exists(UpdateApplier.PreviousBuildPath(updates)));
            Assert.False(File.Exists(UpdateApplier.PendingPreviousBuildPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── A failed update and a rollback leave the kept copy right (#2725) ─────────────────────
    //
    // The outgoing build was written over the kept copy before the move, so an update that then failed replaced the
    // real previous version with the one still running. A rollback, which runs from the kept copy, tried to keep the
    // newer build in its place and left Go back offered to reinstall the version already running. And an update that
    // could not keep its outgoing build left an earlier update's copy offered as the version before the last update.

    [Fact]
    public void ApplyCopy_ThatFails_LeavesTheKeptCopyAsItWas()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierFailKeep_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Path.Combine(dir.FullName, "updates");
            File.WriteAllText(target, "BUILD-1");
            File.WriteAllText(source, "BUILD-2");
            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates));   // keeps BUILD-1
            var previous = UpdateApplier.PreviousBuildPath(updates);
            var hash = File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates));

            File.WriteAllText(source, "BUILD-3");
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(UpdateApplier.ApplyCopyFromPath(source, target, maxAttempts: 2, delayMs: 0, updatesDir: updates));

            Assert.Equal("BUILD-2", File.ReadAllText(target));
            Assert.Equal("BUILD-1", File.ReadAllText(previous));
            Assert.Equal(hash, File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates)));
            Assert.Empty(Directory.GetFiles(updates, "*.pending*"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_ThatCannotReplaceTheKeptCopy_StillUpdates_AndOffersNoGoBack()
    {
        // The kept copy is held, so the staged one cannot take its place. The update lands anyway, and Go back is no
        // longer offered: what is kept is an earlier update's copy, not the version this update replaced.
        var dir = Directory.CreateTempSubdirectory("ApplierPromote_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            KeepABuild(updates, "BUILD-1");
            File.WriteAllText(target, "BUILD-2");
            File.WriteAllText(source, "BUILD-3");

            using (new FileStream(UpdateApplier.PreviousBuildPath(updates), FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, maxAttempts: 2, delayMs: 0, updatesDir: updates));

            Assert.Equal("BUILD-3", File.ReadAllText(target));
            Assert.False(File.Exists(UpdateApplier.PreviousBuildHashPath(updates)));
            Assert.Empty(Directory.GetFiles(updates, "*.pending*"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_ThatCouldNotKeepTheOutgoingBuild_OffersNoGoBack()
    {
        // Keeping the outgoing build failed, so the copy an earlier update kept is all there is, and it is not the
        // version this update replaced.
        var dir = Directory.CreateTempSubdirectory("ApplierNotKept_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            KeepABuild(updates, "BUILD-1");
            File.WriteAllText(target, "BUILD-2");
            File.WriteAllText(source, "BUILD-3");
            var pending = UpdateApplier.PendingPreviousBuildPath(updates);
            File.WriteAllText(pending, "held");

            using (new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, maxAttempts: 2, delayMs: 0, updatesDir: updates));

            Assert.Equal("BUILD-3", File.ReadAllText(target));
            Assert.False(File.Exists(UpdateApplier.PreviousBuildHashPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_ForARollback_KeepsNothingInPlaceOfTheCopy_AndTakesGoBackOffOffer()
    {
        // The applier runs from the kept copy, so the build it replaces is the newer one, and the copy Go back would
        // install is the version now running. From a copy of it here: the kept copy itself is held while a rollback
        // reads it, which would refuse the newer build its place whether or not it was kept.
        var dir = Directory.CreateTempSubdirectory("ApplierRollback_");
        try
        {
            var source = Path.Combine(dir.FullName, "previous.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            KeepABuild(updates, "OLDER-BUILD");
            File.WriteAllText(source, "OLDER-BUILD");
            File.WriteAllText(target, "NEWER-BUILD");

            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: updates, retainOutgoing: false));

            Assert.Equal("OLDER-BUILD", File.ReadAllText(target));
            Assert.Equal("OLDER-BUILD", File.ReadAllText(UpdateApplier.PreviousBuildPath(updates)));
            Assert.False(File.Exists(UpdateApplier.PreviousBuildHashPath(updates)));
            Assert.Empty(Directory.GetFiles(updates, "*.pending*"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyCopy_ForARollbackThatFails_LeavesGoBackOffered()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierRollbackFail_");
        try
        {
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            KeepABuild(updates, "OLDER-BUILD");
            var hash = File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates));
            var source = Path.Combine(dir.FullName, "previous.exe");
            File.WriteAllText(source, "OLDER-BUILD");
            File.WriteAllText(target, "NEWER-BUILD");

            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False(UpdateApplier.ApplyCopyFromPath(source, target,
                    maxAttempts: 2, delayMs: 0, updatesDir: updates, retainOutgoing: false));

            Assert.Equal("NEWER-BUILD", File.ReadAllText(target));
            Assert.Equal(hash, File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void IsTheKeptCopy_KnowsTheKeptBuild_AndNothingElse()
    {
        var updates = Path.Combine(Path.GetTempPath(), "ApplierKept_" + Guid.NewGuid().ToString("N"));

        Assert.True(UpdateApplier.IsTheKeptCopy(UpdateApplier.PreviousBuildPath(updates), updates));
        Assert.True(UpdateApplier.IsTheKeptCopy(UpdateApplier.PreviousBuildPath(updates).ToUpperInvariant(), updates));
        Assert.False(UpdateApplier.IsTheKeptCopy(Path.Combine(updates, "SysManager-v9.9.9.exe"), updates));
    }

    [Fact]
    public void RetireKeptCopy_TakesGoBackOffOffer()
    {
        // The kept binary is the running applier's own image after a rollback, so its checksum is what goes: About
        // offers Go back only when both are there.
        var dir = Directory.CreateTempSubdirectory("ApplierRetire_");
        try
        {
            var updates = Path.Combine(dir.FullName, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllText(UpdateApplier.PreviousBuildPath(updates), "OLDER-BUILD");
            File.WriteAllText(UpdateApplier.PreviousBuildHashPath(updates), AnyHash);

            UpdateApplier.RetireKeptCopy(updates);

            Assert.False(File.Exists(UpdateApplier.PreviousBuildHashPath(updates)));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void KeepingTheOutgoingBuild_IsBestEffort_AndNeverBlocksAnUpdate()
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

            // The retention fails inside ApplyCopy, swallowed and logged, and the update still lands.
            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, updatesDir: blocked));
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
        // the retention step, which is what this asserts.
        var dir = Directory.CreateTempSubdirectory("ApplierRedirect_");
        try
        {
            var source = Path.Combine(dir.FullName, "new.exe");
            var target = Path.Combine(dir.FullName, "current.exe");
            var updates = Updates(dir);
            File.WriteAllText(source, "NEW-BUILD");
            File.WriteAllText(target, "OLD-BUILD");

            // ApplyCopy's own retention must do the work.
            Assert.True(UpdateApplier.ApplyCopyFromPath(source, target, maxAttempts: 1, delayMs: 0, updatesDir: updates));

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

            UpdateApplier.ApplyCopyFromPath(source, target, maxAttempts: 1, delayMs: 0, updatesDir: Updates(dir));
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

    [Fact]
    public void PruneOldDownloads_RemovesAKeptCopyLeftPendingByAnInterruptedUpdate()
    {
        // An update that stopped between keeping the outgoing build and the move leaves it under the pending name. It is
        // never offered, so it goes with the next download like any other superseded file, and the kept copy stays.
        var dir = Directory.CreateTempSubdirectory("ApplierPrunePending_");
        try
        {
            var keep = Path.Combine(dir.FullName, "SysManager-9.9.9.exe");
            File.WriteAllText(keep, "current");
            KeepABuild(dir.FullName, "rollback");
            var pending = UpdateApplier.PendingPreviousBuildPath(dir.FullName);
            File.WriteAllText(pending, "interrupted");
            File.WriteAllText(pending + ".sha256", AnyHash);

            UpdateService.PruneOldDownloads(dir.FullName, keep);

            Assert.False(File.Exists(pending));
            Assert.False(File.Exists(pending + ".sha256"));
            Assert.True(File.Exists(UpdateApplier.PreviousBuildPath(dir.FullName)));
            Assert.True(File.Exists(UpdateApplier.PreviousBuildHashPath(dir.FullName)));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── The --apply-update target is attacker-supplied ──────────────────────────────────────────────
    // App.OnStartup takes the applier branch before the mutex, DI and the CLI guard, so targetExe comes
    // straight from argv. TryParseArgs only ever checked SHAPE (sentinel, non-blank, numeric pid), which
    // made `SysManager.exe --apply-update "<any writable path>" <pid>` copy this executable over that
    // path and launch it — with whatever token started the process, and the app's documented workflow is
    // "Run as administrator". These tests assert the hostile input is REFUSED, per the rule that a
    // validation which is the sole defense must be explicitly tested.

    /// <summary>
    /// A valid target is a COPY of the running test host: identity is the Win32 product resource, and
    /// the only way to get a file that genuinely carries this process's product is to copy this
    /// process's own binary. A plain text file has no resource at all, which is why the earlier
    /// write-some-bytes fixture no longer models an acceptable target.
    /// </summary>
    private static string CopyOfThisBuild(DirectoryInfo dir, string name = "SysManager-vX.Y.Z.exe")
    {
        var target = Path.Combine(dir.FullName, name);
        File.Copy(Environment.ProcessPath!, target);
        return target;
    }

    [Fact]
    public void ApplyTarget_AcceptsAnExistingBuildCarryingThisProduct_UnderAnyName()
    {
        // The legitimate case, and it deliberately uses a DIFFERENT file name from the running process:
        // in production the applier is SysManager-<new>.exe and the target is the older build, so a rule
        // keyed on name equality could never accept a real update. Identity is the product resource.
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            var target = CopyOfThisBuild(dir);
            Assert.NotEqual(Path.GetFileName(target), Path.GetFileName(Environment.ProcessPath!));

            Assert.True(UpdateApplier.IsValidApplyTarget(target, out var reason), reason);
            Assert.Equal("", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("hosts")]                 // the file an attacker would aim at first
    [InlineData("SysManager.exe")]        // even OUR canonical name cannot smuggle a foreign file in
    [InlineData("SysManager-v9.9.9.exe")] // shaped exactly like a downloaded asset
    public void ApplyTarget_RefusesAFileThatDoesNotCarryOurProduct(string fileName)
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            // Existing and writable, but no version resource, so it is not one of our builds — and the
            // name, even our own, cannot make it one.
            var target = Path.Combine(dir.FullName, fileName);
            File.WriteAllText(target, "someone else's file");

            Assert.False(UpdateApplier.IsValidApplyTarget(target, out var reason));
            Assert.Contains("build", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyTarget_RefusesAPathThatDoesNotExistYet()
    {
        // An update REPLACES an install; it never creates one. Without this the applier could be used to
        // plant a new 85 MB executable at a path of the attacker's choosing — a startup folder, a
        // scheduled-task target — even with the name check in place.
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            var target = Path.Combine(dir.FullName, "SysManager-vX.Y.Z.exe");   // never created

            Assert.False(UpdateApplier.IsValidApplyTarget(target, out var reason));
            Assert.Contains("does not exist", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyTarget_RefusesAPathInsideTheWindowsDirectory()
    {
        // Belt-and-braces for a file legitimately named like ours inside a system root: an elevated
        // SysManager must not be steerable into writing there, whatever the file is called.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var target = Path.Combine(windows, "System32", "SysManager-vX.Y.Z.exe");

        Assert.False(UpdateApplier.IsValidApplyTarget(target, out var reason));
        // Specifically for BEING in a system root — the location check runs before the existence check,
        // so this cannot pass merely because no file happens to sit at that path.
        Assert.Contains("system directory", reason);
    }

    // ── asked before SysManager closes (#2725) ───────────────────────────────────────────
    // The applier decides after SysManager has closed, so its refusal could only be logged: under Program Files,
    // Install and Go back closed SysManager for good. About asks the same question first.

    [Fact]
    public void WhyItCannotReplace_AProgramFilesCopy_SaysWhyInPlainWords()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SysManager", "SysManager.exe");

        Assert.Equal("it runs from a folder Windows protects, such as Program Files", UpdateApplier.WhyItCannotReplace(target));
    }

    [Fact]
    public void WhyItCannotReplace_AFolderThatTakesNoNewFile_SaysSo()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            var target = CopyOfThisBuild(dir);

            Assert.Equal("it cannot write to the folder it runs from",
                UpdateApplier.WhyItCannotReplace(target, canCreateFileIn: _ => false));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void WhyItCannotReplace_AWritableCopyOfThisBuild_IsNothing_AndLeavesNoFileBehind()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            var target = CopyOfThisBuild(dir);

            Assert.Null(UpdateApplier.WhyItCannotReplace(target));
            Assert.Equal([target], Directory.GetFiles(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyTarget_RefusesARealBinaryCarryingADifferentProduct()
    {
        // A genuine, signed Windows binary carries a real product resource ("Microsoft ... Operating
        // System"), which is NOT ours. Copied out of System32 into a temp dir so the system-root check
        // cannot be what refuses it: the ONLY thing left to reject it is the product mismatch, which is
        // the branch a plain text file (no resource at all) never exercises.
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.True(File.Exists(source), "probe file missing — this test needs a real System32 binary");

        var dir = Directory.CreateTempSubdirectory("ApplierForeign_");
        try
        {
            var target = Path.Combine(dir.FullName, "SysManager-vX.Y.Z.exe");
            File.Copy(source, target);

            Assert.False(UpdateApplier.IsValidApplyTarget(target, out var reason));
            Assert.Contains("build", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ApplyTarget_ResolvesDotSegmentsBeforeDeciding()
    {
        // Validate and act on the SAME string. A target of "<temp>\sub\..\hosts" has the file name
        // "hosts" only after canonicalisation; comparing the raw string would see "..".
        var dir = Directory.CreateTempSubdirectory("ApplierTarget_");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            var real = Path.Combine(dir.FullName, "hosts");
            File.WriteAllText(real, "someone else's file");

            var traversal = Path.Combine(sub.FullName, "..", "hosts");

            Assert.False(UpdateApplier.IsValidApplyTarget(traversal, out var reason));
            // Refused because "hosts" is not one of our builds; the point here is that canonicalisation
            // happened at all, so ".." was resolved before the decision rather than compared literally.
            Assert.Contains("build", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Run_WithAHostileTarget_LeavesThatFileUntouched()
    {
        // The end-to-end proof: Run is what writes, so Run is what must refuse. Drives the real entry
        // point the startup branch calls, and asserts the would-be victim file is byte-identical after.
        var dir = Directory.CreateTempSubdirectory("ApplierRun_");
        try
        {
            var victim = Path.Combine(dir.FullName, "hosts");
            File.WriteAllText(victim, "127.0.0.1 localhost");
            var before = Sha256(victim);

            // A pid that cannot exist, so Run does not wait: this is exactly what an attacker supplies.
            UpdateApplier.Run(victim, 999999, AnyHash);

            Assert.True(File.Exists(victim));
            Assert.Equal(before, Sha256(victim));
            Assert.False(File.Exists(victim + ".new"));   // not even a staging file was written
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── The bytes installed are the bytes whose hash was checked (#2832) ───────────────────────────
    // The launching app verified a hash on a handle it then closed, and the applier read the file by
    // NAME out of %LOCALAPPDATA%\SysManager\updates — a folder the user can write to — installing and
    // starting whatever that name meant at the second read, elevated. Run now opens its own deny-write
    // handle, hashes THAT, and installs from it.

    /// <summary>
    /// A build that does not match the hash it was launched with installs nothing.
    /// </summary>
    /// <remarks>
    /// Run hashes the process's own executable, which in a test is the test host — so any real SHA-256 that is
    /// not the test host's proves the refusal. The target is a copy of this executable, so the only thing left
    /// to refuse on is the hash: <c>IsValidApplyTarget</c> is satisfied and would otherwise let the write
    /// through. Asserted on the target's bytes AND on the absence of a staging file, so a refusal that happened
    /// after a partial write would still fail.
    /// </remarks>
    [Fact]
    public void Run_WhenThisBuildDoesNotMatchItsHash_InstallsNothing()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierHash_");
        try
        {
            // A target that passes the product check: a copy of the running executable.
            var self = Environment.ProcessPath!;
            var target = Path.Combine(dir.FullName, Path.GetFileName(self));
            File.Copy(self, target);
            var before = Sha256(target);

            // A pid that cannot exist, so nothing waits. AnyHash is a well-formed SHA-256 that is not this
            // executable's, which is exactly the swapped-file case.
            UpdateApplier.Run(target, 999999, AnyHash);

            Assert.Equal(before, Sha256(target));
            Assert.False(File.Exists(target + ".new"), "a staging file was written before the hash was checked");
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// A launch with no usable checksum installs nothing either.
    /// </summary>
    /// <remarks>
    /// <c>TryParseArgs</c> already refuses these, so this is the second gate: Run is the only thing that
    /// writes, so Run is what must refuse, and a future second caller cannot get round it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    public void Run_WithNoUsableHash_InstallsNothing(string hash)
    {
        var dir = Directory.CreateTempSubdirectory("ApplierHash_");
        try
        {
            var self = Environment.ProcessPath!;
            var target = Path.Combine(dir.FullName, Path.GetFileName(self));
            File.Copy(self, target);
            var before = Sha256(target);

            UpdateApplier.Run(target, 999999, hash);

            Assert.Equal(before, Sha256(target));
            Assert.False(File.Exists(target + ".new"));
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// The staged bytes come from the handed stream, not from any path.
    /// </summary>
    /// <remarks>
    /// The property the fix rests on: whoever holds a verified handle can install exactly those bytes. Driven
    /// with a stream whose contents exist nowhere on disk, so a copy that went by name could not possibly pass.
    /// </remarks>
    [Fact]
    public void ApplyCopy_FromAStream_InstallsThoseBytes()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierStream_");
        try
        {
            var target = Path.Combine(dir.FullName, "SysManager.exe");
            File.WriteAllText(target, "the old build");
            var bytes = System.Text.Encoding.UTF8.GetBytes("the verified build");

            using var source = new MemoryStream(bytes);
            // Read to the end first: the copy must rewind the stream itself, because the applier hands over one
            // it has just hashed, and hashing leaves the position at the end.
            source.Position = source.Length;

            Assert.True(UpdateApplier.ApplyCopy(source, target, updatesDir: Updates(dir)));
            Assert.Equal("the verified build", File.ReadAllText(target));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ── The retained rollback build lives in a user-writable folder ─────────────────────────────────
    // RollBackAsync launched %LOCALAPPDATA%\SysManager\updates\SysManager-previous.exe on File.Exists
    // alone, with UseShellExecute — so anything running as the user could replace it and have it start
    // with SysManager's token. The install path hardens this exact shape (hash from a held handle, then
    // launch without reopening); these tests pin that rollback now does the same.

    [Fact]
    public void KeepingTheOutgoingBuild_RecordsTheChecksumOfWhatItWrote()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierPreserve_");
        try
        {
            var updates = Updates(dir);
            var current = Path.Combine(dir.FullName, "SysManager.exe");
            var source = Path.Combine(dir.FullName, "new.exe");
            File.WriteAllText(current, "the outgoing build");
            File.WriteAllText(source, "the incoming build");

            Assert.True(UpdateApplier.ApplyCopyFromPath(source, current, updatesDir: updates));

            var previous = UpdateApplier.PreviousBuildPath(updates);
            var hashFile = UpdateApplier.PreviousBuildHashPath(updates);
            Assert.True(File.Exists(previous));
            Assert.True(File.Exists(hashFile), "a retained build without its checksum can never be verified");
            // The recorded hash must describe the RETAINED bytes, not the source's.
            Assert.Equal(Sha256(previous), File.ReadAllText(hashFile).Trim());
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void VerifiedPreviousBuild_AcceptsAnUntouchedRetainedBuild()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierVerify_");
        try
        {
            var updates = Updates(dir);
            KeepABuild(updates, "the outgoing build");

            Assert.True(
                UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out var reason, out _),
                reason);
            using (stream)
            {
                Assert.NotNull(stream);
                // The handle is returned so the caller launches the very bytes that were verified,
                // instead of reopening the path and reintroducing the swap window.
                Assert.Throws<IOException>(() => new FileStream(
                    UpdateApplier.PreviousBuildPath(updates),
                    FileMode.Open, FileAccess.Write, FileShare.None));
            }
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void VerifiedPreviousBuild_RefusesABuildSwappedAfterItWasSaved()
    {
        // THE attack: the retained copy is replaced between the update and the click on "Go back".
        var dir = Directory.CreateTempSubdirectory("ApplierVerify_");
        try
        {
            var updates = Updates(dir);
            KeepABuild(updates, "the outgoing build");

            File.WriteAllText(UpdateApplier.PreviousBuildPath(updates), "attacker payload");

            Assert.False(UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out var reason, out _));
            Assert.Null(stream);   // nothing left open on a refusal
            Assert.Contains("changed", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(null)]                                   // no checksum recorded at all
    [InlineData("")]                                     // present but empty
    [InlineData("not-a-hash")]                           // present but unusable
    [InlineData("DEADBEEF")]                             // right alphabet, wrong length
    public void VerifiedPreviousBuild_FailsClosedWithoutAUsableChecksum(string? hashContent)
    {
        // "Cannot prove this" is a refusal, not a skip — the same call UpdateService makes for a missing
        // published .sha256. A permissive branch here would undo the whole check.
        var dir = Directory.CreateTempSubdirectory("ApplierVerify_");
        try
        {
            var updates = Directory.CreateDirectory(Updates(dir)).FullName;
            File.WriteAllText(UpdateApplier.PreviousBuildPath(updates), "some build");
            if (hashContent is not null)
                File.WriteAllText(UpdateApplier.PreviousBuildHashPath(updates), hashContent);

            Assert.False(UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out var reason, out _));
            Assert.Null(stream);
            Assert.NotEqual("", reason);   // and it says why, so the UI never refuses silently
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void VerifiedPreviousBuild_RefusesWhenThereIsNoRetainedBuild()
    {
        var dir = Directory.CreateTempSubdirectory("ApplierVerify_");
        try
        {
            var updates = Directory.CreateDirectory(Updates(dir)).FullName;

            Assert.False(UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out var reason, out _));
            Assert.Null(stream);
            Assert.NotEqual("", reason);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(null)]           // no checksum file
    [InlineData("")]             // present but empty
    [InlineData("not-a-hash")]   // unusable
    [InlineData("00000000000000000000000000000000000000000000000000000000000000FF")]  // valid shape, wrong value
    public void VerifiedPreviousBuild_ReleasesTheHandleOnEveryRefusal(string? hashContent)
    {
        // The verification opens the retained build with FileShare.Read (deny-write) BEFORE hashing it.
        // Every refusal path must close that handle, and a leak here is worse than an ordinary handle
        // leak: while it is held, nothing can replace SysManager-previous.exe, so the next legitimate
        // update silently fails to refresh the rollback copy and leaves the user with a stale one.
        // Proven by DELETING the file afterwards — Windows refuses that while a handle is open, so a
        // successful delete IS the evidence the handle was released.
        var dir = Directory.CreateTempSubdirectory("ApplierHandle_");
        try
        {
            var updates = Directory.CreateDirectory(Updates(dir)).FullName;
            var previous = UpdateApplier.PreviousBuildPath(updates);
            File.WriteAllText(previous, "some build");
            if (hashContent is not null)
                File.WriteAllText(UpdateApplier.PreviousBuildHashPath(updates), hashContent);

            Assert.False(UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out _, out _));
            Assert.Null(stream);

            File.Delete(previous);   // throws IOException if the handle is still open
            Assert.False(File.Exists(previous));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void VerifiedPreviousBuild_OnSuccess_KeepsTheHandleOpenForTheCaller()
    {
        // The control for the test above: the success path must NOT release the handle, because holding it
        // across Process.Start is the whole point — it is what stops the verified bytes being swapped
        // between the check and the launch. A finally that disposed unconditionally would pass the
        // refusal tests and silently destroy this guarantee.
        var dir = Directory.CreateTempSubdirectory("ApplierHandle_");
        try
        {
            var updates = Updates(dir);
            KeepABuild(updates, "the outgoing build");

            Assert.True(UpdateApplier.TryOpenVerifiedPreviousBuild(updates, out var stream, out var why, out var hash), why);
            // The checksum it proved the copy against comes back with the handle: the rollback launch carries it,
            // so the applier can check the same bytes on a handle of its own (#2832).
            Assert.Equal(File.ReadAllText(UpdateApplier.PreviousBuildHashPath(updates)).Trim(), hash);
            using (stream)
            {
                Assert.NotNull(stream);
                Assert.Throws<IOException>(() => File.Delete(UpdateApplier.PreviousBuildPath(updates)));
            }
        }
        finally { dir.Delete(recursive: true); }
    }
}
