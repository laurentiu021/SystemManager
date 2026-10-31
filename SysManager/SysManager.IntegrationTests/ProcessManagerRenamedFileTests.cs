// SysManager · ProcessManagerRenamedFileTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The process list with a running program whose file was renamed, the case a self-updater swapping its exe makes.
/// Only Windows can show it: the description is read from the file the program was started from (#2718).
/// </summary>
/// <remarks>
/// The program is a copy of <c>cmd.exe</c>, in a folder of the test's own, that says "ready" and then waits on a ping
/// of this PC, so nothing leaves it. It is known to be running once it has said so, which a copy of ping itself does not
/// do on a redirected output until it exits. It is ended with what it started, and its folder deleted, afterwards.
/// </remarks>
public class ProcessManagerRenamedFileTests
{
    private const string FolderPrefix = "SysManagerRenamedFile_";

    [Fact]
    public async Task AProgramWhoseFileWasRenamed_IsListedWithoutADescription_AndTheRestLoad()
    {
        DeleteFoldersAnEarlierRunLeft();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var folder = Directory.CreateTempSubdirectory(FolderPrefix);
        var started = Path.Combine(folder.FullName, "cmdcopy.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), started);
        using var program = Process.Start(new ProcessStartInfo(started, "/d /c \"echo ready&& ping -n 120 127.0.0.1 >nul\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;

        try
        {
            Assert.Equal("ready", await program.StandardOutput.ReadLineAsync(bounded.Token));
            File.Move(started, Path.Combine(folder.FullName, "cmdcopy.old"));

            var listed = await new ProcessManagerService().SnapshotAsync(ct: bounded.Token);

            var row = Assert.Single(listed, e => e.Pid == program.Id);
            Assert.Equal(started, row.FilePath, ignoreCase: true);
            Assert.Equal("", row.Description);
            Assert.False(row.CanOpenFileLocation);
            Assert.Contains(listed, e => e.Pid == Environment.ProcessId && e.Description.Length > 0);
        }
        finally
        {
            program.Kill(entireProcessTree: true);
            await program.WaitForExitAsync(CancellationToken.None);
            TryDelete(folder);
        }
    }

    // A folder whose program has only just ended can still be held for a moment, by the system or a virus scanner, so
    // what is left is deleted by the next run rather than failing this one.
    private static void DeleteFoldersAnEarlierRunLeft()
    {
        foreach (var left in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(FolderPrefix + "*"))
            TryDelete(left);
    }

    private static void TryDelete(DirectoryInfo folder)
    {
        try { folder.Delete(recursive: true); }
        // Held still: the next run deletes it.
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
