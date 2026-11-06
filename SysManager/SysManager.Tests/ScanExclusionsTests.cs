// SysManager · ScanExclusionsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The folders and files the scans leave out answer exactly as the lists they replaced did (#2854).
/// </summary>
/// <remarks>
/// Each scanner asked its own lists through <c>Any</c> with a lambda, a closure and a delegate for every file and
/// folder. The lists are a <c>SearchValues</c> and a <c>FrozenSet</c> now, and these hold them to the answers of the
/// old rule, written out here as it was, over paths and names chosen to sit on its edges: case, a folder name cut
/// short or run on, one inside another folder, and characters outside ASCII.
/// </remarks>
public class ScanExclusionsTests
{
    private static readonly string[] ScannerFolders =
        [@"\$recycle.bin", @"\system volume information", @"\windows\winsxs", @"\windows\system32\config",
         @"\windows\csc"];

    private static readonly string[] DiskAnalyzerFolders =
        [@"\$recycle.bin", @"\system volume information", @"\windows\winsxs", @"\windows\csc"];

    private static readonly string[] Files = ["pagefile.sys", "hiberfil.sys", "swapfile.sys"];

    private static bool AsBefore(string[] folders, string path) =>
        folders.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Paths(string[] folders)
    {
        foreach (var folder in ScannerFolders.Concat(folders).Distinct())
        {
            yield return @"C:" + folder;
            yield return @"C:" + folder.ToUpperInvariant() + @"\inside";
            yield return @"C:" + folder[..^1];
            yield return @"X:\a" + folder + "b";
            yield return @"\\server\share" + folder;
        }

        yield return @"C:\Users\me\Documents";
        yield return @"D:\Games\Steam\steamapps\common";
        yield return @"C:\Windows\System32\Config\SAM";
        yield return @"C:\windows\system32\configuration";
        yield return @"C:\windows\system32\conf";
        yield return @"C:\Ünïcödé\$RECYCLE.BIN\S-1-5-21";
        yield return @"C:\Windows\WinSxS";
        yield return @"C:\Windows";
        yield return "";
    }

    [Fact]
    public void TheScannersFolderCheck_AnswersAsTheListItReplaced()
    {
        var paths = Paths(ScannerFolders).ToList();

        foreach (var path in paths)
        {
            Assert.True(AsBefore(ScannerFolders, path) == path.AsSpan().ContainsAny(ScanExclusions.SystemFolders),
                $"'{path}' is answered differently from the list it replaced");
        }

        // Vacuity floor: the corpus holds both answers.
        Assert.Contains(paths, path => AsBefore(ScannerFolders, path));
        Assert.Contains(paths, path => !AsBefore(ScannerFolders, path));
    }

    [Fact]
    public void DiskAnalyzersFolderCheck_AnswersAsTheListItReplaced()
    {
        var shouldSkip = typeof(DiskAnalyzerService)
            .GetMethod("ShouldSkip", BindingFlags.NonPublic | BindingFlags.Static)!;
        var paths = Paths(DiskAnalyzerFolders).ToList();

        foreach (var path in paths)
        {
            Assert.True(AsBefore(DiskAnalyzerFolders, path) == (bool)shouldSkip.Invoke(null, [path])!,
                $"'{path}' is answered differently from the list it replaced");
        }

        // Disk Analyzer measures System32\config, which the two file scanners do not enter.
        Assert.False((bool)shouldSkip.Invoke(null, [@"C:\Windows\System32\config"])!);
    }

    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("HIBERFIL.SYS")]
    [InlineData("SwapFile.Sys")]
    [InlineData("pagefile.sys ")]
    [InlineData("pagefile")]
    [InlineData("my-pagefile.sys.bak")]
    [InlineData("holiday-video.mp4")]
    [InlineData("")]
    public void TheWindowsManagedFiles_AnswerAsTheListTheyReplaced(string name)
    {
        var before = Files.Any(file => name.Equals(file, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(before, ScanExclusions.WindowsManagedFiles.Contains(name));
    }
}
