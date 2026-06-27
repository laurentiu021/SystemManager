// SysManager · UpdaterScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Regression tests for the self-update script hardening (updater TOCTOU).
/// Before the fix the script was written into the predictable, user-writable
/// download folder and launched via the bare "cmd.exe" name, so a same-user
/// process could pre-plant the script or a fake interpreter and have it run
/// during the elevated copy step (local elevation-of-privilege). These tests
/// pin the hardened behaviour: random isolated directory, absolute interpreter
/// path, and rejection of paths that could break out of the batch quoting.
/// </summary>
public class UpdaterScriptTests
{
    [Fact]
    public void UpdaterCmdPath_IsAbsolutePathUnderSystem32()
    {
        var path = AboutViewModel.UpdaterCmdPath;

        // Must be rooted (absolute), not a bare "cmd.exe" resolved via PATH.
        Assert.True(Path.IsPathFullyQualified(path), $"Expected absolute path, got: {path}");
        Assert.Equal("cmd.exe", Path.GetFileName(path));
        Assert.StartsWith(Environment.SystemDirectory, path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateUpdaterDirectory_IsFreshRandomAndNotTheDownloadFolder()
    {
        var downloadFolder = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysManager", "updates");

        var dir = AboutViewModel.CreateUpdaterDirectory();
        try
        {
            Assert.True(Directory.Exists(dir), "Updater directory should exist after creation.");

            // The whole point of the fix: NOT the predictable download folder.
            Assert.NotEqual(
                Path.GetFullPath(downloadFolder),
                Path.GetFullPath(dir));
            Assert.False(
                Path.GetFullPath(dir).StartsWith(Path.GetFullPath(downloadFolder), StringComparison.OrdinalIgnoreCase),
                "Updater directory must not live under the user-writable download folder.");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CreateUpdaterDirectory_ReturnsUniquePathEachCall()
    {
        var a = AboutViewModel.CreateUpdaterDirectory();
        var b = AboutViewModel.CreateUpdaterDirectory();
        try
        {
            Assert.NotEqual(a, b);
        }
        finally
        {
            if (Directory.Exists(a)) Directory.Delete(a, recursive: true);
            if (Directory.Exists(b)) Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public void BuildUpdaterScript_EmbedsBothPathsQuoted()
    {
        const string src = @"C:\Temp\SysManager-1.2.3.exe";
        const string dst = @"C:\Program Files\SysManager\SysManager.exe";

        var script = AboutViewModel.BuildUpdaterScript(1234, src, dst);

        Assert.Contains($"copy /Y \"{src}\" \"{dst}\"", script);
        Assert.Contains("PID eq 1234", script);
        // Self-cleanup removes the isolated directory, not just the script file.
        Assert.Contains("rmdir /S /Q \"%~dp0\"", script);
    }

    [Theory]
    [InlineData(@"C:\Temp\evil"" & calc & "".exe", @"C:\Program Files\SysManager\SysManager.exe")]
    [InlineData(@"C:\Temp\ok.exe", @"C:\Program Files\""whoami""\SysManager.exe")]
    public void BuildUpdaterScript_RejectsQuoteInPath(string src, string dst)
    {
        // A double quote can't legally appear in a Windows path; its presence
        // means tampering/injection. The builder must refuse rather than emit a
        // script whose quoting can be escaped.
        Assert.Throws<InvalidOperationException>(
            () => AboutViewModel.BuildUpdaterScript(1, src, dst));
    }
}
