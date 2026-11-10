// SysManager · ShortcutCleanerRecheckTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Deleting the broken shortcuts a scan found judges each one again, over real shortcut files: one whose program is
/// back by then is kept.
/// </summary>
/// <remarks>
/// The shortcut is written by Windows' own shell object, in a folder the test owns, and points at a program inside that
/// folder, which the test creates between the scan and the delete. The delete is permanent, of the test's own shortcut;
/// nothing reaches the Recycle Bin or any folder outside the test's.
/// </remarks>
public sealed class ShortcutCleanerRecheckTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "SysManagerShortcutRecheck", Guid.NewGuid().ToString("N"));

    public ShortcutCleanerRecheckTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder must never fail a test run */ }
    }

    [Fact]
    public async Task AShortcutWhoseProgramIsBackBeforeTheDelete_IsKept()
    {
        var program = Path.Combine(_folder, "Acme", "Acme.exe");
        var lnk = Path.Combine(_folder, "Acme.lnk");
        var (_, _, error) = await WindowsPowerShellScript.RunAsync(
            "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk.Replace("'", "''") + "'); " +
            "$s.TargetPath = '" + program.Replace("'", "''") + "'; $s.Save()");
        Assert.True(File.Exists(lnk), $"the shortcut was not written: {error}");

        var scan = ShortcutCleanerService.Scan([("Desktop", _folder)], [], null, CancellationToken.None);
        var broken = Assert.Single(scan.Broken);
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllBytes(program, []);

        var result = ShortcutCleanerService.DeleteShortcuts([broken], toRecycleBin: false);

        Assert.Equal(0, result.Deleted);
        Assert.Same(broken, Assert.Single(result.WorkAgain));
        Assert.True(File.Exists(lnk));
    }
}
