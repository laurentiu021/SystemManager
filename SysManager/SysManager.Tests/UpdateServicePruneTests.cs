// SysManager · UpdateServicePruneTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="UpdateService.PruneOldDownloads"/> — the cache sweep that keeps
/// %LOCALAPPDATA%\SysManager\updates from growing by ~85 MB on every update. Each test works in
/// its own temp directory, so the developer's real update cache is never touched.
/// </summary>
public class UpdateServicePruneTests : IDisposable
{
    private readonly string _dir;

    public UpdateServicePruneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerPruneTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // Ordinal sort: the default OrderBy is culture-sensitive, which on Windows placed
    // "notes.txt" before the capitalised names and made an assertion on the expected order
    // fail for a reason unrelated to pruning.
    private string[] Remaining() =>
        Directory.GetFiles(_dir).Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Prune_RemovesSupersededBinariesAndKeepsTheCurrentOne()
    {
        Touch("SysManager-1.56.6.exe");
        Touch("SysManager-1.56.7.exe");
        var keep = Touch("SysManager-1.56.8.exe");

        var removed = UpdateService.PruneOldDownloads(_dir, keep);

        Assert.Equal(2, removed);
        Assert.Equal(["SysManager-1.56.8.exe"], Remaining());
    }

    [Fact]
    public void Prune_KeepsTheCompanionHashOfTheCurrentBinary()
    {
        // The .sha256 next to the kept binary is what lets the next launch skip a re-download.
        // Deleting it would force an avoidable 85 MB fetch.
        var keep = Touch("SysManager-1.56.8.exe");
        Touch("SysManager-1.56.8.exe.sha256");

        UpdateService.PruneOldDownloads(_dir, keep);

        Assert.Equal(["SysManager-1.56.8.exe", "SysManager-1.56.8.exe.sha256"], Remaining());
    }

    [Fact]
    public void Prune_RemovesStaleHashesAndOrphanedTempFiles()
    {
        Touch("SysManager-1.56.6.exe");
        Touch("SysManager-1.56.6.exe.sha256");
        Touch("SysManager-1.56.7.exe.tmp");   // interrupted download
        var keep = Touch("SysManager-1.56.8.exe");

        var removed = UpdateService.PruneOldDownloads(_dir, keep);

        Assert.Equal(3, removed);
        Assert.Equal(["SysManager-1.56.8.exe"], Remaining());
    }

    [Fact]
    public void Prune_LeavesUnrelatedFilesAlone()
    {
        // Only names this service writes are eligible. Anything else in the folder — including
        // a user's own file — must survive, since this runs unattended after every update.
        Touch("notes.txt");
        Touch("SomeOtherApp-2.0.exe");
        Touch("sysmanager-report.txt");
        var keep = Touch("SysManager-1.56.8.exe");

        var removed = UpdateService.PruneOldDownloads(_dir, keep);

        Assert.Equal(0, removed);
        // Membership, not order: what matters is that nothing was deleted, and directory
        // enumeration order is not a contract worth asserting.
        Assert.Equal(
            new HashSet<string>(["SomeOtherApp-2.0.exe", "SysManager-1.56.8.exe", "notes.txt", "sysmanager-report.txt"]),
            Remaining().ToHashSet());
    }

    [Fact]
    public void Prune_IsCaseInsensitiveAboutTheKeptName()
    {
        // Windows paths are case-insensitive; a casing difference must not delete the binary
        // that was just installed.
        Touch("SysManager-1.56.8.exe");
        var keepWithOtherCasing = Path.Combine(_dir, "sysmanager-1.56.8.EXE");

        var removed = UpdateService.PruneOldDownloads(_dir, keepWithOtherCasing);

        Assert.Equal(0, removed);
        Assert.Single(Remaining());
    }

    [Fact]
    public void Prune_EmptyDirectory_DoesNothing()
    {
        var removed = UpdateService.PruneOldDownloads(_dir, Path.Combine(_dir, "SysManager-1.56.8.exe"));

        Assert.Equal(0, removed);
        Assert.Empty(Remaining());
    }

    [Fact]
    public void Prune_MissingDirectory_ReturnsZeroWithoutThrowing()
    {
        var missing = Path.Combine(_dir, "no-such-folder");

        var removed = UpdateService.PruneOldDownloads(missing, Path.Combine(missing, "SysManager-1.0.0.exe"));

        Assert.Equal(0, removed);
    }

    [Fact]
    public void Prune_FileHeldOpen_SurvivesAndDoesNotThrow()
    {
        // The binary of a running instance cannot be deleted. Pruning must treat that as
        // ordinary, prune what it can, and leave the locked file for the next round.
        var locked = Touch("SysManager-1.56.6.exe");
        Touch("SysManager-1.56.7.exe");
        var keep = Touch("SysManager-1.56.8.exe");

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var removed = UpdateService.PruneOldDownloads(_dir, keep);

            Assert.Equal(1, removed);   // only 1.56.7 could go
            Assert.Contains("SysManager-1.56.6.exe", Remaining());
            Assert.Contains("SysManager-1.56.8.exe", Remaining());
        }
    }

    [Fact]
    public void Prune_RunTwice_IsIdempotent()
    {
        Touch("SysManager-1.56.6.exe");
        var keep = Touch("SysManager-1.56.8.exe");

        Assert.Equal(1, UpdateService.PruneOldDownloads(_dir, keep));
        Assert.Equal(0, UpdateService.PruneOldDownloads(_dir, keep));
        Assert.Equal(["SysManager-1.56.8.exe"], Remaining());
    }
}
