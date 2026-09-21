// SysManager · ContextMenuBackupRetentionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Registry exports are capped per key, and pruning one key never touches another's.
/// </summary>
/// <remarks>
/// <c>BackupRegistry</c> wrote one <c>.reg</c> per context-menu toggle into
/// <c>%LocalAppData%\SysManager\Backups\ContextMenu</c> with no retention and no reader, so the folder grew
/// for the life of the install (#2369).
/// <para>The case worth the most attention here is the prefix collision. A sanitised key name can be a prefix
/// of another's — a key <c>A</c> and a key <c>A_B</c> both produce files starting <c>A_</c> — so a naive
/// <c>A_*.reg</c> sweep would delete a different key's history while reporting success. That is the failure
/// this fix could plausibly have introduced, which is why it is tested before the happy path.</para>
/// </remarks>
public sealed class ContextMenuBackupRetentionTests : IDisposable
{
    private readonly string _dir;

    public ContextMenuBackupRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerCtxBackups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    /// <summary>Writes a backup file with the real naming shape: <c>{key}_{yyyyMMdd}_{HHmmss}.reg</c>.</summary>
    private string Plant(string safeName, string stamp)
    {
        var path = Path.Combine(_dir, $"{safeName}_{stamp}.reg");
        File.WriteAllText(path, "Windows Registry Editor Version 5.00");
        return path;
    }

    private List<string> Names() =>
        [.. Directory.EnumerateFiles(_dir).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal)];

    // ── the collision that matters ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PruningOneKey_NeverTouchesAKeyWhoseNameSharesItsPrefix()
    {
        // "A" is a prefix of "A_B". Both produce files beginning "A_", so a glob alone cannot tell them apart.
        foreach (var stamp in new[] { "20260101_000001", "20260101_000002", "20260101_000003", "20260101_000004" })
        {
            Plant("A", stamp);
            Plant("A_B", stamp);
        }

        ContextMenuService.PruneBackups(_dir, "A", keep: 2);

        var left = Names();

        // A keeps its two newest.
        Assert.Equal(["A_20260101_000003.reg", "A_20260101_000004.reg"],
            left.Where(n => ContextMenuTestHelper.BelongsTo(n, "A")).ToList());

        // A_B is untouched — all four still there.
        Assert.Equal(4, left.Count(n => n.StartsWith("A_B_", StringComparison.Ordinal)));
    }

    [Fact]
    public void AFileThatIsNotAnExport_IsLeftAlone()
    {
        // Anything without the exact _yyyyMMdd_HHmmss.reg tail is not ours to delete, however it got there.
        Plant("notepad", "20260101_000001");
        Plant("notepad", "20260101_000002");
        Plant("notepad", "20260101_000003");
        Plant("notepad", "20260101_000004");
        File.WriteAllText(Path.Combine(_dir, "notepad_notes.txt"), "user's own file");
        File.WriteAllText(Path.Combine(_dir, "notepad_backup.reg"), "no timestamp");

        ContextMenuService.PruneBackups(_dir, "notepad", keep: 1);

        var left = Names();
        Assert.Contains("notepad_notes.txt", left);
        Assert.Contains("notepad_backup.reg", left);
        Assert.Single(left, n => ContextMenuTestHelper.BelongsTo(n, "notepad"));
    }

    // ── the cap itself ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheNewestAreKept_OrderedByNameNotByDiskTime()
    {
        // Planted out of chronological order on purpose: every file is created within the same second, so
        // CreationTime cannot separate them and only the name can. If the implementation sorted by disk time
        // this would keep an arbitrary pair.
        Plant("shell", "20250301_120000");
        Plant("shell", "20260921_090000");
        Plant("shell", "20240115_235959");
        Plant("shell", "20260921_090001");

        ContextMenuService.PruneBackups(_dir, "shell", keep: 2);

        Assert.Equal(["shell_20260921_090000.reg", "shell_20260921_090001.reg"], Names());
    }

    [Fact]
    public void FewerThanTheCap_AreAllKept()
    {
        Plant("cmd", "20260101_000001");
        Plant("cmd", "20260101_000002");

        ContextMenuService.PruneBackups(_dir, "cmd", keep: ContextMenuService.BackupsKeptPerKey);

        Assert.Equal(2, Names().Count);
    }

    [Fact]
    public void TheDefaultCap_IsThree()
    {
        // Pinned because the number is quoted to the user in the tab's text, so the two can drift apart.
        Assert.Equal(3, ContextMenuService.BackupsKeptPerKey);

        for (var i = 1; i <= 6; i++) Plant("explorer", $"2026010{i}_000000");
        ContextMenuService.PruneBackups(_dir, "explorer");

        Assert.Equal(3, Names().Count);
    }

    // ── it must never break the operation it is attached to ─────────────────────────────────────────

    [Fact]
    public void AMissingDirectory_DoesNotThrow()
    {
        // Pruning runs after a successful export inside a best-effort backup. A folder that vanished between
        // the two must not surface as a failure of the context-menu change the user actually asked for.
        var gone = Path.Combine(_dir, "does-not-exist");

        Assert.Null(Record.Exception(() => ContextMenuService.PruneBackups(gone, "anything")));
    }

    [Fact]
    public void AnEmptyDirectory_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => ContextMenuService.PruneBackups(_dir, "nothing-here")));
        Assert.Empty(Names());
    }
}

/// <summary>Shared by the retention tests: does a file name belong to exactly this key?</summary>
internal static class ContextMenuTestHelper
{
    /// <summary>
    /// True when <paramref name="fileName"/> is <c>{safeName}_yyyyMMdd_HHmmss.reg</c> — the same shape the
    /// service requires, restated here so the test does not lean on the implementation to decide what it is
    /// asserting about.
    /// </summary>
    internal static bool BelongsTo(string fileName, string safeName) =>
        fileName.StartsWith(safeName + "_", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.IsMatch(
            fileName[safeName.Length..], @"\A_\d{8}_\d{6}\.reg\z");
}
