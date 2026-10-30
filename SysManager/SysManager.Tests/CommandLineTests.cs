// SysManager · CommandLineTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The program a command line starts, read the way Windows reads it (#2708).
/// </summary>
/// <remarks>
/// Over a stand-in for the file system, so what counts as there is what each test says and not what the PC running the
/// suite has installed.
/// </remarks>
public class CommandLineTests
{
    private static Func<string, bool> Only(params string[] files) =>
        path => files.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static readonly Func<string, bool> Nothing = _ => false;

    [Theory]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\vlc.exe"" --started-from-file ""%1""")]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\vlc.exe"" ""%1""")]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\vlc.exe""")]
    [InlineData(@"  ""C:\Program Files\VideoLAN\VLC\vlc.exe"" /x")]
    public void AQuotedProgram_RunsToItsClosingQuote(string command)
    {
        // The old parsers kept the closing quote and cut at the first space, inside the path: "C:\Program".
        Assert.Equal(@"C:\Program Files\VideoLAN\VLC\vlc.exe", CommandLine.Program(command, Nothing));
    }

    [Fact]
    public void AQuotedProgramWithNoClosingQuote_IsTheRestOfTheLine()
    {
        Assert.Equal(@"C:\Program Files\App\app.exe", CommandLine.Program(@"""C:\Program Files\App\app.exe", Nothing));
    }

    [Fact]
    public void AnUnquotedProgramWithArguments_IsTheFirstPrefixThatIsAFile()
    {
        var program = CommandLine.Program(@"C:\Windows\System32\cmd.exe /s /k pushd ""%V""",
            Only(@"C:\Windows\System32\cmd.exe"));

        Assert.Equal(@"C:\Windows\System32\cmd.exe", program);
    }

    [Fact]
    public void AnUnquotedPathWithSpaces_IsTriedAtEverySpace_AsWindowsTriesIt()
    {
        var program = CommandLine.Program(@"C:\Program Files\Some App\app.exe --tray",
            Only(@"C:\Program Files\Some App\app.exe"));

        Assert.Equal(@"C:\Program Files\Some App\app.exe", program);
    }

    [Fact]
    public void AnUnquotedPathWithSpacesAndNoArguments_IsTheWholeLine()
    {
        var program = CommandLine.Program(@"C:\Program Files\Some App\app.exe",
            Only(@"C:\Program Files\Some App\app.exe"));

        Assert.Equal(@"C:\Program Files\Some App\app.exe", program);
    }

    [Fact]
    public void AnUnquotedProgramWithoutItsExtension_IsFoundWithIt()
    {
        var program = CommandLine.Program(@"C:\Tools\helper --quiet", Only(@"C:\Tools\helper.exe"));

        Assert.Equal(@"C:\Tools\helper.exe", program);
    }

    [Theory]
    [InlineData(@"notepad.exe %1", "notepad.exe")]
    [InlineData(@"wscript.exe ""C:\scripts\a b.vbs""", "wscript.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe")]
    [InlineData("single", "single")]
    public void AProgramThatIsNoFileHere_IsTheFirstToken(string command, string expected)
    {
        Assert.Equal(expected, CommandLine.Program(command, Nothing));
    }

    [Fact]
    public void AnEnvironmentVariable_IsExpanded()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot")!;

        Assert.Equal(Path.Combine(root, "system32", "cmd.exe"),
            CommandLine.Program(@"""%SystemRoot%\system32\cmd.exe"" /c", Nothing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCommandLine_HasNoProgram(string? command)
    {
        Assert.Equal("", CommandLine.Program(command, Nothing));
    }
}
