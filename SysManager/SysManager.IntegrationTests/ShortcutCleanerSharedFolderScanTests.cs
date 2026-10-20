// SysManager · ShortcutCleanerSharedFolderScanTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The shortcut scan over real shortcut files: one whose target is missing is broken in an ordinary folder and left
/// alone in a folder OneDrive shares with the user's other PCs.
/// </summary>
/// <remarks>
/// The shortcut is written by Windows' own shell object, in a folder the test owns, and points at a path that does
/// not exist. Nothing outside that folder is read or changed.
/// </remarks>
public sealed class ShortcutCleanerSharedFolderScanTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SysManagerSharedShortcuts", Guid.NewGuid().ToString("N"));

    public ShortcutCleanerSharedFolderScanTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder must never fail a test run */ }
    }

    /// <summary>Writes a shortcut to a program that is not on this PC, and returns its path.</summary>
    private async Task<string> AShortcutToAProgramThatIsNotHereAsync()
    {
        var lnk = Path.Combine(_folder, "Discord.lnk");
        var target = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "SysManagerNoSuchFolder", "Update.exe");
        var (_, _, error) = await WindowsPowerShellScript.RunAsync(
            "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk.Replace("'", "''") + "'); " +
            "$s.TargetPath = '" + target.Replace("'", "''") + "'; $s.Save()");
        Assert.True(File.Exists(lnk), $"the shortcut was not written: {error}");
        return lnk;
    }

    [Fact]
    public async Task InAnOrdinaryFolder_ItIsBroken()
    {
        // The positive control: the same shortcut outside a shared folder is what the scan exists to find.
        var lnk = await AShortcutToAProgramThatIsNotHereAsync();

        var report = ShortcutCleanerService.Scan([("Desktop", _folder)], [], null, CancellationToken.None);

        Assert.Equal(lnk, Assert.Single(report.Broken).ShortcutPath);
        Assert.Equal(0, report.SharedTargets);
    }

    [Fact]
    public async Task InAFolderOneDriveShares_ItIsLeftAlone()
    {
        await AShortcutToAProgramThatIsNotHereAsync();

        var report = ShortcutCleanerService.Scan([("Desktop", _folder)], [Path.GetDirectoryName(_folder)!], null,
            CancellationToken.None);

        Assert.Empty(report.Broken);
        Assert.Equal(1, report.SharedTargets);
        Assert.False(report.CheckedEverything);
    }
}
