// SysManager · DuplicateFileGroupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DuplicateFileGroup"/> model — validates WastedBytes
/// calculation and property change notifications.
/// </summary>
public class DuplicateFileGroupTests
{
    [Fact]
    public void WastedBytes_CalculatesCorrectly()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 1024;
        group.Count = 3;
        // Wasted = (3 - 1) * 1024 = 2048
        Assert.Equal(2048, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_ZeroWhenCountIsOne()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 5000;
        group.Count = 1;
        Assert.Equal(0, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_ZeroWhenCountIsZero()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 5000;
        group.Count = 0;
        Assert.Equal(0, group.WastedBytes);
    }

    [Fact]
    public void WastedBytes_NotifiesWhenFileSizeChanges()
    {
        var group = new DuplicateFileGroup();
        group.Count = 3;
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.FileSize = 2048;

        Assert.Contains("WastedBytes", changed);
    }

    [Fact]
    public void WastedBytes_NotifiesWhenCountChanges()
    {
        var group = new DuplicateFileGroup();
        group.FileSize = 1024;
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.Count = 5;

        Assert.Contains("WastedBytes", changed);
    }

    [Fact]
    public void Hash_PropertyNotifies()
    {
        var group = new DuplicateFileGroup();
        var changed = new List<string>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        group.Hash = "abc123";

        Assert.Contains("Hash", changed);
    }

    [Fact]
    public void Files_Collection_IsInitialized()
    {
        var group = new DuplicateFileGroup();
        Assert.NotNull(group.Files);
        Assert.Empty(group.Files);
    }

    // ── DuplicateFileEntry tests ──

    [Fact]
    public void DuplicateFileEntry_Properties_Notify()
    {
        var entry = new DuplicateFileEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.Path = @"C:\test\file.txt";
        entry.Name = "file.txt";
        entry.SizeBytes = 4096;
        entry.LastModified = new DateTime(2026, 3, 1, 12, 0, 0);
        entry.IsSelected = true;

        Assert.Contains("Path", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("SizeBytes", changed);
        Assert.Contains("LastModified", changed);
        Assert.Contains("IsSelected", changed);
    }

    // ── Keep-rule ────────────────────────────────────────────────────────────────────────────
    //
    // Before this, a group of five identical photos was five equal rows with no hint which one was
    // the original, and IsSelected was declared on the model but read by nothing. The rule is
    // "oldest wins" — a copy is normally made after its source — and it is a HEURISTIC: a
    // timestamp-preserving copy or a cloud-sync rewrite breaks it, which is why it is shown with its
    // reason and can be overridden rather than applied silently.
    //
    // Fixed dates throughout: no DateTime.Now, so the outcome cannot depend on when the suite runs.

    private static DuplicateFileEntry File(string path, int year, int month = 1, int day = 1) =>
        new()
        {
            Path = path,
            Name = System.IO.Path.GetFileName(path),
            LastModified = new DateTime(year, month, day, 0, 0, 0)
        };

    private static DuplicateFileGroup GroupOf(params DuplicateFileEntry[] files)
    {
        var g = new DuplicateFileGroup { FileSize = 1024 };
        foreach (var f in files) g.Files.Add(f);
        g.Count = g.Files.Count;
        return g;
    }

    [Fact]
    public void ApplySuggestedKeeper_MarksTheOldestFile()
    {
        var newest = File(@"C:\photos\copy.jpg", 2026);
        var oldest = File(@"C:\photos\original.jpg", 2019);
        var middle = File(@"C:\photos\backup.jpg", 2023);
        var group = GroupOf(newest, oldest, middle);

        group.ApplySuggestedKeeper();

        Assert.True(oldest.IsSelected);
        Assert.False(newest.IsSelected);
        Assert.False(middle.IsSelected);
    }

    [Fact]
    public void ApplySuggestedKeeper_MarksExactlyOne()
    {
        // "Which one do I keep" is a single choice. Two badges would be worse than none.
        var group = GroupOf(
            File(@"C:\a\x.jpg", 2020),
            File(@"C:\b\x.jpg", 2021),
            File(@"C:\c\x.jpg", 2022),
            File(@"C:\d\x.jpg", 2023));

        group.ApplySuggestedKeeper();

        Assert.Single(group.Files, f => f.IsSelected);
    }

    [Fact]
    public void ApplySuggestedKeeper_SameTimestamp_PrefersTheShallowerPath()
    {
        // A timestamp-preserving copy leaves the dates identical, which is exactly the case the
        // heuristic cannot resolve. Falling back to the shortest path picks the file nearer the root
        // over one buried in a "New folder (2)" — and, crucially, is deterministic.
        var deep = File(@"C:\pics\2019\imports\copy\holiday.jpg", 2019);
        var shallow = File(@"C:\pics\holiday.jpg", 2019);
        var group = GroupOf(deep, shallow);

        group.ApplySuggestedKeeper();

        Assert.True(shallow.IsSelected);
        Assert.False(deep.IsSelected);
    }

    [Fact]
    public void ApplySuggestedKeeper_IsDeterministic_ForIdenticalTimestampsAndPathLengths()
    {
        // Last tiebreak. Without it the keeper would depend on enumeration order, so the same folder
        // could suggest a different file on a re-scan and the user could not trust the badge.
        var b = File(@"C:\pics\b.jpg", 2020);
        var a = File(@"C:\pics\a.jpg", 2020);
        var first = GroupOf(b, a);
        var second = GroupOf(a, b);

        first.ApplySuggestedKeeper();
        second.ApplySuggestedKeeper();

        Assert.Equal(@"C:\pics\a.jpg", first.Files.Single(f => f.IsSelected).Path);
        Assert.Equal(@"C:\pics\a.jpg", second.Files.Single(f => f.IsSelected).Path);
    }

    [Fact]
    public void ApplySuggestedKeeper_EmptyGroup_DoesNotThrow()
    {
        var group = new DuplicateFileGroup();

        var ex = Record.Exception(group.ApplySuggestedKeeper);

        Assert.Null(ex);
    }

    [Fact]
    public void ApplySuggestedKeeper_ClearsAStaleKeeper()
    {
        // Re-applying must not leave two files marked — e.g. if a group is ever re-evaluated.
        var oldest = File(@"C:\a\x.jpg", 2018);
        var newer = File(@"C:\b\x.jpg", 2024);
        newer.IsSelected = true;                 // stale mark from a previous state
        var group = GroupOf(oldest, newer);

        group.ApplySuggestedKeeper();

        Assert.True(oldest.IsSelected);
        Assert.False(newer.IsSelected);
    }

    [Fact]
    public void SetKeeper_MovesTheMarkAndClearsTheRest()
    {
        // The user overriding the suggestion is the whole point: "oldest" is a guess, and only they
        // know that the 2026 file is the edited one they actually want.
        var oldest = File(@"C:\a\x.jpg", 2019);
        var chosen = File(@"C:\b\x.jpg", 2026);
        var third = File(@"C:\c\x.jpg", 2022);
        var group = GroupOf(oldest, chosen, third);
        group.ApplySuggestedKeeper();
        Assert.True(oldest.IsSelected);          // precondition: the suggestion took

        group.SetKeeper(chosen);

        Assert.True(chosen.IsSelected);
        Assert.False(oldest.IsSelected);
        Assert.False(third.IsSelected);
        Assert.Single(group.Files, f => f.IsSelected);
    }

    [Fact]
    public void SetKeeper_ForeignEntry_ChangesNothing()
    {
        // Guards against a stale DataTemplate binding silently clearing every badge in a group.
        var kept = File(@"C:\a\x.jpg", 2019);
        var other = File(@"C:\b\x.jpg", 2020);
        var group = GroupOf(kept, other);
        group.ApplySuggestedKeeper();

        group.SetKeeper(File(@"C:\elsewhere\x.jpg", 2015));

        Assert.True(kept.IsSelected);            // untouched
        Assert.Single(group.Files, f => f.IsSelected);
    }

    [Theory]
    [InlineData(true, "Keep")]
    [InlineData(false, "")]
    public void KeepLabel_ReflectsTheMark(bool isSelected, string expected)
    {
        // The badge collapses on non-keepers rather than rendering an empty pill.
        var entry = new DuplicateFileEntry { IsSelected = isSelected };
        Assert.Equal(expected, entry.KeepLabel);
    }

    [Fact]
    public void KeepLabel_NotifiesWhenTheMarkMoves()
    {
        var entry = new DuplicateFileEntry();
        var changed = new List<string>();
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        entry.IsSelected = true;

        Assert.Contains("KeepLabel", changed);   // else the badge would not repaint on override
    }
}
