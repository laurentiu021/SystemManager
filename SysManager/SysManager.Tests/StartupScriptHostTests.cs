// SysManager · StartupScriptHostTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// What Startup Manager says about an entry whose program runs something else (a script through PowerShell, a DLL
/// through rundll32), an entry with a bare program name, and a scheduled task, whose command is its description.
/// </summary>
/// <remarks>
/// In <c>ProcessEnvironment</c>, which runs alone, because several tests set the working folder: a bare name must
/// not be judged by the folder SysManager runs in, so each test chooses that folder rather than inheriting it.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class StartupScriptHostTests : IDisposable
{
    private readonly string _before = Environment.CurrentDirectory;
    private readonly string _dir = Directory.CreateTempSubdirectory("smtest_startup_hosts_").FullName;

    public void Dispose()
    {
        Environment.CurrentDirectory = _before;
        Directory.Delete(_dir, recursive: true);
    }

    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static StartupEntry Run(string command) => new() { Name = "Entry", Command = command };

    /// <summary>
    /// An entry that runs a script through Windows PowerShell is not labelled part of Windows: what it runs is the
    /// script, and the row names it.
    /// </summary>
    [Fact]
    public void AScriptRunThroughPowerShell_IsNotCalledPartOfWindows_AndNamesTheScript()
    {
        var script = Path.Combine(_dir, "run.ps1");
        File.WriteAllText(script, "Write-Output 1");
        var entry = Run($"\"{PowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"{script}\"");

        StartupService.EnrichWithDescriptions([entry]);

        Assert.NotEqual("System", entry.Safety);
        Assert.Contains(script, entry.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A DLL run through rundll32 is not given rundll32's publisher and signature: the file that runs is the DLL.
    /// </summary>
    [Fact]
    public void ADllRunThroughRundll32_DoesNotBorrowRundll32sPublisherOrSignature()
    {
        var dll = Path.Combine(_dir, "helper.dll");
        File.WriteAllBytes(dll, [0x4D, 0x5A]);
        var command = $"{Path.Combine(Environment.SystemDirectory, "rundll32.exe")} \"{dll}\",Start";
        var entry = StartupService.BuildStartupFolderEntry(
            Path.Combine(_dir, "Helper.lnk"), command, "User Startup Folder");

        StartupService.VerifySignatures([entry]);

        Assert.NotEqual(SignatureTrust.Verified, entry.Signature);
        Assert.DoesNotContain("Microsoft", entry.Publisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bare Windows program name is judged where Windows finds it, not by the folder SysManager runs in, so the real
    /// Explorer is not called an impostor.
    /// </summary>
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("explorer.exe /n")]
    public void ABareWindowsProgramName_IsNotCalledAnImpostor(string command)
    {
        Environment.CurrentDirectory = _dir;
        var entry = Run(command);

        StartupService.EnrichWithDescriptions([entry]);

        Assert.Equal("System", entry.Safety);
        Assert.DoesNotContain("Not recognised", entry.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare name Windows' own folders do not have is neither vouched for nor accused: nothing resolved it, so the row
    /// carries no chip and no claim about where it runs.
    /// </summary>
    [Fact]
    public void ABareNameWindowsDoesNotHave_IsNeitherVouchedForNorAccused()
    {
        Environment.CurrentDirectory = Environment.SystemDirectory;
        var entry = Run("Widgets.exe");

        StartupService.EnrichWithDescriptions([entry]);

        Assert.Equal("", entry.Safety);
        Assert.DoesNotContain("Not recognised", entry.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scheduled task's command is its description, so the first word of it never picks a Windows program, a chip
    /// or a signature.
    /// </summary>
    [Theory]
    [InlineData("System maintenance for Contoso")]
    [InlineData("Control centre helper")]
    [InlineData("Explorer helper for Contoso")]
    public void AScheduledTaskDescription_NeverPicksAWindowsProgram(string description)
    {
        Environment.CurrentDirectory = Environment.SystemDirectory;
        var entry = new StartupEntry { Name = "Task", Command = description, Source = StartupSource.TaskScheduler };

        StartupService.EnrichWithDescriptions([entry]);
        StartupService.VerifySignatures([entry]);

        Assert.Equal("", entry.Safety);
        Assert.Equal(SignatureTrust.Unknown, entry.Signature);
    }

    /// <summary>
    /// A program merely named like PowerShell, somewhere Windows does not keep its programs, is not given the host
    /// treatment: it is judged as itself, a copy that is not recognised.
    /// </summary>
    [Fact]
    public void AHostNamedCopyElsewhere_IsJudgedAsItself()
    {
        var copy = Path.Combine(_dir, "powershell.exe");
        File.WriteAllBytes(copy, [0x4D, 0x5A]);
        var entry = Run($"\"{copy}\" -File \"{Path.Combine(_dir, "y.ps1")}\"");

        StartupService.EnrichWithDescriptions([entry]);

        Assert.Equal("Unknown", entry.Safety);
        Assert.Contains("Not recognised", entry.Description, StringComparison.Ordinal);
    }
}
