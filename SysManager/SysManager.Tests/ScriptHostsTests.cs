// SysManager · ScriptHostsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="ScriptHosts"/>: what a Windows helper runs, read from its command line. The file system is a stand-in
/// that holds only the files each row names, so no row depends on this PC.
/// </summary>
public sealed class ScriptHostsTests
{
    private const string Sys = @"C:\Windows\System32";

    /// <summary>The files the stand-in file system holds.</summary>
    private static readonly HashSet<string> Present = new(StringComparer.OrdinalIgnoreCase)
    {
        $@"{Sys}\WindowsPowerShell\v1.0\powershell.exe", $@"{Sys}\cmd.exe", $@"{Sys}\wscript.exe",
        $@"{Sys}\cscript.exe", $@"{Sys}\mshta.exe", $@"{Sys}\rundll32.exe", $@"{Sys}\regsvr32.exe",
        $@"{Sys}\conhost.exe", @"C:\Windows\explorer.exe", $@"{Sys}\msiexec.exe", $@"{Sys}\mmc.exe",
        @"C:\Windows\regedit.exe", $@"{Sys}\wsl.exe",
        @"C:\Users\someone\AppData\Roaming\Updater\run.ps1", @"C:\Tools\My Scripts\start up.ps1",
        @"C:\Tools\job.bat", @"C:\Tools\hello.vbs", @"C:\Tools\page.hta", @"C:\Tools\helper.dll",
        @"C:\Tools\a.dll", @"C:\Tools\b.dll", @"C:\Tools\setup.msi", @"C:\Tools\console.exe",
        @"C:\Tools\snap.msc", @"C:\Tools\settings.reg",
    };

    private static HostedRun? Read(string command) => ScriptHosts.Hosted(command, Present.Contains);

    [Theory]
    // PowerShell given a file, by every spelling of -File, with value-taking switches before it.
    [InlineData(@"powershell -File C:\Users\someone\AppData\Roaming\Updater\run.ps1",
        @"C:\Users\someone\AppData\Roaming\Updater\run.ps1")]
    [InlineData(
        @"powershell.exe -NoProfile -ExecutionPolicy Bypass -w Hidden -f ""C:\Tools\My Scripts\start up.ps1""",
        @"C:\Tools\My Scripts\start up.ps1")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -ep Bypass -nop -File:C:\Tools\job.bat",
        @"C:\Tools\job.bat")]
    [InlineData(
        @"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" /NoLogo -Version 5.1 -File C:\Tools\job.bat",
        @"C:\Tools\job.bat")]
    // PowerShell 7 reads a first argument that is not a parameter as a script.
    [InlineData(@"pwsh C:\Tools\job.bat", @"C:\Tools\job.bat")]
    // Command Prompt given a file, with switches before /c, /k, a quoted line and start with its title.
    [InlineData(@"cmd /c C:\Tools\job.bat", @"C:\Tools\job.bat")]
    [InlineData(@"cmd.exe /q /d /C ""C:\Tools\job.bat"" --quiet", @"C:\Tools\job.bat")]
    [InlineData(@"cmd /k""C:\Tools\job.bat""", @"C:\Tools\job.bat")]
    [InlineData(@"cmd /c start """" /min C:\Tools\console.exe", @"C:\Tools\console.exe")]
    [InlineData(@"cmd /c start /d C:\Tools C:\Tools\console.exe", @"C:\Tools\console.exe")]
    // One level of a helper inside another: what the inner one runs.
    [InlineData(@"cmd /c powershell -File C:\Tools\job.bat", @"C:\Tools\job.bat")]
    [InlineData(@"conhost.exe 0xffffffff -ForceV1 --width 80 powershell -File C:\Tools\job.bat", @"C:\Tools\job.bat")]
    [InlineData(@"conhost --headless C:\Tools\console.exe", @"C:\Tools\console.exe")]
    // Windows Script Host, mshta, rundll32, regsvr32.
    [InlineData(@"wscript //B //Nologo C:\Tools\hello.vbs", @"C:\Tools\hello.vbs")]
    [InlineData(@"cscript C:\Tools\hello.vbs arg", @"C:\Tools\hello.vbs")]
    [InlineData(@"mshta C:\Tools\page.hta", @"C:\Tools\page.hta")]
    [InlineData(@"rundll32.exe ""C:\Tools\helper.dll"",Start", @"C:\Tools\helper.dll")]
    [InlineData(@"rundll32 C:\Tools\helper.dll,Start extra", @"C:\Tools\helper.dll")]
    [InlineData(@"regsvr32 /s C:\Tools\helper.dll", @"C:\Tools\helper.dll")]
    // Programs that open what they are given.
    [InlineData(@"msiexec /i C:\Tools\setup.msi /qn", @"C:\Tools\setup.msi")]
    [InlineData(@"msiexec /package ""C:\Tools\setup.msi""", @"C:\Tools\setup.msi")]
    [InlineData(@"mmc C:\Tools\snap.msc", @"C:\Tools\snap.msc")]
    [InlineData(@"regedit /s C:\Tools\settings.reg", @"C:\Tools\settings.reg")]
    [InlineData(@"explorer.exe C:\Tools\console.exe", @"C:\Tools\console.exe")]
    // Variables are expanded before the file is looked for.
    [InlineData(@"powershell -File %SystemDrive%\Tools\job.bat", @"C:\Tools\job.bat")]
    public void AHelperGivenAFile_IsAttributedToThatFile(string command, string file)
    {
        var run = Read(command);

        Assert.NotNull(run);
        Assert.Equal(file, run.TargetPath, ignoreCase: true);
        Assert.Equal(file, run.Target, ignoreCase: true);
    }

    [Theory]
    // Commands rather than files: the run is named, and nothing is attributed to a file.
    [InlineData(@"powershell -Command ""Start-Process x""", "a PowerShell command")]
    [InlineData(@"powershell -c Get-Date", "a PowerShell command")]
    [InlineData(@"powershell -EncodedCommand ZQBjAGgAbwA=", "a PowerShell command")]
    [InlineData(@"powershell -e ZQBjAGgAbwA=", "a PowerShell command")]
    // Windows PowerShell 5.1 reads a first argument that is not a parameter as a command.
    [InlineData(@"powershell Get-Date", "a PowerShell command")]
    // A parameter several begin with, which this cannot place, is not guessed.
    [InlineData(@"powershell -n x", "a PowerShell command")]
    [InlineData(@"powershell -", "a PowerShell command")]
    // One file never vouches for a whole command.
    [InlineData(@"cmd /c C:\Tools\job.bat & C:\Tools\console.exe", "a Command Prompt command")]
    [InlineData(@"cmd /c C:\Tools\job.bat > C:\out.txt", "a Command Prompt command")]
    [InlineData(@"cmd /c echo hi", "echo")]
    [InlineData(@"regsvr32 C:\Tools\a.dll C:\Tools\b.dll", "several files")]
    [InlineData(@"regsvr32 /i:http://example.test/x.sct scrobj.dll", "a registration command")]
    [InlineData(@"mshta https://example.test/page", "a web page")]
    [InlineData(@"mshta vbscript:Close(1)", "a web page")]
    [InlineData(@"rundll32 -sta {00000000-0000-0000-0000-000000000000}", "a COM object")]
    [InlineData(@"rundll32 -localserver {00000000-0000-0000-0000-000000000000}", "a COM object")]
    [InlineData(@"msiexec /i{00000000-0000-0000-0000-000000000000}", "an installed program's setup")]
    [InlineData(@"msiexec /x {00000000-0000-0000-0000-000000000000}", "an installed program's setup")]
    [InlineData(@"wsl -e sh -c ""echo hi""", "a command in WSL")]
    [InlineData(@"wt -p Ubuntu", "a command in Windows Terminal")]
    public void AHelperGivenACommand_HasNoFileToAttribute(string command, string target)
    {
        var run = Read(command);

        Assert.NotNull(run);
        Assert.Equal(target, run.Target, ignoreCase: true);
        Assert.Equal("", run.TargetPath);
    }

    [Theory]
    // A bare or relative file is never looked for; it is named, with nothing attributed.
    [InlineData(@"rundll32.exe shell32.dll,Control_RunDLL", "shell32.dll")]
    [InlineData(@"powershell -File run.ps1", "run.ps1")]
    [InlineData(@"wscript Tools\hello.vbs", @"Tools\hello.vbs")]
    // A network or device path is never probed.
    [InlineData(@"powershell -File \\server\share\run.ps1", @"\\server\share\run.ps1")]
    [InlineData(@"powershell -File \\?\C:\Tools\job.bat", @"\\?\C:\Tools\job.bat")]
    // A full path to a file that is not there.
    [InlineData(@"powershell -File C:\Nowhere\gone.ps1", @"C:\Nowhere\gone.ps1")]
    public void AFileThatCannotBeChecked_IsNamed_ButNotAttributed(string command, string target)
    {
        var run = Read(command);

        Assert.NotNull(run);
        Assert.Equal(target, run.Target);
        Assert.Equal("", run.TargetPath);
    }

    [Theory]
    // Given nothing to run, a helper is an ordinary program.
    [InlineData(@"explorer.exe /n")]
    [InlineData(@"cmd")]
    [InlineData(@"cmd /q")]
    [InlineData(@"cmd echo")]
    [InlineData(@"powershell")]
    [InlineData(@"powershell -NoProfile -NoLogo")]
    [InlineData(@"wscript //B")]
    [InlineData(@"rundll32")]
    [InlineData(@"regsvr32 /s")]
    [InlineData(@"mshta")]
    [InlineData(@"msiexec /qn")]
    [InlineData(@"conhost 0xffffffff")]
    [InlineData(@"wsl")]
    // Not one of the helpers, including a program whose name only begins like one.
    [InlineData(@"C:\Tools\console.exe --run")]
    [InlineData(@"C:\Tools\powershell-helper.exe -File C:\Tools\job.bat")]
    [InlineData(@"")]
    [InlineData(@"   ")]
    public void ACommandThatIsNotAHostedRun_IsNull(string command) => Assert.Null(Read(command));

    /// <summary>The parser never throws on text it cannot make sense of.</summary>
    [Theory]
    [InlineData(@"regsvr32 """"")]
    [InlineData(@"powershell -File """)]
    [InlineData(@"cmd /c """)]
    [InlineData(@"rundll32 "",")]
    [InlineData(@"powershell -File C:\a\\""b")]
    [InlineData(@"conhost --width")]
    [InlineData(@"cmd /c start """)]
    [InlineData(@"msiexec /i")]
    [InlineData("\"")]
    public void TextItCannotRead_NeverThrows(string command)
    {
        var exception = Record.Exception(() => Read(command));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("powershell", true)]
    [InlineData("PowerShell", true)]
    [InlineData("cmd", true)]
    [InlineData("conhost", true)]
    [InlineData("rundll32", true)]
    // Explorer and Windows Installer do work of their own.
    [InlineData("explorer", false)]
    [InlineData("msiexec", false)]
    [InlineData("chrome", false)]
    public void IsScriptHost_IsTheHelpersThatRunWhatOthersName(string name, bool expected)
        => Assert.Equal(expected, ScriptHosts.IsScriptHost(name));

    [Theory]
    [InlineData(@"C:\Tools\console.exe", "", @"C:\Tools\console.exe")]
    [InlineData(@"C:\Tools\console.exe", "   ", @"C:\Tools\console.exe")]
    [InlineData(@"C:\Tools\console.exe", null, @"C:\Tools\console.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"-File C:\Tools\job.bat ",
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -File C:\Tools\job.bat")]
    [InlineData(@"C:\Program Files\App\app.exe", "--tray", @"""C:\Program Files\App\app.exe"" --tray")]
    [InlineData(@"""C:\Program Files\App\app.exe""", "--tray", @"""C:\Program Files\App\app.exe"" --tray")]
    [InlineData("", "--tray", "")]
    public void ShortcutCommand_IsTheTargetThenItsArguments(string target, string? arguments, string expected)
        => Assert.Equal(expected, StartupService.ShortcutCommand(target, arguments));
}
