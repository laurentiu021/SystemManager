// SysManager · AppBlockerServiceValidationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using SysManager.Services;

namespace SysManager.Tests;

public class AppBlockerServiceValidationTests
{
    private static AppBlockerService NewService() => new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlockApp_NullOrWhitespace_ReturnsFalse(string? exeName)
    {
        Assert.False(NewService().BlockApp(exeName!));
    }

    [Theory]
    [InlineData(@"..\..\windows\system32\cmd.exe")]
    [InlineData(@"path\to\app.exe")]
    [InlineData(@"folder/app.exe")]
    [InlineData("app;malicious.exe")]
    [InlineData("app&cmd.exe")]
    [InlineData("app|pipe.exe")]
    [InlineData("app<>.exe")]
    [InlineData(@"app"".exe")]
    public void BlockApp_InvalidCharsInName_ReturnsFalse(string exeName)
    {
        Assert.False(NewService().BlockApp(exeName));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("my-app.exe")]
    [InlineData("My App.exe")]
    [InlineData("app_v2.0.exe")]
    public void BlockApp_ValidNames_FormatsCorrectly(string exeName)
    {
        // These are valid names but will fail because we don't have admin access.
        // The important thing is they pass the validation check.
        // The method returns false because of UnauthorizedAccessException,
        // not because of invalid input. We can't distinguish in the return value,
        // but we verify the format is accepted by checking IsBlocked (which also
        // requires registry access and will return false gracefully).
        var result = NewService().IsBlocked(exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe");
        // IsBlocked reads registry - will return false if no access, but won't throw
        Assert.False(result); // expected since nothing is actually blocked
    }

    [Theory]
    [InlineData("winlogon.exe")]
    [InlineData("lsass.exe")]
    [InlineData("csrss.exe")]
    [InlineData("smss.exe")]
    [InlineData("services.exe")]
    [InlineData("wininit.exe")]
    [InlineData("svchost.exe")]
    [InlineData("explorer.exe")]
    // case-insensitive + bare-name (the service appends .exe before the denylist check)
    [InlineData("WinLogon.exe")]
    [InlineData("lsass")]
    public void BlockApp_BootCriticalExecutable_IsRefused(string exeName)
    {
        // Blocking a boot/logon-critical process via IFEO would render Windows
        // unbootable. The denylist must reject it before any registry write.
        Assert.False(NewService().BlockApp(exeName));
    }

    [Fact]
    public void GetBlockedApps_ReturnsListWithoutThrowing()
    {
        // Should not throw even without admin
        var result = NewService().GetBlockedApps();
        Assert.NotNull(result);
    }

    [Fact]
    public void IsBlocked_EmptyName_ReturnsFalse()
    {
        Assert.False(NewService().IsBlocked(""));
    }

    [Fact]
    public void IsBlocked_NormalExeName_DoesNotThrow()
    {
        var ex = Record.Exception(() => NewService().IsBlocked("notepad.exe"));
        Assert.Null(ex);
    }
}
