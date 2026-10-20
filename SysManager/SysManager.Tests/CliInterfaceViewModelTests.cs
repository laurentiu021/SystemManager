// SysManager · CliInterfaceViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// What the CLI Interface tab copies. The commands named <c>SysManager.exe</c>, which is not the name of the file anyone
/// downloads, and copying a modifier's row gave a line the command line refuses.
/// </summary>
public class CliInterfaceViewModelTests
{
    private const string Exe = @"C:\Tools\SysManager-v1.2.3.exe";

    private static CliInterfaceViewModel Tab(string? exe = Exe) => new(() => exe);

    [Fact]
    public void TheExample_NamesTheFileThatIsRunning()
        => Assert.Equal($"\"{Exe}\" --cleanup --silent", Tab().Example);

    [Fact]
    public void ACommand_IsCopiedWithTheRunningFilesPath()
        => Assert.Equal($"\"{Exe}\" --cleanup", Tab().CommandFor(new("--cleanup", "")));

    [Theory]
    [InlineData("--json", "--health --json")]
    [InlineData("--silent, -s", "--health --silent")]
    public void AModifier_IsCopiedWithTheCommandThatChangesNothing(string flags, string expected)
        => Assert.Equal($"\"{Exe}\" {expected}", Tab().CommandFor(new(flags, "")));

    [Fact]
    public void WhenWindowsGivesNoPath_TheCommandsFallBackToTheBareName()
        => Assert.Equal("SysManager.exe --cleanup --silent", Tab(exe: null).Example);

    [Fact]
    public void EveryRowsCopy_IsACommandTheCommandLineRuns()
    {
        var tab = Tab();
        Assert.NotEmpty(tab.Commands);

        foreach (var row in tab.Commands)
        {
            var copied = tab.CommandFor(row);
            Assert.StartsWith($"\"{Exe}\" ", copied, StringComparison.Ordinal);
            var arguments = copied[$"\"{Exe}\" ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var request = CliRunner.Parse(arguments);
            Assert.True(request.Command is not (CliCommand.None or CliCommand.Unknown),
                $"Copying \"{row.Flags}\" gives \"{copied}\", which runs no command.");
        }
    }
}
