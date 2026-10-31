// SysManager · UninstallerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerService"/>. Focuses on the table parser
/// since winget calls are integration-level.
/// </summary>
[Collection("ProcessEnvironment")]
public class UninstallerServiceTests
{
    [Fact]
    public async Task UninstallAsync_WhenElevated_RejectsBeforeWingetLaunch()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new UninstallerService(runner, () => true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UninstallAsync("Vendor.Package"));

        Assert.Contains("running as administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessAsync(default!, default!, default, default);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessWithShellAsync(default!, default!, default);
    }

    [Fact]
    public async Task UninstallLocalAsync_WhenElevated_RejectsWindowsCommandBeforeLaunch()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new UninstallerService(runner, () => true);
        var app = new InstalledApp
        {
            Name = "Forged registration",
            UninstallString =
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UninstallLocalAsync(app));

        Assert.Contains("running as administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessAsync(default!, default!, default, default);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessWithShellAsync(default!, default!, default);
    }

    [Fact]
    public async Task UninstallAsync_WhenNotElevated_UsesWingetRunner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                "winget",
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        var service = new UninstallerService(runner, () => false);

        var exitCode = await service.UninstallAsync("Vendor.Package");

        Assert.Equal(0, exitCode);
        await runner.Received(1).RunProcessAsync(
            "winget",
            Arg.Is<string>(args => args != null && args.Contains("Vendor.Package", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// A LOCAL id — the kind winget prints for an app no source matches — reaches winget instead of being
    /// refused before the process starts.
    /// </summary>
    /// <remarks>
    /// The assertion that matters, and the one whose absence let a reverted fix pass: testing
    /// <c>WingetId.IsLocalUninstallId</c> on its own proves the RULE and says nothing about whether
    /// <c>UninstallAsync</c> consults it. On the machine this was derived from, 103 of 182 installed apps had
    /// an id of this shape, every one of which could be ticked, confirmed, and then not removed (#2849).
    /// </remarks>
    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"ARP\Machine\X64\{90160000-008C-0000-1000-0000000FF1CE}")]
    [InlineData(@"ARP\User\X64\SomeRegistryKey")]
    public async Task UninstallAsync_PassesAWingetLocalIdThroughToWinget(string localId)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        var service = new UninstallerService(runner, () => false);

        var exitCode = await service.UninstallAsync(localId);

        Assert.Equal(0, exitCode);
        await runner.Received(1).RunProcessAsync(
            "winget",
            // Quoted and whole: the id carries backslashes, so a command line that lost the quotes or
            // truncated at the first separator would be a different package — or none.
            Arg.Is<string>(args => args != null
                && args.Contains($"--id \"{localId}\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// An id that is neither a source id nor a local one is still refused before winget starts, with a
    /// sentence a person can act on rather than the parameter name.
    /// </summary>
    [Theory]
    [InlineData(@"MSIX\pkg""; calc.exe")]
    [InlineData(@"ARP\Machine\X64\key\")]
    [InlineData("pkg && calc")]
    public async Task UninstallAsync_RefusesAnIdItCannotPassSafely(string badId)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new UninstallerService(runner, () => false);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.UninstallAsync(badId));

        Assert.Contains("Settings", error.Message, StringComparison.Ordinal);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task UninstallLocalAsync_WhenNotElevated_UsesShellRunnerForOwnedUac()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessWithShellAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(0);
        var service = new UninstallerService(runner, () => false);
        var executable = System.IO.Path.Combine(Environment.SystemDirectory, "where.exe");
        var app = new InstalledApp
        {
            Name = "Local app",
            UninstallString = $"\"{executable}\" cmd.exe"
        };

        var exitCode = await service.UninstallLocalAsync(app);

        Assert.Equal(0, exitCode);
        await runner.Received(1).RunProcessWithShellAsync(
            executable,
            "cmd.exe",
            Arg.Any<CancellationToken>());
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task UninstallLocalAsync_RelativeExecutable_RejectsBeforeShellLaunch()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new UninstallerService(runner, () => false);
        var app = new InstalledApp
        {
            Name = "Relative registration",
            UninstallString = @".\where.exe cmd.exe"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UninstallLocalAsync(app));

        Assert.Contains("not absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessWithShellAsync(default!, default!, default);
    }

    // ── An MSI uninstall can ask for administrator rights (#2724) ──
    // It ran under /quiet, where Windows Installer shows no window and so cannot ask, and SysManager uninstalls only
    // without them: an app installed for all users failed with 1603.

    private const string ProductCode = "{0F2C3A4B-1234-5678-9ABC-DEF012345678}";

    [Theory]
    [InlineData("MsiExec.exe /I" + ProductCode, "", "/X" + ProductCode + " /passive /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode, "", "/X" + ProductCode + " /passive /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode, "MsiExec.exe /X" + ProductCode + " /qn",
                "/X" + ProductCode + " /passive /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode + " /quiet /norestart", "", "/X" + ProductCode + " /passive /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode + " /QN /Quiet /promptrestart", "",
                "/X" + ProductCode + " /passive /promptrestart")]
    [InlineData("MsiExec.exe /x " + ProductCode + " /qn", "", "/x " + ProductCode + " /passive /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode + " /qb", "", "/X" + ProductCode + " /qb /norestart")]
    [InlineData("MsiExec.exe /X" + ProductCode + " /qn /qf", "", "/X" + ProductCode + " /qf /norestart")]
    public async Task UninstallLocalAsync_AnMsiUninstall_RunsWithAWindow_SoItCanAskForAdministratorRights(
        string uninstallString, string quietUninstallString, string expected)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var service = new UninstallerService(runner, () => false);
        var app = new InstalledApp
        {
            Name = "OEM utility",
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstallString,
        };

        await service.UninstallLocalAsync(app);

        await runner.Received(1).RunProcessWithShellAsync(
            System.IO.Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "MsiExec.exe"),
            expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallLocalAsync_AQuotedMsiExecPath_RunsWithAWindowToo()
    {
        // A quoted path is split before the MsiExec form is recognised, so its switches reached the launch as given.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessWithShellAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(0);
        var service = new UninstallerService(runner, () => false);
        var msiExec = System.IO.Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "MsiExec.exe");
        var app = new InstalledApp
        {
            Name = "OEM utility",
            UninstallString = "MsiExec.exe /X" + ProductCode,
            QuietUninstallString = $"\"{msiExec}\" /X{ProductCode} /quiet",
        };

        await service.UninstallLocalAsync(app);

        await runner.Received(1).RunProcessWithShellAsync(msiExec, "/X" + ProductCode + " /passive /norestart",
                                                          Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/X" + ProductCode + " /quiet")]
    [InlineData("/X" + ProductCode + " /qn /norestart")]
    [InlineData("/X" + ProductCode + " /QUIET /QN")]
    [InlineData("/x " + ProductCode)]
    public void WithInstallerUi_NeverLeavesASilentSwitch_AndStillPassesTheCheck(string args)
    {
        var ui = UninstallerService.WithInstallerUi(args);

        var switches = ui.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(switches, s => s.Equals("/quiet", StringComparison.OrdinalIgnoreCase)
                                          || s.Equals("/qn", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/passive", switches);
        Assert.Equal(ui, UninstallerService.ValidateTrustedBinaryArgs("MsiExec.exe", ui, isElevated: false));
    }

    // ── IsUnderTrustedDirectory (regression: prefix-boundary bypass) ──

    [Fact]
    public void IsUnderTrustedDirectory_PathInsideProgramFiles_IsTrusted()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        // Program Files is admin-protected, so it is trusted regardless of elevation.
        Assert.True(UninstallerService.IsUnderTrustedDirectory(System.IO.Path.Combine(pf, "Vendor", "app.exe"), isElevated: false));
        Assert.True(UninstallerService.IsUnderTrustedDirectory(System.IO.Path.Combine(pf, "Vendor", "app.exe"), isElevated: true));
    }

    [Fact]
    public void IsUnderTrustedDirectory_SiblingWithSharedPrefix_IsNotTrusted()
    {
        // "C:\Program Files Evil\..." must NOT pass the "C:\Program Files" check.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var evil = pf + " Evil\\malware.exe";
        Assert.False(UninstallerService.IsUnderTrustedDirectory(evil, isElevated: false));
    }

    [Fact]
    public void IsUnderTrustedDirectory_UntrustedLocation_IsNotTrusted()
        => Assert.False(UninstallerService.IsUnderTrustedDirectory(@"C:\Temp\random\app.exe", isElevated: false));

    [Fact]
    public void IsUnderTrustedDirectory_ProgramData_IsTrustedOnlyAtStandardIntegrity()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var executable = System.IO.Path.Combine(programData, "Vendor", "uninstall.exe");

        Assert.True(UninstallerService.IsUnderTrustedDirectory(executable, isElevated: false));
        Assert.False(UninstallerService.IsUnderTrustedDirectory(executable, isElevated: true));
    }

    [Fact]
    public void IsUnderTrustedDirectory_ForgedProgramDataVariable_IsNotTrustedWhenElevated()
    {
        var original = Environment.GetEnvironmentVariable("ProgramData");
        var forgedRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SysManagerProgramDataTest_Forged");
        try
        {
            Environment.SetEnvironmentVariable("ProgramData", forgedRoot);
            var plantedBinary = System.IO.Path.Combine(forgedRoot, "Vendor", "uninstall.exe");

            Assert.False(UninstallerService.IsUnderTrustedDirectory(plantedBinary, isElevated: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ProgramData", original);
        }
    }

    // ── SEC-LPE: user-writable per-user location is trusted only when NOT elevated ──

    [Fact]
    public void IsUnderTrustedDirectory_LocalAppData_TrustedWhenNotElevated()
    {
        // A per-user install (VS Code, Discord) lives under %LocalAppData% and must
        // still uninstall normally when SysManager runs without elevation.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perUserApp = System.IO.Path.Combine(localAppData, "Programs", "VendorApp", "uninstall.exe");
        Assert.True(UninstallerService.IsUnderTrustedDirectory(perUserApp, isElevated: false));
    }

    [Fact]
    public void IsUnderTrustedDirectory_LocalAppData_NotTrustedWhenElevated()
    {
        // %LocalAppData% is writable by a standard user. When SysManager is elevated,
        // executing a binary there would let an unprivileged attacker who planted it
        // run with our elevation (local privilege escalation) — so it must NOT be trusted.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var plantedBinary = System.IO.Path.Combine(localAppData, "Programs", "VendorApp", "uninstall.exe");
        Assert.False(UninstallerService.IsUnderTrustedDirectory(plantedBinary, isElevated: true));
    }

    // ── ValidateTrustedBinaryArgs (regression: LPE via HKCU UninstallString) ──

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_DllOutsideTrustedDir_Throws()
    {
        // rundll32 would load an attacker-controlled DLL from a writable temp path
        // with our elevation — must be rejected.
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("rundll32.exe", @"C:\Temp\evil.dll,EntryPoint", isElevated: false));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_DllUnderWindows_IsCanonicalized()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dll = System.IO.Path.Combine(sys, "shell32.dll");

        var args = UninstallerService.ValidateTrustedBinaryArgs(
            "rundll32.exe", $"\"{dll}\",Control_RunDLL", isElevated: false);

        Assert.Equal($"\"{dll}\",Control_RunDLL", args);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_BareSystemDll_ResolvesToSystem32()
    {
        var dll = System.IO.Path.Combine(Environment.SystemDirectory, "shell32.dll");

        var args = UninstallerService.ValidateTrustedBinaryArgs(
            "rundll32.exe", "shell32.dll,Control_RunDLL", isElevated: false);

        Assert.Equal($"\"{dll}\",Control_RunDLL", args);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_DllInLocalAppData_NotTrustedWhenElevated_Throws()
    {
        // SEC-LPE: a rundll32 DLL planted in user-writable %LocalAppData% must be
        // rejected when elevated, even though it would be allowed un-elevated.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dll = System.IO.Path.Combine(localAppData, "evil.dll");
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("rundll32.exe", $"\"{dll}\",EntryPoint", isElevated: true));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_RelativeDll_Throws()
    {
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs(
                "rundll32.exe", @"subdir\shell32.dll,Control_RunDLL", isElevated: false));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_DriveRelativeDll_ThrowsAsNonAbsolute()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            UninstallerService.ValidateTrustedBinaryArgs(
                "rundll32.exe", @"C:shell32.dll,Control_RunDLL", isElevated: false));

        Assert.Contains("not absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_Rundll32_NoDllPath_Throws()
    {
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("rundll32.exe", "   ", isElevated: false));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void ValidateTrustedBinaryArgs_MsiExec_ProductCodeUninstall_IsAllowed()
    {
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs(
                "MsiExec.exe", "/X{0F2C3A4B-1234-5678-9ABC-DEF012345678} /quiet /norestart", isElevated: false));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(@"C:\Temp\evil.msi /quiet")]            // arbitrary package path, not /X{GUID}
    [InlineData(@"/I{0F2C3A4B-1234-5678-9ABC-DEF012345678}")] // install, not uninstall
    [InlineData(@"/X notaguid")]                         // /X without a product code
    public void ValidateTrustedBinaryArgs_MsiExec_NonProductCode_Throws(string args)
    {
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("MsiExec.exe", args, isElevated: false));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Theory]
    [InlineData(@"/X{0F2C3A4B-1234-5678-9ABC-DEF012345678} C:\Temp\evil.msi")]
    [InlineData(@"/X{0F2C3A4B-1234-5678-9ABC-DEF012345678} /quiet TRANSFORMS=C:\Temp\evil.mst")]
    public void ValidateTrustedBinaryArgs_MsiExec_TrailingPayload_Throws(string args)
    {
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("MsiExec.exe", args, isElevated: true));

        Assert.IsType<InvalidOperationException>(ex);
    }

    // ── ParseListTable ──

    [Fact]
    public void ParseListTable_EmptyInput_ReturnsEmpty()
    {
        var result = UninstallerService.ParseListTable(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseListTable_NoHeader_ReturnsEmpty()
    {
        var lines = new List<string> { "some random text", "another line" };
        var result = UninstallerService.ParseListTable(lines);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseListTable_ValidTable_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name                         Id                           Version    Source",
            "-------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode   1.90.0     winget",
            "Git                          Git.Git                      2.45.1     winget",
            "2 packages installed."
        };

        var result = UninstallerService.ParseListTable(lines);

        Assert.Equal(2, result.Count);
        Assert.Equal("Visual Studio Code", result[0].Name);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("1.90.0", result[0].Version);
        Assert.Equal("winget", result[0].Source);
        Assert.Equal("Git", result[1].Name);
        Assert.Equal("Git.Git", result[1].Id);
    }

    [Fact]
    public void ParseListTable_WithAvailableColumn_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Available  Source",
            "----------------------------------------------------------------",
            "Node.js           OpenJS.NodeJS       20.11.0   20.12.0    winget",
        };

        var result = UninstallerService.ParseListTable(lines);

        Assert.Single(result);
        Assert.Equal("Node.js", result[0].Name);
        Assert.Equal("OpenJS.NodeJS", result[0].Id);
        Assert.Equal("20.11.0", result[0].Version);
    }

    [Fact]
    public void ParseListTable_SkipsSeparatorLines()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "--some separator--",
            "App               Some.App            1.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Single(result);
        Assert.Equal("Some.App", result[0].Id);
    }

    [Fact]
    public void ParseListTable_StopsAtSummaryLine()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "App1              Some.App1            1.0       winget",
            "App2              Some.App2            2.0       winget",
            "5 packages installed.",
            "App3              Some.App3            3.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseListTable_SkipsShortLines()
    {
        var lines = new List<string>
        {
            "Name              Id                  Version   Source",
            "------------------------------------------------------",
            "X",
            "App               Some.App            1.0       winget",
        };

        var result = UninstallerService.ParseListTable(lines);
        Assert.Single(result);
    }

    // ── InstalledApp model ──

    [Fact]
    public void InstalledApp_DefaultValues()
    {
        var app = new InstalledApp();
        Assert.False(app.IsSelected);
        Assert.Equal("", app.Name);
        Assert.Equal("", app.Id);
        Assert.Equal("", app.Version);
        Assert.Equal("", app.Source);
        Assert.Equal("", app.Status);
    }

    [Fact]
    public void InstalledApp_PropertyChange_Notifies()
    {
        var app = new InstalledApp();
        var changed = app.RecordPropertyChanges();

        app.IsSelected = true;
        app.Name = "Test";
        app.Id = "test.id";
        app.Version = "1.0";
        app.Status = "Removed";

        Assert.Contains("IsSelected", changed);
        Assert.Contains("Name", changed);
        Assert.Contains("Id", changed);
        Assert.Contains("Version", changed);
        Assert.Contains("Status", changed);
    }

    // ── InstalledApp size display ──

    [Fact]
    public void InstalledApp_SizeDisplay_ZeroBytes_ReturnsDash()
    {
        var app = new InstalledApp { SizeBytes = 0 };
        Assert.Equal("—", app.SizeDisplay);
    }

    [Fact]
    public void InstalledApp_SizeDisplay_MB()
    {
        var app = new InstalledApp { SizeBytes = 150 * 1024 * 1024L };
        Assert.Contains("MB", app.SizeDisplay);
    }

    [Fact]
    public void InstalledApp_SizeDisplay_GB()
    {
        var app = new InstalledApp { SizeBytes = 2L * 1024 * 1024 * 1024 };
        Assert.Contains("GB", app.SizeDisplay);
    }

    [Fact]
    public void InstalledApp_Publisher_DefaultEmpty()
    {
        var app = new InstalledApp();
        Assert.Equal("", app.Publisher);
    }

    [Fact]
    public void InstalledApp_SizeBytes_DefaultZero()
    {
        var app = new InstalledApp();
        Assert.Equal(0, app.SizeBytes);
    }

    // ── ParseUninstallCommand ──

    [Fact]
    public void ParseUninstallCommand_QuotedPath_SplitsCorrectly()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "\"C:\\Program Files\\App\\uninstall.exe\" /S");
        Assert.Equal("C:\\Program Files\\App\\uninstall.exe", exe);
        Assert.Equal("/S", args);
    }

    [Fact]
    public void ParseUninstallCommand_QuotedPathNoArgs_ReturnsEmptyArgs()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "\"C:\\Program Files\\App\\uninstall.exe\"");
        Assert.Equal("C:\\Program Files\\App\\uninstall.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void ParseUninstallCommand_MsiExec_ConvertsToUninstall()
    {
        // The switches it runs with are set at launch, by WithInstallerUi (#2724).
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "MsiExec.exe /I{12345-GUID}");
        Assert.Equal("MsiExec.exe", exe);
        Assert.Equal("/X{12345-GUID}", args);
    }

    [Fact]
    public void ParseUninstallCommand_Rundll32_PassesAsIs()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "rundll32.exe advpack.dll,LaunchINFSection something.inf");
        Assert.Equal("rundll32.exe", exe);
        Assert.Equal("advpack.dll,LaunchINFSection something.inf", args);
    }

    [Fact]
    public void ParseUninstallCommand_UnquotedExePath_FindsExeBoundary()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "C:\\Apps\\uninstall.exe --silent");
        Assert.Equal("C:\\Apps\\uninstall.exe", exe);
        Assert.Equal("--silent", args);
    }

    [Fact]
    public void ParseUninstallCommand_NoExeExtension_ThrowsInvalidOperation()
    {
        // SEC-M7: Commands without a valid .exe boundary are rejected for security.
        Assert.Throws<InvalidOperationException>(
            () => UninstallerService.ParseUninstallCommand("some-command"));
    }

    [Fact]
    public void ParseUninstallCommand_MsiExec_KeepsTheSwitchesItWasGiven()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "MsiExec.exe /X{GUID} /qn");
        Assert.Equal("MsiExec.exe", exe);
        Assert.Equal("/X{GUID} /qn", args);
    }

    [Theory]
    [InlineData("cmd.exe /c calc.exe | evil.exe")]
    [InlineData("uninstall.exe & del /q *")]
    [InlineData("app.exe; rm -rf /")]
    [InlineData("tool.exe `whoami`")]
    [InlineData("app.exe $(malicious)")]
    public void ParseUninstallCommand_ShellMetacharacters_Throws(string command)
    {
        // SEC-M7: Shell metacharacters are rejected to prevent command injection.
        Assert.Throws<InvalidOperationException>(
            () => UninstallerService.ParseUninstallCommand(command));
    }

    [Fact]
    public void ParseUninstallCommand_ExeInMiddleOfPath_FindsCorrectBoundary()
    {
        // SEC-M7: ".exe" followed by non-boundary char should not split there.
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            @"C:\dir\app.exefiles\tool.exe /silent");
        Assert.Equal(@"C:\dir\app.exefiles\tool.exe", exe);
        Assert.Equal("/silent", args);
    }
}
