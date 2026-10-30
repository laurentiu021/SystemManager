// SysManager · UpdateServiceWinGetDetectionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// A copy winget's portable installer manages must be recognised whether the running path is the real
/// executable under <c>WinGet\Packages</c> or the PATH shim winget creates at <c>WinGet\Links</c> — and
/// nothing else is mistaken for one (#2889).
/// </summary>
public sealed class UpdateServiceWinGetDetectionTests
{
    private const string LocalAppData = @"C:\Users\test\AppData\Local";
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";

    private const string UserPackagesExe =
        @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\laurentiu021.SysManager_Microsoft.Winget.Source_8wekyb3d8bbwe\SysManager.exe";

    private const string UserLinkShim =
        @"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\SysManager.exe";

    private const string MachinePackagesExe =
        @"C:\Program Files\WinGet\Packages\laurentiu021.SysManager_Microsoft.Winget.Source_8wekyb3d8bbwe\SysManager.exe";

    private const string MachineLinkShim =
        @"C:\Program Files\WinGet\Links\SysManager.exe";

    private static bool IsRunningFromWinGet(string? processPath)
        => UpdateService.IsRunningFromWinGet(processPath, LocalAppData, ProgramFiles, ProgramFilesX86);

    [Theory]
    [InlineData(UserPackagesExe)]
    [InlineData(UserLinkShim)]
    [InlineData(MachinePackagesExe)]
    [InlineData(MachineLinkShim)]
    public void IsRunningFromWinGet_RecognisesBothThePackagesCopyAndTheLinksShim(string processPath)
        => Assert.True(IsRunningFromWinGet(processPath));

    [Fact]
    public void IsRunningFromWinGet_IsCaseInsensitive()
        => Assert.True(IsRunningFromWinGet(UserPackagesExe.ToUpperInvariant()));

    [Fact]
    public void IsRunningFromWinGet_AcceptsAFileDeeperInThePackageFolder()
    {
        var nested = Path.Combine(
            LocalAppData, "Microsoft", "WinGet", "Packages",
            "laurentiu021.SysManager_Microsoft.Winget.Source_8wekyb3d8bbwe", "some-subfolder", "SysManager.exe");

        Assert.True(IsRunningFromWinGet(nested));
    }

    [Theory]
    [InlineData(@"C:\Users\test\AppData\Local\Programs\SysManager\SysManager.exe")] // a plain portable install
    [InlineData(@"D:\Tools\SysManager.exe")] // run from anywhere else
    // A sibling package whose id merely starts the same must not match on a bare prefix.
    [InlineData(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\laurentiu021.SysManagerX_Microsoft.Winget.Source_8wekyb3d8bbwe\SysManagerX.exe")]
    // The Links shim for a DIFFERENT package must not match just because it sits in the same Links folder.
    [InlineData(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\OtherApp.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void IsRunningFromWinGet_RejectsEverythingElse(string? processPath)
        => Assert.False(IsRunningFromWinGet(processPath));

    [Fact]
    public void IsRunningFromWinGet_WithUnavailableSpecialFolders_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            UpdateService.IsRunningFromWinGet(UserPackagesExe, localAppData: null, programFiles: null, programFilesX86: null));

        Assert.Null(ex);
    }

    [Fact]
    public void IsRunningFromWinGet_WithUnavailableSpecialFolders_StillRejectsAWinGetLookingPath()
        => Assert.False(UpdateService.IsRunningFromWinGet(UserPackagesExe, localAppData: null, programFiles: null, programFilesX86: null));

    [Fact]
    public void IsRunningFromWinGet_PublicOverload_DoesNotThrowAgainstTheRealMachine()
    {
        // Exercises the zero-arg-folder overload against this machine's real Environment.GetFolderPath —
        // the point is that it runs to completion, not what it returns (this dev machine has no winget
        // install of SysManager, so false is simply the correct answer, not a fixture).
        var ex = Record.Exception(() => UpdateService.IsRunningFromWinGet(@"C:\SysManager\SysManager.exe"));

        Assert.Null(ex);
    }
}
