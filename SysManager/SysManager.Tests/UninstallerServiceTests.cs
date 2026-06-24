// SysManager · UninstallerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="UninstallerService"/>. Focuses on the table parser
/// since winget calls are integration-level.
/// </summary>
public class UninstallerServiceTests
{
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
    public void ValidateTrustedBinaryArgs_Rundll32_DllUnderWindows_IsAllowed()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dll = System.IO.Path.Combine(sys, "shell32.dll");
        var ex = Record.Exception(() =>
            UninstallerService.ValidateTrustedBinaryArgs("rundll32.exe", $"\"{dll}\",Control_RunDLL", isElevated: false));
        Assert.Null(ex);
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
        var changed = new List<string>();
        app.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

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

    // ── EnrichFromRegistry ──

    [Fact]
    public void EnrichFromRegistry_EmptyList_DoesNotThrow()
    {
        var ex = Record.Exception(() => UninstallerService.EnrichFromRegistry(new List<InstalledApp>()));
        Assert.Null(ex);
    }

    [Fact]
    public void EnrichFromRegistry_WithApps_DoesNotThrow()
    {
        var apps = new List<InstalledApp>
        {
            new() { Name = "NonExistentApp12345", Id = "test.id" }
        };
        var ex = Record.Exception(() => UninstallerService.EnrichFromRegistry(apps));
        Assert.Null(ex);
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
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "MsiExec.exe /I{12345-GUID}");
        Assert.Equal("MsiExec.exe", exe);
        Assert.Contains("/X{12345-GUID}", args);
        Assert.Contains("/quiet", args);
    }

    [Fact]
    public void ParseUninstallCommand_MsiExecAlreadyQuiet_DoesNotDuplicate()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "MsiExec.exe /X{GUID} /quiet");
        Assert.Equal("MsiExec.exe", exe);
        Assert.Contains("/X{GUID}", args);
        // Should not add /quiet again
        Assert.Equal(1, args.Split("/quiet").Length - 1);
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
    public void ParseUninstallCommand_MsiExecWithQn_DoesNotAddQuiet()
    {
        var (exe, args) = UninstallerService.ParseUninstallCommand(
            "MsiExec.exe /X{GUID} /qn");
        Assert.Equal("MsiExec.exe", exe);
        Assert.DoesNotContain("/quiet", args);
        Assert.Contains("/qn", args);
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
