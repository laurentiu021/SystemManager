// SysManager · FileShredderServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
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

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> that records reports on the calling
    /// thread. Unlike <see cref="Progress{T}"/> (which marshals callbacks via the
    /// captured SynchronizationContext asynchronously), this captures every report
    /// deterministically by the time the awaited call returns — no timing race.
    /// </summary>
    private sealed class SyncProgress : IProgress<int>
    {
        public List<int> Reports { get; } = [];
        public void Report(int value) => Reports.Add(value);
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
        var progress = new SyncProgress();

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

    [Fact]
    public async Task ShredFileAsync_AlreadyCancelledToken_DoesNotDeleteFile()
    {
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_cancel_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "keep me, the operation was cancelled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => svc.ShredFileAsync(file, ShredMethod.Quick, null, cts.Token));

            Assert.True(File.Exists(file), "File was deleted despite cancellation before any pass ran");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
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
            // Creating a file symlink needs privilege/developer-mode. If unavailable,
            // skip the assertion rather than fail on an environment limitation.
            try { File.CreateSymbolicLink(link, protectedTarget); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

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
            // mklink /J creates a directory junction; no admin rights required.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{sys32}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                proc.WaitForExit(10_000);
                if (proc.ExitCode != 0 || !IsReparse(link))
                    return; // environment can't create junctions (rare) — nothing to assert
            }

            // A file that reliably exists under System32, reached THROUGH the junction.
            var throughJunction = Path.Combine(link, "notepad.exe");
            if (!File.Exists(throughJunction))
                throughJunction = Path.Combine(link, "drivers", "etc", "hosts");

            await Assert.ThrowsAsync<SecurityException>(
                () => svc.ShredFileAsync(throughJunction, ShredMethod.Quick, null, CancellationToken.None));
        }
        finally
        {
            // Delete the junction itself (Directory.Delete on a junction removes the link,
            // not its target), then the base dir.
            try { Directory.Delete(link); } catch { /* ignore */ }
            try { Directory.Delete(baseDir, recursive: true); } catch { /* ignore */ }
        }

        static bool IsReparse(string p)
        {
            try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint; }
            catch { return false; }
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
            // mklink /H creates a hard link on the same volume; no admin required. If the
            // environment can't create one, skip rather than fail on an environment limitation.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = System.Diagnostics.Process.Start(psi)!)
            {
                proc.WaitForExit(10_000);
                if (proc.ExitCode != 0 || !File.Exists(link))
                    return; // couldn't create a hard link here — nothing to assert
            }

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
}
