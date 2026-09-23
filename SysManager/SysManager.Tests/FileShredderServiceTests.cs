// SysManager · FileShredderServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Security;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Regression coverage for <see cref="FileShredderService"/> — the most
/// data-loss-sensitive guard in the app (audit finding tests #1, P0).
/// <para>
/// <see cref="FileShredderService.ShredFileAsync"/> /
/// <c>ShredFolderAsync</c> reject any path under a system-protected root
/// (Windows, System32, Program Files, Program Files (x86)) by throwing
/// <see cref="SecurityException"/>. Before these tests a refactor of the
/// prefix list or the <c>StartsWith</c> comparison could have silently
/// allowed shredding system files with no test failing.
/// </para>
/// The positive paths operate on real temp files because the API is
/// filesystem-bound; each test cleans up after itself.
/// </summary>
public class FileShredderServiceTests
{
    private static FileShredderService NewService() => new();

    private static string ProtectedRoot(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);

    // ---------- denylist: protected roots rejected (P0) ----------

    public static IEnumerable<object[]> ProtectedRoots()
    {
        // A file directly under each protected root.
        yield return [Path.Combine(ProtectedRoot(Environment.SpecialFolder.Windows), "smtest_should_never_shred.dat")];
        yield return [Path.Combine(ProtectedRoot(Environment.SpecialFolder.Windows), "System32", "smtest_should_never_shred.dat")];
        yield return [Path.Combine(ProtectedRoot(Environment.SpecialFolder.ProgramFiles), "smtest_should_never_shred.dat")];
        yield return [Path.Combine(ProtectedRoot(Environment.SpecialFolder.ProgramFilesX86), "smtest_should_never_shred.dat")];
    }

    [Theory]
    [MemberData(nameof(ProtectedRoots))]
    public async Task ShredFileAsync_UnderProtectedRoot_ThrowsSecurityException(string path)
    {
        var svc = NewService();
        await Assert.ThrowsAsync<SecurityException>(
            () => svc.ShredFileAsync(path, ShredMethod.Quick, null, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(ProtectedRoots))]
    public async Task ShredFolderAsync_UnderProtectedRoot_ThrowsSecurityException(string path)
    {
        var svc = NewService();
        await Assert.ThrowsAsync<SecurityException>(
            () => svc.ShredFolderAsync(path, ShredMethod.Quick, null, CancellationToken.None));
    }

    [Fact]
    public async Task ShredFileAsync_ProtectedRoot_IsCaseInsensitive()
    {
        // The guard uses OrdinalIgnoreCase; a lowercased system path must still be blocked.
        var svc = NewService();
        var sys32 = Path.Combine(ProtectedRoot(Environment.SpecialFolder.Windows), "System32");
        var lowered = Path.Combine(sys32.ToLowerInvariant(), "smtest_should_never_shred.dat");

        await Assert.ThrowsAsync<SecurityException>(
            () => svc.ShredFileAsync(lowered, ShredMethod.Quick, null, CancellationToken.None));
    }

    // ---------- argument / existence guards ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ShredFileAsync_NullOrWhitespace_ThrowsArgumentException(string? path)
    {
        var svc = NewService();
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException for null and
        // ArgumentException for empty/whitespace — ThrowsAny accepts both.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => svc.ShredFileAsync(path!, ShredMethod.Quick, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ShredFolderAsync_NullOrWhitespace_ThrowsArgumentException(string? path)
    {
        var svc = NewService();
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => svc.ShredFolderAsync(path!, ShredMethod.Quick, null, CancellationToken.None));
    }

    [Fact]
    public async Task ShredFileAsync_MissingFile_ThrowsFileNotFound()
    {
        var svc = NewService();
        var missing = Path.Combine(Path.GetTempPath(), "smtest_missing_" + Guid.NewGuid().ToString("N") + ".dat");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => svc.ShredFileAsync(missing, ShredMethod.Quick, null, CancellationToken.None));
    }

    [Fact]
    public async Task ShredFolderAsync_MissingFolder_ThrowsDirectoryNotFound()
    {
        var svc = NewService();
        var missing = Path.Combine(Path.GetTempPath(), "smtest_missingdir_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => svc.ShredFolderAsync(missing, ShredMethod.Quick, null, CancellationToken.None));
    }

    // ---------- positive path: a real temp file is overwritten and removed ----------

    [Fact]
    public async Task ShredFileAsync_TempFile_QuickMethod_DeletesFile()
    {
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_shred_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "sensitive data that must be destroyed");

        try
        {
            Assert.True(File.Exists(file));

            await svc.ShredFileAsync(file, ShredMethod.Quick, null, CancellationToken.None);

            Assert.False(File.Exists(file), "Quick shred did not remove the file");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFileAsync_Thorough_MultiPass_OverwritesAndDeletes()
    {
        // Held-handle regression (F48): the shred now opens ONE exclusive handle and reuses
        // it for every pass (rewinding Position=0) plus the final truncate, instead of
        // reopening the file by path each pass. A 7-pass Thorough shred exercises that reuse
        // across many passes and must still fully overwrite (multi-KB file spanning several
        // 64 KB buffer writes) and delete the file.
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_thorough_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllBytesAsync(file, new byte[200_000]); // > buffer size, forces multiple chunks/pass

        try
        {
            await svc.ShredFileAsync(file, ShredMethod.Thorough, null, CancellationToken.None);
            Assert.False(File.Exists(file), "Thorough (7-pass) shred did not remove the file");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFileAsync_ReportsProgressToCompletion()
    {
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_progress_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "some bytes");
        var progress = new SyncProgress<int>();

        try
        {
            await svc.ShredFileAsync(file, ShredMethod.Standard, progress, CancellationToken.None);

            // The service reports synchronously during the awaited shred, so the
            // final 100% report is guaranteed present once the call completes.
            Assert.Contains(100, progress.Reports);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFolderAsync_TempFolder_RemovesFolderAndContents()
    {
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_shreddir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var nested = Path.Combine(dir, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.dat"), "aaa");
        await File.WriteAllTextAsync(Path.Combine(nested, "b.dat"), "bbb");

        try
        {
            await svc.ShredFolderAsync(dir, ShredMethod.Quick, null, CancellationToken.None);

            Assert.False(Directory.Exists(dir), "Folder was not removed after shred");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- cancellation: the end state on disk must match what the caller is told (#2374) ----------

    [Fact]
    public async Task ShredFileAsync_AlreadyCancelledToken_LeavesTheFileIntact()
    {
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_cancel_" + Guid.NewGuid().ToString("N") + ".dat");
        const string original = "keep me, the operation was cancelled";
        await File.WriteAllTextAsync(file, original);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.ShredFileAsync(file, ShredMethod.Quick, null, cts.Token));

            Assert.True(File.Exists(file), "File was deleted despite cancellation before any pass ran");

            // Existence alone does not prove the file survived: the defect this pins left a file
            // present and full-length with every byte replaced by 0x00. Read it back.
            Assert.Equal(original, await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFileAsync_CancelledBetweenPasses_FinishesTheOverwriteAndRemovesTheFile()
    {
        // The defect: the token was checked at the top of every pass, so cancelling during a Standard
        // shred threw once pass 1 had overwritten the whole file — leaving it on disk at its original
        // name and length holding 0x00, while the queue reported "Cancelled". Data destroyed, user told
        // nothing happened. Now the pass in flight finishes, the rest are dropped, and the file goes.
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_cancelmid_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllBytesAsync(file, new byte[200_000]); // > one 64 KB buffer: several chunks per pass

        using var cts = new CancellationTokenSource();
        // Synchronous progress, so the cancel lands before the service evaluates the next pass —
        // System.Progress would marshal the callback and race it.
        var progress = new SyncProgress<int>(_ => cts.Cancel());

        try
        {
            var passesRun = await svc.ShredFileAsync(file, ShredMethod.Standard, progress, cts.Token);

            Assert.False(File.Exists(file),
                "Cancelling after the overwrite began left the destroyed file on disk");
            Assert.Equal(1, passesRun);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFileAsync_CancelledAfterTheFinalPass_StillRemovesTheFile()
    {
        // A second, separate end state from the same root cause: with Quick there is no next pass to
        // refuse, so the token was next observed by the FlushAsync that follows SetLength(0). It threw
        // after the truncate, leaving a zero-byte file with the original name that was never deleted.
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_canceltail_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "one pass is all this gets");

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<int>(_ => cts.Cancel());

        try
        {
            var passesRun = await svc.ShredFileAsync(file, ShredMethod.Quick, progress, cts.Token);

            Assert.False(File.Exists(file), "Cancelling on the final flush left a truncated file behind");
            Assert.Equal(1, passesRun);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ShredFolderAsync_CancelledBetweenFiles_ReportsWhatItAlreadyDestroyed()
    {
        // Cancelling a folder used to throw, which discarded the count and let the queue mark the whole
        // folder "Cancelled" — with the files it had already visited gone for good. It now returns.
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_cancelfolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 4; i++)
            await File.WriteAllTextAsync(Path.Combine(dir, $"f{i}.dat"), $"contents of file {i}");

        using var cts = new CancellationTokenSource();
        // Folder progress reports once per file, so cancelling on the first report stops the walk at the
        // boundary after file one — deterministically, with no timing assumption.
        var progress = new SyncProgress<int>(_ => cts.Cancel());

        try
        {
            var report = await svc.ShredFolderAsync(dir, ShredMethod.Quick, progress, cts.Token);

            Assert.True(report.WasCancelled);
            Assert.Equal(1, report.FilesShredded);

            // The honest end state: exactly one file destroyed and removed, the other three untouched
            // and still readable — not three files silently missing, and not four still on disk.
            Assert.Equal(3, Directory.GetFiles(dir).Length);
            Assert.True(Directory.Exists(dir), "A cancelled folder shred removed the folder anyway");
            Assert.NotNull(report.Notice);
            Assert.Contains("1 file inside had already been destroyed", report.Notice);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- symlink bypass regression (P0) ----------

    [Fact]
    public async Task ShredFileAsync_SymlinkToProtectedFile_ThrowsSecurityException()
    {
        // Regression: ValidatePath used Path.GetFullPath, which does NOT resolve
        // symlinks. A link sitting at an unprotected path but pointing into System32
        // previously passed validation and the real protected file was shredded
        // through the link. The guard must resolve the link target and block it.
        var svc = NewService();
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        // A file that reliably exists under System32; the link's *target* is what matters.
        var protectedTarget = Path.Combine(sys32, "drivers", "etc", "hosts");
        if (!File.Exists(protectedTarget))
            protectedTarget = Path.Combine(sys32, "notepad.exe");

        var link = Path.Combine(Path.GetTempPath(), "smtest_symlink_" + Guid.NewGuid().ToString("N") + ".dat");

        try
        {
            // Creating a file symlink needs privilege/developer-mode; the helper reports the skip.
            Symlinks.RequireFileLink(link, protectedTarget);

            await Assert.ThrowsAsync<SecurityException>(
                () => svc.ShredFileAsync(link, ShredMethod.Quick, null, CancellationToken.None));
        }
        finally
        {
            // Delete only the link, never its target. File.Delete on a symlink removes
            // the link itself.
            if (File.Exists(link)) File.Delete(link);
        }
    }

    [Fact]
    public async Task ValidatePath_SiblingOfProtectedRoot_IsAllowed()
    {
        // Boundary regression: the guard now matches on a directory boundary, so a
        // sibling whose name merely shares the protected prefix (e.g. a "<Windows>Apps"
        // style sibling) must NOT be blocked. Use a path that starts with the Windows
        // root string but is not under it.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sibling = windows + "Sibling_smtest"; // e.g. C:\WindowsSibling_smtest — shares prefix, not under root
        var svc = NewService();
        // Not a protected path → ShredFileAsync should fail on FileNotFound, NOT SecurityException.
        var ex = await Record.ExceptionAsync(
            () => svc.ShredFileAsync(Path.Combine(sibling, "x.dat"), ShredMethod.Quick, null, CancellationToken.None));
        Assert.IsNotType<SecurityException>(ex);
    }

    // ---------- mid-path junction bypass regression (P0) ----------

    [Fact]
    public async Task ShredFileAsync_FileBehindMidPathJunctionToProtected_ThrowsSecurityException()
    {
        // Regression: ValidatePath resolved only a LEAF link and expanded 8.3 names, but
        // neither follows a junction in a PARENT component. A junction at an unprotected
        // path pointing into System32 (creatable without admin via `mklink /J`) let a
        // path like <temp>\link\notepad.exe pass the denylist, and the real protected
        // file behind it was shredded through. The guard must canonicalize the full path.
        var svc = NewService();
        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

        var baseDir = Path.Combine(Path.GetTempPath(), "smtest_midjunc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var link = Path.Combine(baseDir, "sys32link");

        try
        {
            // A directory junction needs no admin rights — which is the whole reason this guard has to exist.
            Symlinks.RequireJunction(link, sys32);

            // A file that reliably exists under System32, reached THROUGH the junction.
            var throughJunction = Path.Combine(link, "notepad.exe");
            if (!File.Exists(throughJunction))
                throughJunction = Path.Combine(link, "drivers", "etc", "hosts");

            await Assert.ThrowsAsync<SecurityException>(
                () => svc.ShredFileAsync(throughJunction, ShredMethod.Quick, null, CancellationToken.None));
        }
        finally
        {
            // The junction goes first and as a link (Directory.Delete on a junction removes the link, not
            // its target — and the target here is System32), then the base dir.
            Symlinks.RemoveLinkThenTree(link, baseDir);
        }
    }

    // ---------- hard-link shred refusal (data shared outside the selected scope) ----------

    [Fact]
    public async Task ShredFileAsync_FileWithHardLink_Refuses_AndPreservesData()
    {
        // Regression: overwriting a hard-linked file destroys the data for EVERY name that
        // points at it — links outside the selected scope, or a link a standard user placed to
        // share a protected file's data (the path guard sees only the opened name, and
        // GetFinalPathNameByHandle resolves symlinks/junctions but NOT hard links). The shredder
        // must refuse a multi-link file rather than destroy the shared data.
        var svc = NewService();
        var target = Path.Combine(Path.GetTempPath(), "smtest_hltarget_" + Guid.NewGuid().ToString("N") + ".dat");
        var link = Path.Combine(Path.GetTempPath(), "smtest_hllink_" + Guid.NewGuid().ToString("N") + ".dat");
        const string content = "shared data behind two hard links";
        await File.WriteAllTextAsync(target, content);

        try
        {
            // A hard link needs the same volume and no admin.
            Symlinks.RequireHardLink(link, target);

            // Shredding through the link must be refused (IOException), and BOTH names plus the
            // shared data must remain intact.
            await Assert.ThrowsAsync<IOException>(
                () => svc.ShredFileAsync(link, ShredMethod.Quick, null, CancellationToken.None));

            Assert.True(File.Exists(target), "target destroyed despite the hard-link refusal");
            Assert.True(File.Exists(link), "link destroyed despite the hard-link refusal");
            Assert.Equal(content, await File.ReadAllTextAsync(target));
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task ShredFolderAsync_WithUnshreddableFile_LeavesItAndReportsFailure()
    {
        // Regression: a file the shredder cannot securely overwrite (here a hard-linked file,
        // which ShredFileAsync refuses) must be LEFT in place, never plain-deleted by a recursive
        // folder delete — otherwise the caller believes a recoverable file was securely shredded.
        // The folder shred must report the failure (throw) rather than claim success.
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_guarantee_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var normal = Path.Combine(dir, "normal.dat");
        var linkTarget = Path.Combine(Path.GetTempPath(), "smtest_gtarget_" + Guid.NewGuid().ToString("N") + ".dat");
        var hardlink = Path.Combine(dir, "hardlink.dat");
        await File.WriteAllTextAsync(normal, "shred me");
        await File.WriteAllTextAsync(linkTarget, "shared data");

        try
        {
            Symlinks.RequireHardLink(hardlink, linkTarget);

            await Assert.ThrowsAsync<IOException>(
                () => svc.ShredFolderAsync(dir, ShredMethod.Quick, null, CancellationToken.None));

            Assert.False(File.Exists(normal), "the shreddable file should have been securely shredded and removed");
            Assert.True(File.Exists(hardlink), "the hard-linked file must be LEFT in place, not plain-deleted");
            Assert.Equal("shared data", await File.ReadAllTextAsync(linkTarget));
            Assert.True(Directory.Exists(dir), "the folder holding the un-shreddable file must remain");
        }
        finally
        {
            if (File.Exists(hardlink)) File.Delete(hardlink);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (File.Exists(linkTarget)) File.Delete(linkTarget);
        }
    }

    // ---------- child-junction cleanup regression (out-of-scope directory deletion) ----------

    [Fact]
    public async Task ShredFolderAsync_WithChildJunction_DoesNotDeleteThroughIt()
    {
        // Regression: the empty-directory cleanup after a folder shred must skip junctions and
        // symlinks, exactly like the file walk. A child junction inside the selected folder used
        // to be FOLLOWED by the recursive directory enumerator, so an empty directory at the
        // junction's target — OUTSIDE the selected folder — was deleted by the cleanup pass.
        // Nothing beyond the selected folder may be touched.
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_childjunc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        // An external target, outside the shred folder, holding an EMPTY sub-directory that must
        // survive. recursive:false can only ever delete an EMPTY directory, so the empty one is
        // precisely what was at risk of deletion through the junction.
        var external = Path.Combine(Path.GetTempPath(), "smtest_external_" + Guid.NewGuid().ToString("N"));
        var victim = Path.Combine(external, "keep_me");
        Directory.CreateDirectory(victim);

        await File.WriteAllTextAsync(Path.Combine(dir, "normal.dat"), "shred me");
        var junction = Path.Combine(dir, "linkout");

        try
        {
            Symlinks.RequireJunction(junction, external);

            // Shredding the folder must not descend through the junction: the normal in-scope file
            // is shredded, but the empty directory behind the junction (outside the folder) remains.
            await svc.ShredFolderAsync(dir, ShredMethod.Quick, null, CancellationToken.None);

            Assert.True(Directory.Exists(victim),
                "an empty directory at the junction's target, outside the selected folder, was deleted through the junction");
            Assert.True(Directory.Exists(external), "the junction's external target directory was removed");
            Assert.False(File.Exists(Path.Combine(dir, "normal.dat")),
                "the normal in-scope file should still have been securely shredded");
        }
        finally
        {
            // The junction goes first and as a link, then both trees — the external one is the data this
            // test proves survives, so a recursive delete must never reach it through the link.
            Symlinks.RemoveLinkThenTree(junction, dir);
            Symlinks.RemoveTree(external);
        }
    }

    [Fact]
    public void ResolveFinalPath_MissingPath_ReturnsNull()
    {
        // A path that can't be opened must return null so ValidatePath falls back to the
        // already-validated literal form rather than throwing.
        var missing = Path.Combine(Path.GetTempPath(), "smtest_nofinal_" + Guid.NewGuid().ToString("N") + ".dat");
        Assert.Null(FileShredderService.ResolveFinalPath(missing));
    }

    [Fact]
    public void ResolveFinalPath_RealTempFile_ResolvesToItself()
    {
        // For a normal (non-link) file the canonical path equals the input, confirming the
        // handle-based resolver and \\?\ prefix stripping work. Compare against the EXPANDED
        // long form, not the raw input: GetFinalPathNameByHandle always returns the long
        // form, while %TEMP% on a CI runner can contain an 8.3 component (e.g. RUNNER~1),
        // so a raw comparison would be environment-dependent and flaky.
        var file = Path.Combine(Path.GetTempPath(), "smtest_final_" + Guid.NewGuid().ToString("N") + ".dat");
        File.WriteAllText(file, "x");
        try
        {
            var resolved = FileShredderService.ResolveFinalPath(file);
            Assert.NotNull(resolved);
            var expectedLong = FileShredderService.ExpandShortPath(Path.GetFullPath(file));
            Assert.Equal(expectedLong, resolved, ignoreCase: true);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // ---------- 8.3 short-path bypass regression ----------

    [Fact]
    public void ExpandShortPath_PathWithoutTilde_ReturnsUnchanged()
    {
        // Fast path: no '~' component means nothing to expand.
        const string p = @"C:\Program Files\SomeApp\file.dat";
        Assert.Equal(p, FileShredderService.ExpandShortPath(p));
    }

    [Fact]
    public void ExpandShortPath_NonexistentShortPath_ReturnsLiteral()
    {
        // A '~' path that doesn't resolve must fall back to the literal input rather
        // than throw, so ValidatePath still checks the path it was given.
        var p = @"C:\NOEXIS~1\nothing.dat";
        Assert.Equal(p, FileShredderService.ExpandShortPath(p));
    }

    [Fact]
    public void ExpandShortPath_ProgramFilesShortName_ExpandsToLongForm()
    {
        // Regression: C:\PROGRA~1 must expand to the real "Program Files" path so a
        // short-name alias of a protected directory can't slip past the denylist.
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.GetPathRoot(progFiles); // e.g. "C:\"
        if (string.IsNullOrEmpty(root)) return;
        var shortForm = Path.Combine(root, "PROGRA~1");

        var expanded = FileShredderService.ExpandShortPath(shortForm);

        // On volumes with 8.3 generation enabled this expands to "Program Files";
        // where 8.3 is disabled GetLongPathName returns the literal — accept either,
        // but it must never be left as a different protected-looking alias.
        Assert.True(
            expanded.Equals(progFiles, StringComparison.OrdinalIgnoreCase) ||
            expanded.Equals(shortForm, StringComparison.OrdinalIgnoreCase),
            $"Unexpected expansion: {expanded}");
    }
    // ---------- a deliberately skipped link must be reported, not passed off as success ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void ShredFolderReport_Notice_AppearsOnlyWhenSomethingWasSkipped(int skipped, bool expectNotice)
    {
        // The report is what stops a skipped junction being reported as a completed shred. With nothing
        // skipped it must stay silent, or every ordinary folder shred would grow a warning nobody needs.
        var report = new ShredFolderReport
        {
            FilesShredded = 5,
            SkippedLinks = Enumerable.Range(0, skipped).Select(i => $@"C:\temp\link{i}").ToList()
        };

        Assert.Equal(expectNotice, report.Notice is not null);
        if (expectNotice)
        {
            Assert.Contains(skipped.ToString(CultureInfo.InvariantCulture), report.Notice);
            // Singular and plural both have to read correctly — this text is shown to someone who does
            // not know what a reparse point is, and "1 shortcuts were left alone" undermines the rest.
            Assert.Contains(skipped == 1 ? "shortcut inside was" : "shortcuts inside were", report.Notice);
            Assert.Contains("still on the computer", report.Notice);
        }
    }

    [Theory]
    [InlineData(0, "before anything inside was destroyed")]
    [InlineData(1, "1 file inside had already been destroyed")]
    [InlineData(4, "4 files inside had already been destroyed")]
    public void ShredFolderReport_Notice_ForACancelledFolder_SaysWhatWasAlreadyDestroyed(
        int shredded, string expected)
    {
        // A cancelled folder shred returns instead of throwing, so this sentence is the only thing that
        // corrects the user's reasonable assumption that stopping meant nothing was destroyed (#2374).
        var report = new ShredFolderReport { FilesShredded = shredded, WasCancelled = true };

        Assert.NotNull(report.Notice);
        Assert.Contains(expected, report.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ShredFolderReport_Notice_ForACancelledFolder_AlsoNamesFilesLeftInPlace()
    {
        // Cancelling does not throw away the failures the walk had already hit. Those files are still on
        // disk and were NOT plainly deleted — a shredder that quietly downgraded to a recoverable delete
        // would break the only promise it makes.
        var report = new ShredFolderReport
        {
            FilesShredded = 2,
            FilesLeftInPlace = 3,
            WasCancelled = true
        };

        Assert.Contains("2 files inside had already been destroyed", report.Notice, StringComparison.Ordinal);
        Assert.Contains("3 files could not be securely overwritten", report.Notice, StringComparison.Ordinal);
        Assert.Contains("left in place", report.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void ShredFolderReport_Notice_StaysSilentWhenNothingHappenedWorthSaying()
    {
        // The negative half of both cases above: an ordinary completed shred with no skips and no
        // cancellation must produce no notice at all, or every folder grows a warning nobody needs.
        var report = new ShredFolderReport { FilesShredded = 9 };

        Assert.Null(report.Notice);
    }

    [Fact]
    public async Task ShredFolderAsync_FolderWithoutLinks_ReportsNoNotice()
    {
        // The ordinary case, and the one that keeps the notice honest: two real files, both shredded,
        // nothing skipped, nothing to tell the user.
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_shredplain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.dat"), "aaa");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.dat"), "bbb");

            var report = await svc.ShredFolderAsync(dir, ShredMethod.Quick, null, CancellationToken.None);

            Assert.Equal(2, report.FilesShredded);
            Assert.Empty(report.SkippedLinks);
            Assert.Null(report.Notice);
            Assert.False(Directory.Exists(dir), "an empty folder should have been removed after the shred");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public async Task ShredFolderAsync_JunctionInside_IsReportedNotSilentlySkipped()
    {
        // The walk correctly refuses to follow a junction — shredding through one would destroy data at
        // its target, outside the selected folder. But the refusal used to be recorded nowhere, not even
        // at Debug level, and the method then logged "Folder shredded successfully" while the folder was
        // still on disk (TryRemoveIfEmpty cannot remove a directory that still holds the junction, and
        // that failure is swallowed too). So the user was told their data was destroyed while looking at
        // the folder containing it.
        var svc = NewService();
        var dir = Path.Combine(Path.GetTempPath(), "smtest_shredjunc_" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "smtest_shredtarget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(outside);
        var keep = Path.Combine(outside, "must-survive.dat");
        await File.WriteAllTextAsync(keep, "data outside the selected folder");
        var link = Path.Combine(dir, "junction");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "real.dat"), "shred me");

            Symlinks.RequireJunction(link, outside);

            var report = await svc.ShredFolderAsync(dir, ShredMethod.Quick, null, CancellationToken.None);

            Assert.Contains(link, report.SkippedLinks);
            Assert.NotNull(report.Notice);
            // The safety behaviour itself must be unchanged: the file behind the junction is untouched.
            Assert.True(File.Exists(keep), "the shred followed the junction and destroyed data outside the folder");
        }
        finally
        {
            Symlinks.RemoveLinkThenTree(link, dir);
            Symlinks.RemoveTree(outside);
        }
    }
    [Fact]
    public async Task ShredFolderAsync_ReparsePointRoot_IsRefusedAndTargetSurvives()
    {
        // The root guard had no test. Handing the shredder a junction AS the folder to shred used to walk
        // straight into the link's target: the reparse-point skip inside EnumerateFilesSafe only inspects
        // child entries, never the root it starts from, so everything at the target — outside the selected
        // location entirely — would have been overwritten.
        //
        // Asserts the target's contents survive as well as the exception type. A SecurityException raised
        // after the data was already destroyed would satisfy a type-only assertion and be worthless.
        var svc = NewService();
        var outside = Path.Combine(Path.GetTempPath(), "smtest_rootjunctarget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var keep = Path.Combine(outside, "must-survive.dat");
        const string content = "data at the junction target";
        await File.WriteAllTextAsync(keep, content);

        var baseDir = Path.Combine(Path.GetTempPath(), "smtest_rootjunc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var link = Path.Combine(baseDir, "asjunction");

        try
        {
            Symlinks.RequireJunction(link, outside);

            await Assert.ThrowsAsync<SecurityException>(
                () => svc.ShredFolderAsync(link, ShredMethod.Quick, null, CancellationToken.None));

            Assert.True(File.Exists(keep), "the shred followed the root junction and destroyed its target");
            Assert.Equal(content, await File.ReadAllTextAsync(keep));
        }
        finally
        {
            Symlinks.RemoveLinkThenTree(link, baseDir);
            Symlinks.RemoveTree(outside);
        }
    }
}
