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
    public void IsInSystemFolder_BlankPath_IsTrue()
    {
        Assert.True(ProcessCriticality.IsInSystemFolder(null));
        Assert.True(ProcessCriticality.IsInSystemFolder(""));
        Assert.True(ProcessCriticality.IsInSystemFolder("   "));
    }

    [Fact]
    public void IsInSystemFolder_DefinitePathOutsideWindows_IsFalse()
        => Assert.False(ProcessCriticality.IsInSystemFolder(@"C:\Users\someone\AppData\Local\thing.exe"));

    [Fact]
    public void IsInSystemFolder_AFolderBelowTheSystemFolder_IsFalse()
        => Assert.False(ProcessCriticality.IsInSystemFolder(
            Path.Combine(Environment.SystemDirectory, "Tasks", "svchost.exe")));

    /// <summary>
    /// The refusal fails toward the name for a path it cannot place, and only a definite drive or network path outside
    /// the System folder lifts it. A drive path written as a device path names the same file and is judged as one.
    /// </summary>
    [Theory]
    [InlineData(@"svchost.exe", true)]
    [InlineData(@"System32\svchost.exe", true)]
    [InlineData(@"\\.\pipe\svchost", true)]
    [InlineData(@"\\?\UNC\server\share\svchost.exe", true)]
    [InlineData(@"\\?\{SYS}\svchost.exe", true)]
    [InlineData(@"\??\{SYS}\svchost.exe", true)]
    [InlineData(@"\\?\{WIN}\Temp\svchost.exe", false)]
    [InlineData(@"\??\{WIN}\Temp\svchost.exe", false)]
    [InlineData(@"\\server\share\svchost.exe", false)]
    [InlineData(@"{SYS}\..\Temp\svchost.exe", false)]
    [InlineData(@"{WIN}\Temp\..\System32\svchost.exe", true)]
    public void IsInSystemFolder_KeepsTheRefusalForWhatItCannotPlace(string spelled, bool expected)
        => Assert.Equal(expected, ProcessCriticality.IsInSystemFolder(Spelled(spelled)));

    /// <summary>
    /// The label fails the other way for those: never vouching for what it cannot place, while a drive path written as
    /// a
    /// device path is still judged as the drive path it is.
    /// </summary>
    [Theory]
    [InlineData(@"svchost.exe", false)]
    [InlineData(@"\\.\pipe\svchost", false)]
    [InlineData(@"\??\{SYS}\conhost.exe", true)]
    [InlineData(@"\\?\{SYS}\conhost.exe", true)]
    [InlineData(@"\??\{WIN}\Temp\conhost.exe", false)]
    [InlineData(@"\\server\share\conhost.exe", false)]
    public void IsWhereWindowsKeepsItsPrograms_NeverVouchesForWhatItCannotPlace(string spelled, bool expected)
        => Assert.Equal(expected, ProcessCriticality.IsWhereWindowsKeepsItsPrograms(Spelled(spelled)));

    /// <summary><paramref name="spelled"/> with {SYS} and {WIN} replaced by this PC's System and Windows
    /// folders.</summary>
    private static string Spelled(string spelled) => spelled
        .Replace("{SYS}", Environment.SystemDirectory, StringComparison.Ordinal)
        .Replace("{WIN}", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.Ordinal);

    /// <summary>
    /// Every Windows entry of the description database runs from a folder the label accepts, where Windows 11 keeps it.
    /// A Windows entry added later for a program in a folder not on the list fails here rather than reading "Not
    /// recognised" for the real program.
    /// </summary>
    [Fact]
    public void EveryWindowsEntryOfTheDatabase_RunsFromAFolderTheLabelAccepts()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var apps = Path.Combine(programFiles, "WindowsApps");
        string App(string package, string file) => Path.Combine(apps, package, file);
        string SystemApp(string package, string file) => Path.Combine(windows, "SystemApps", package, file);
        // Processes with no file of their own: the kernel's, which Windows lists without a path.
        string[] noFile = ["Idle", "Registry", "System"];
        var where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["explorer"] = Path.Combine(windows, "explorer.exe"),
            ["regedit"] = Path.Combine(windows, "regedit.exe"),
            ["notepad"] = Path.Combine(windows, "notepad.exe"),
            ["WmiPrvSE"] = Path.Combine(system, "wbem", "WmiPrvSE.exe"),
            ["powershell"] = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["TrustedInstaller"] = Path.Combine(windows, "servicing", "TrustedInstaller.exe"),
            ["SystemSettings"] = Path.Combine(windows, "ImmersiveControlPanel", "SystemSettings.exe"),
            ["LockApp"] = SystemApp("Microsoft.LockApp_cw5n1h2txyewy", "LockApp.exe"),
            ["SearchHost"] = SystemApp("MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "SearchHost.exe"),
            ["TextInputHost"] = SystemApp("MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "TextInputHost.exe"),
            ["ShellExperienceHost"] = SystemApp("ShellExperienceHost_cw5n1h2txyewy", "ShellExperienceHost.exe"),
            ["StartMenuExperienceHost"] = SystemApp("Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy",
                "StartMenuExperienceHost.exe"),
            ["MsMpEng"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows Defender\Platform\4.18.26100.4-0\MsMpEng.exe"),
            ["NisSrv"] = Path.Combine(programFiles, "Windows Defender", "NisSrv.exe"),
            ["wmplayer"] = Path.Combine(programFiles, "Windows Media Player", "wmplayer.exe"),
            ["GameBar"] = App("Microsoft.XboxGamingOverlay_7.325.10141.0_x64__8wekyb3d8bbwe", "GameBar.exe"),
            ["PhoneExperienceHost"] = App("Microsoft.YourPhone_1.25092.95.0_x64__8wekyb3d8bbwe",
                "PhoneExperienceHost.exe"),
            ["SnippingTool"] = App("Microsoft.ScreenSketch_11.2508.29.0_x64__8wekyb3d8bbwe",
                @"SnippingTool\SnippingTool.exe"),
            ["WidgetService"] = App("Microsoft.WidgetsPlatformRuntime_1.6.13.0_x64__8wekyb3d8bbwe",
                @"WidgetService\WidgetService.exe"),
            ["Widgets"] = App("MicrosoftWindows.Client.WebExperience_526.21100.40.0_x64__cw5n1h2txyewy",
                @"Dashboard\Widgets.exe"),
            ["WindowsTerminal"] = App("Microsoft.WindowsTerminal_1.25.2733.0_x64__8wekyb3d8bbwe",
                "WindowsTerminal.exe"),
            ["mspaint"] = App("Microsoft.Paint_11.2508.361.0_x64__8wekyb3d8bbwe", @"PaintApp\mspaint.exe"),
            ["wsl"] = Path.Combine(programFiles, "WSL", "wsl.exe"),
        };

        // Read from the database file itself, so an entry added to it is checked without being listed here.
        using var database = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(TestPaths.AppFile("Data", "ProcessDescriptions.json")));
        var windowsEntries = database.RootElement.EnumerateArray()
            .Where(e => string.Equals(e.GetProperty("safety").GetString(), "System",
                StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
        Assert.True(windowsEntries.Count >= 59,
            $"only {windowsEntries.Count} Windows entries were read from the database, and 59 were counted");

        var refused = windowsEntries
            .Where(name => !noFile.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(name => (Name: name, Path: where.TryGetValue(name, out var at) ? at : Path.Combine(system,
                name + ".exe")))
            .Where(entry => !ProcessCriticality.IsWhereWindowsKeepsItsPrograms(entry.Path))
            .Select(entry => $"{entry.Name}: {entry.Path}")
            .ToList();
        Assert.True(refused.Count == 0,
            "these Windows entries of the database run from a folder the label does not accept:\n  "
            + string.Join("\n  ", refused));
    }

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
