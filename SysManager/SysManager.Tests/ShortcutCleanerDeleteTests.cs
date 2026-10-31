// SysManager · ShortcutCleanerDeleteTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Deleting broken shortcuts removes the ticked ones and no others, the way the user chose, and counts only what went.
/// </summary>
/// <remarks>
/// No test reached <see cref="ShortcutCleanerService.DeleteShortcuts(IEnumerable{BrokenShortcut}, bool)"/>, so which
/// rows go, permanently or to the Recycle Bin, and the count the tab reports were all unchecked (#2741). The shortcuts
/// are throwaway files in a temp folder, and the Recycle Bin is a stand-in: nothing reaches the Recycle Bin of the
/// machine running the tests.
/// </remarks>
public sealed class ShortcutCleanerDeleteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ShortcutDelete_").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private BrokenShortcut Shortcut(string name, bool selected = true)
    {
        var path = Path.Combine(_dir, name + ".lnk");
        File.WriteAllBytes(path, [0x4C, 0x00, 0x00, 0x00]);   // what it holds does not matter, only that it is there
        return new BrokenShortcut { Name = name, ShortcutPath = path, IsSelected = selected };
    }

    /// <summary>A permanent delete must never reach the Recycle Bin.</summary>
    private static bool NeverCalled(string path) => throw new InvalidOperationException($"{path} was recycled");

    [Fact]
    public void Permanently_DeletesTheTickedShortcutsAndNoOthers()
    {
        var kept = Shortcut("Kept", selected: false);
        List<BrokenShortcut> rows = [Shortcut("First"), kept, Shortcut("Second")];

        var deleted = ShortcutCleanerService.DeleteShortcuts(rows, toRecycleBin: false, NeverCalled);

        Assert.Equal(2, deleted);
        Assert.Equal([kept.ShortcutPath], Directory.GetFiles(_dir));
    }

    [Fact]
    public void ToTheRecycleBin_HandsItTheTickedShortcuts_AndDeletesNoneItself()
    {
        var first = Shortcut("First");
        var second = Shortcut("Second");
        List<BrokenShortcut> rows = [first, Shortcut("Kept", selected: false), second];
        List<string> handed = [];

        // The stand-in takes each file without removing it, so a file gone afterwards was deleted by something else.
        var deleted = ShortcutCleanerService.DeleteShortcuts(rows, toRecycleBin: true, path =>
        {
            handed.Add(path);
            return true;
        });

        Assert.Equal(2, deleted);
        Assert.Equal([first.ShortcutPath, second.ShortcutPath], handed);
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void AShortcutTheRecycleBinRefuses_IsNotCounted_AndStays()
    {
        var refused = Shortcut("Refused");

        var deleted = ShortcutCleanerService.DeleteShortcuts([refused], toRecycleBin: true, _ => false);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(refused.ShortcutPath));
    }

    [Fact]
    public void AShortcutAlreadyGone_IsNotCounted()
    {
        var gone = Shortcut("Gone");
        File.Delete(gone.ShortcutPath);

        Assert.Equal(1, ShortcutCleanerService.DeleteShortcuts([gone, Shortcut("Here")], toRecycleBin: false, NeverCalled));
    }

    [Fact]
    public void AShortcutThatCannotBeDeleted_IsNotCounted_AndTheRestStillGo()
    {
        // Read-only, Windows refuses the delete as access denied; open elsewhere with no sharing, as a sharing violation.
        var readOnly = Shortcut("ReadOnly");
        File.SetAttributes(readOnly.ShortcutPath, FileAttributes.ReadOnly);
        var open = Shortcut("Open");
        using var held = new FileStream(open.ShortcutPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var deletable = Shortcut("Deletable");

        var deleted = ShortcutCleanerService.DeleteShortcuts([readOnly, open, deletable], toRecycleBin: false, NeverCalled);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(readOnly.ShortcutPath));
        Assert.True(File.Exists(open.ShortcutPath));
        Assert.False(File.Exists(deletable.ShortcutPath));
    }
}
