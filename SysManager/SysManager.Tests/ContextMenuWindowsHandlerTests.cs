// SysManager · ContextMenuWindowsHandlerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Which right-click handlers count as Windows' own (<see cref="ContextMenuService.IsWindowsOwned"/>), so "Show system
/// entries" hides them and presets leave them alone.
/// </summary>
/// <remarks>
/// In <c>ProcessEnvironment</c>, which runs alone, because some tests set the working folder: a bare file name must
/// not be judged by the folder SysManager runs in.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class ContextMenuWindowsHandlerTests : IDisposable
{
    private readonly string _before = Environment.CurrentDirectory;
    private readonly string _dir = Directory.CreateTempSubdirectory("smtest_handlers_").FullName;

    public void Dispose()
    {
        Environment.CurrentDirectory = _before;
        Directory.Delete(_dir, recursive: true);
    }

    private static string Windows(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relative);

    [Theory]
    [InlineData(@"System32\shell32.dll", true)]
    [InlineData(@"SysWOW64\shell32.dll", true)]
    // Folders inside Windows that hold other companies' files, or that any program can write to.
    [InlineData(@"System32\DriverStore\FileRepository\vendor.inf_amd64_0123456789abcdef\vendorext.dll", false)]
    [InlineData(@"System32\spool\drivers\x64\3\vendorext.dll", false)]
    [InlineData(@"Temp\vendorext.dll", false)]
    [InlineData(@"Tasks\vendorext.dll", false)]
    public void AHandlerInsideTheWindowsFolder_IsWindowsOwnOnlyWhereWindowsKeepsItsFiles(string relative, bool expected)
        => Assert.Equal(expected, ContextMenuService.IsWindowsOwned(Windows(relative)));

    /// <summary>
    /// A bare file name is judged by the file Windows loads, its copy in the System folder, never by the folder
    /// SysManager runs in.
    /// </summary>
    [Fact]
    public void ABareWindowsFileName_IsWindowsOwn_WhereverSysManagerRuns()
    {
        Environment.CurrentDirectory = _dir;

        Assert.True(ContextMenuService.IsWindowsOwned("shell32.dll"));
    }

    [Fact]
    public void ABareNameWindowsDoesNotHave_IsNotWindowsOwn_EvenRunFromTheSystemFolder()
    {
        Environment.CurrentDirectory = Environment.SystemDirectory;

        Assert.False(ContextMenuService.IsWindowsOwned("vendorext-made-up.dll"));
        Assert.False(ContextMenuService.IsWindowsIndirectString("@vendorext-made-up.dll,-1"));
    }

    [Fact]
    public void ALocalServerCommandLine_IsJudgedByItsProgram()
    {
        var vendor = Path.Combine(_dir, "Vendor Tool", "tool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vendor)!);
        File.WriteAllBytes(vendor, [0x4D, 0x5A]);

        Assert.False(ContextMenuService.IsWindowsOwned($"\"{vendor}\" -Embedding"));
        Assert.True(ContextMenuService.IsWindowsOwned($"\"{Windows(@"System32\shell32.dll")}\""));
    }
}
