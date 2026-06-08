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

    [Fact]
    public async Task ShredFileAsync_ReportsProgressToCompletion()
    {
        var svc = NewService();
        var file = Path.Combine(Path.GetTempPath(), "smtest_progress_" + Guid.NewGuid().ToString("N") + ".dat");
        await File.WriteAllTextAsync(file, "some bytes");
        var reports = new List<int>();
        var progress = new Progress<int>(reports.Add);

        try
        {
            await svc.ShredFileAsync(file, ShredMethod.Standard, progress, CancellationToken.None);

            // Progress is marshaled via the captured SynchronizationContext; give it a beat.
            await Task.Delay(50);
            Assert.Contains(100, reports);
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
}
