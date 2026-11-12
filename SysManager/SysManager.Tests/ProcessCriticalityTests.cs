// SysManager · ProcessCriticalityTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProcessCriticality"/>: the name-plus-path checks Process Manager's kill
/// rules and the chip tooltip both key on (#2792).
/// </summary>
public class ProcessCriticalityTests
{
    private static string SystemRootExe(string bareName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", bareName + ".exe");

    private static string ProfileFolderExe(string bareName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), bareName + ".exe");

    [Theory]
    [InlineData("winlogon")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    public void IsBootCritical_GenuineImage_IsTrue(string bareName)
        => Assert.True(ProcessCriticality.IsBootCritical(bareName, SystemRootExe(bareName)));

    [Theory]
    [InlineData("winlogon")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    public void IsBootCritical_ProfileFolderImpersonator_IsFalse(string bareName)
        // A program merely NAMED svchost.exe running from a user's profile is not the real service
        // host — the name match alone used to let it inherit both the refusal and the tooltip.
        => Assert.False(ProcessCriticality.IsBootCritical(bareName, ProfileFolderExe(bareName)));

    [Fact]
    public void IsBootCritical_UnreadablePath_StillTrustsTheName()
        // Most real system processes cannot have their path read without elevation — an empty path is
        // what the overwhelming majority of genuine boot-critical processes look like from here.
        => Assert.True(ProcessCriticality.IsBootCritical("lsass", null));

    [Fact]
    public void IsBootCritical_UnlistedName_IsFalseRegardlessOfPath()
        => Assert.False(ProcessCriticality.IsBootCritical("notepad", SystemRootExe("notepad")));

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("TrustedInstaller")]
    public void IsHighConsequence_GenuineImage_IsTrue(string bareName)
        => Assert.True(ProcessCriticality.IsHighConsequence(bareName, SystemRootExe(bareName)));

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("TrustedInstaller")]
    public void IsHighConsequence_ProfileFolderImpersonator_IsFalse(string bareName)
        => Assert.False(ProcessCriticality.IsHighConsequence(bareName, ProfileFolderExe(bareName)));

    [Fact]
    public void IsHighConsequence_UnreadablePath_StillTrustsTheName()
        => Assert.True(ProcessCriticality.IsHighConsequence("MsMpEng", null));

    [Fact]
    public void IsUnderSystemRoot_BlankPath_IsTrue()
    {
        Assert.True(ProcessCriticality.IsUnderSystemRoot(null));
        Assert.True(ProcessCriticality.IsUnderSystemRoot(""));
        Assert.True(ProcessCriticality.IsUnderSystemRoot("   "));
    }

    [Fact]
    public void IsUnderSystemRoot_DefinitePathOutsideWindows_IsFalse()
        => Assert.False(ProcessCriticality.IsUnderSystemRoot(@"C:\Users\someone\AppData\Local\thing.exe"));

    // ---------- where Windows keeps its programs, beyond the System folder (#3166) ----------

    /// <summary>A path under one of this PC's special folders, so the cases hold wherever Windows is.</summary>
    private static string Under(Environment.SpecialFolder root, string relative)
        => Path.Combine(Environment.GetFolderPath(root), relative);

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows, @"System32\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows,
        @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe")]
    // The packages Windows 11 installs its own apps as: Microsoft Corporation's publisher ID, and Microsoft Windows'.
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsApps\Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsApps\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsApps\MicrosoftWindows.Client.WebExperience_526.21100.40.0_x64__cw5n1h2txyewy\Dashboard\Widgets.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86, @"Windows Media Player\wmplayer.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"Windows Media Player\wmplayer.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"Windows Defender\MsMpEng.exe")]
    [InlineData(Environment.SpecialFolder.CommonApplicationData,
        @"Microsoft\Windows Defender\Platform\4.18.26100.4-0\MsMpEng.exe")]
    // Each folder inside Windows a database program runs from, so none can be dropped from the list unnoticed.
    [InlineData(Environment.SpecialFolder.Windows, @"explorer.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"System32\wbem\WmiPrvSE.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"servicing\TrustedInstaller.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"ImmersiveControlPanel\SystemSettings.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"SystemApps\Microsoft.LockApp_cw5n1h2txyewy\LockApp.exe")]
    [InlineData(Environment.SpecialFolder.SystemX86, @"cmd.exe")]
    // WSL as the Store installs it.
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"WSL\wsl.exe")]
    public void IsWhereWindowsKeepsItsPrograms_WindowsOwnFolders_AreTrue(
        Environment.SpecialFolder root, string relative)
        => Assert.True(ProcessCriticality.IsWhereWindowsKeepsItsPrograms(Under(root, relative)));

    [Theory]
    [InlineData(Environment.SpecialFolder.ApplicationData, @"svchost.exe")]
    [InlineData(Environment.SpecialFolder.LocalApplicationData, @"Temp\notepad.exe")]
    // A package someone else publishes, even with a Windows program's name in it.
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsApps\SomeVendor.Notepad_1.0.0.0_x64__2247w0b46hfww\Notepad.exe")]
    // A Microsoft publisher ID inside a package name, and not at its end, is not Microsoft's package.
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsApps\Evil_8wekyb3d8bbwe_1.0.0.0_x64__2247w0b46hfww\svchost.exe")]
    // A file straight inside WindowsApps is in no package.
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"WindowsApps\svchost.exe")]
    // Folders whose names only begin like Windows' own.
    [InlineData(Environment.SpecialFolder.ProgramFiles,
        @"WindowsAppsX\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\Notepad.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"Windows Media Player Extra\wmplayer.exe")]
    // The folder of the same name in the user's own profile, which the user can write to.
    [InlineData(Environment.SpecialFolder.LocalApplicationData,
        @"Microsoft\WindowsApps\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\Notepad.exe")]
    // Defender's data, outside its platform folder.
    [InlineData(Environment.SpecialFolder.CommonApplicationData, @"Microsoft\Windows Defender\Scans\MsMpEng.exe")]
    // Folders inside Windows that any program can write to, which is not where Windows keeps its programs.
    [InlineData(Environment.SpecialFolder.Windows, @"Temp\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"Tasks\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"tracing\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"System32\Tasks\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"System32\spool\drivers\color\svchost.exe")]
    [InlineData(Environment.SpecialFolder.Windows, @"Registration\CRMLog\svchost.exe")]
    // WSL's name, in folders that are not the one the Store installs it to.
    [InlineData(Environment.SpecialFolder.LocalApplicationData, @"WSL\wsl.exe")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, @"WSLX\wsl.exe")]
    public void IsWhereWindowsKeepsItsPrograms_AnywhereElse_IsFalse(Environment.SpecialFolder root, string relative)
        => Assert.False(ProcessCriticality.IsWhereWindowsKeepsItsPrograms(Under(root, relative)));

    [Fact]
    public void IsWhereWindowsKeepsItsPrograms_UnreadablePath_StillTrustsTheName()
    {
        Assert.True(ProcessCriticality.IsWhereWindowsKeepsItsPrograms(null));
        Assert.True(ProcessCriticality.IsWhereWindowsKeepsItsPrograms(""));
        Assert.True(ProcessCriticality.IsWhereWindowsKeepsItsPrograms("   "));
    }

    [Fact]
    public void IsBootCritical_StillWantsTheSystemFolder_EvenInsideAMicrosoftPackage()
        // The names it keeps run from the System folder, so a copy anywhere else is not one, however protected.
        => Assert.False(ProcessCriticality.IsBootCritical("svchost", Under(Environment.SpecialFolder.ProgramFiles,
            @"WindowsApps\Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe\svchost.exe")));

    /// <summary>
    /// A boot-critical name running from a folder inside Windows that any program can write to is not the real one,
    /// so it does not borrow the refusal: only the System folder itself is where those run from.
    /// </summary>
    [Theory]
    [InlineData("svchost", @"Temp")]
    [InlineData("csrss", @"Tasks")]
    [InlineData("lsass", @"tracing")]
    [InlineData("dwm", @"System32\Tasks")]
    [InlineData("svchost", @"System32\spool\drivers\color")]
    public void IsBootCritical_ACopyInAFolderInsideWindowsAnyUserCanWriteTo_IsFalse(string bareName, string folder)
        => Assert.False(ProcessCriticality.IsBootCritical(bareName,
            Under(Environment.SpecialFolder.Windows, Path.Combine(folder, bareName + ".exe"))));

    /// <summary>Defender updates its engine into its platform folder, so the engine there is the real one.</summary>
    [Fact]
    public void IsHighConsequence_DefendersEngineInItsPlatformFolder_IsTrue()
        => Assert.True(ProcessCriticality.IsHighConsequence("MsMpEng",
            Under(Environment.SpecialFolder.CommonApplicationData,
                @"Microsoft\Windows Defender\Platform\4.18.26100.4-0\MsMpEng.exe")));

    [Fact]
    public void BootCriticalAndHighConsequence_DoNotOverlap()
    {
        // Checked in order by ProcessManagerViewModel, so a name in both sets would be refused and its
        // high-consequence warning never seen — the entry would look present while being dead.
        Assert.Empty(ProcessCriticality.BootCriticalProcesses.Intersect(
            ProcessCriticality.HighConsequenceProcesses, StringComparer.OrdinalIgnoreCase));
    }
}
