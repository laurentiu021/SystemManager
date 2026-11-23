// SysManager · StartupTaskProgramTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.Principal;
using System.Xml.Linq;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// What a scheduled task's row runs (<see cref="StartupService.TaskProgram"/>), and what "Hide system" counts as
/// part of Windows (<see cref="StartupViewModel.IsWindowsEntry"/>, <see cref="SignatureVerdict.IsMicrosoft"/>).
/// </summary>
public sealed class StartupTaskProgramTests
{
    private static XElement Task(string actions, string principals = "") => XElement.Parse(
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        + principals + "<Actions>" + actions + "</Actions></Task>");

    private static string Principal(string userId) =>
        $"<Principals><Principal><UserId>{userId}</UserId></Principal></Principals>";

    [Theory]
    [InlineData("<Exec><Command>C:\\Tools\\app.exe</Command></Exec>", "C:\\Tools\\app.exe")]
    [InlineData("<Exec><Command>C:\\Tools\\app.exe</Command><Arguments>--run now</Arguments></Exec>",
        "C:\\Tools\\app.exe --run now")]
    [InlineData("<Exec><Command>\"C:\\Program Files\\App\\app.exe\"</Command></Exec>",
        "\"C:\\Program Files\\App\\app.exe\"")]
    [InlineData("<Exec><Command>C:\\Program Files\\App\\app.exe</Command><Arguments>/s</Arguments></Exec>",
        "\"C:\\Program Files\\App\\app.exe\" /s")]
    // The first program action, not a later one.
    [InlineData("<Exec><Command>C:\\First\\a.exe</Command></Exec><Exec><Command>C:\\Second\\b.exe</Command></Exec>",
        "C:\\First\\a.exe")]
    // A program with a folder but not a full path runs from the action's working folder.
    [InlineData("<Exec><Command>bin\\app.exe</Command><WorkingDirectory>C:\\Vendor</WorkingDirectory></Exec>",
        "C:\\Vendor\\bin\\app.exe")]
    // A bare name is left as it is, for the lookup in Windows' own folders.
    [InlineData("<Exec><Command>app.exe</Command><WorkingDirectory>C:\\Vendor</WorkingDirectory></Exec>", "app.exe")]
    // A task that runs no program.
    [InlineData("<ComHandler><ClassId>{00000000-0000-0000-0000-000000000001}</ClassId></ComHandler>", "")]
    [InlineData("<Exec><Command>  </Command></Exec>", "")]
    public void TaskProgram_IsTheFirstProgramActionsCommandLine(string actions, string expected)
        => Assert.Equal(expected, StartupService.TaskProgram(Task(actions)));

    [Fact]
    public void TaskProgram_ExpandsAMachineWideVariable_WhoeverTheTaskRunsAs()
    {
        var program = StartupService.TaskProgram(Task(
            "<Exec><Command>%ProgramFiles%\\Vendor\\app.exe</Command></Exec>", Principal("S-1-5-18")));

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor",
            "app.exe"), program, ignoreCase: true);
    }

    /// <summary>
    /// A variable that differs between accounts would name this account's folder for a task another account runs, so
    /// that task's program is not read at all rather than read wrongly.
    /// </summary>
    [Fact]
    public void TaskProgram_WithAnAccountsOwnVariable_RunAsAnotherAccount_IsNotRead()
        => Assert.Equal("", StartupService.TaskProgram(Task(
            "<Exec><Command>%LOCALAPPDATA%\\Vendor\\app.exe</Command></Exec>", Principal("S-1-5-18"))));

    [Fact]
    public void TaskProgram_WithAnAccountsOwnVariable_RunAsThisAccount_IsRead()
    {
        using var me = WindowsIdentity.GetCurrent();

        var program = StartupService.TaskProgram(Task(
            "<Exec><Command>%LOCALAPPDATA%\\Vendor\\app.exe</Command></Exec>", Principal(me.User!.Value)));

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vendor",
            "app.exe"), program, ignoreCase: true);
    }

    [Theory]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("Microsoft Windows", true)]
    [InlineData("Microsoft Windows Publisher", true)]
    // Names Microsoft signs other companies' work with: they vouch for a file, not that Microsoft made it.
    [InlineData("Microsoft Windows Hardware Compatibility Publisher", false)]
    [InlineData("Microsoft 3rd Party Application Component", false)]
    [InlineData("Microsoft Corporation Ltd", false)]
    [InlineData("microsoft corporation", false)]
    [InlineData("", false)]
    public void IsMicrosoft_IsOnlyTheNamesMicrosoftSignsItsOwnProgramsWith(string signer, bool expected)
        => Assert.Equal(expected, SignatureVerdict.IsMicrosoft(signer));

    [Theory]
    [InlineData("System", SignatureTrust.Unknown, "", true)]
    [InlineData("", SignatureTrust.Verified, "Microsoft Corporation", true)]
    [InlineData("", SignatureTrust.Verified, "Google LLC", false)]
    [InlineData("", SignatureTrust.Verified, "Microsoft Windows Hardware Compatibility Publisher", false)]
    // A signer is only ever recorded for a signature Windows confirms, and one that is not confirmed never counts.
    [InlineData("", SignatureTrust.Invalid, "Microsoft Corporation", false)]
    [InlineData("Unknown", SignatureTrust.Unsigned, "", false)]
    [InlineData("Trusted", SignatureTrust.Unknown, "", false)]
    public void IsWindowsEntry_IsRecognisedAsWindows_OrSignedByMicrosoft(string safety, SignatureTrust signature,
        string signer, bool expected)
    {
        var entry = new StartupEntry
        {
            Name = "Entry",
            Command = @"C:\Windows\System32\rundll32.exe C:\Vendor\x.dll,Run",
            Publisher = "Microsoft Corporation",
            Safety = safety,
            Signature = signature,
            Signer = signer,
        };

        Assert.Equal(expected, StartupViewModel.IsWindowsEntry(entry));
    }
}
