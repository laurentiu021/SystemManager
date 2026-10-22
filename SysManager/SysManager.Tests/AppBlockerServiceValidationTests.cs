// SysManager · AppBlockerServiceValidationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Which executable names <see cref="AppBlockerService"/> blocks and which it refuses.
/// </summary>
/// <remarks>
/// Every test that could write a block runs over a throwaway key under HKEY_CURRENT_USER, never the machine's Image
/// File Execution Options. A name expected to be refused would otherwise be written there as a real block the day the
/// check let it through, and CI runs elevated. Only the two reads at the end use the machine's own key.
/// </remarks>
public sealed class AppBlockerServiceValidationTests : IDisposable
{
    private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private readonly string _rootName =
        @"Software\SysManagerTests\AppBlockerValidation_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public AppBlockerServiceValidationTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _root.CreateSubKey(IfeoPath, writable: true)!.Dispose();
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private AppBlockerService NewService() => new(_root);

    /// <summary>The executables blocked under the throwaway key.</summary>
    private string[] Blocked()
    {
        using var ifeo = _root.OpenSubKey(IfeoPath)!;
        return ifeo.GetSubKeyNames();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlockApp_NullOrWhitespace_ReturnsFalse(string? exeName)
    {
        Assert.False(NewService().BlockApp(exeName!));
    }

    // A folder would put the key outside Image File Execution Options, and no program can have a character Windows
    // does not allow in a file name.
    [Theory]
    [InlineData(@"..\..\windows\system32\cmd.exe")]
    [InlineData(@"path\to\app.exe")]
    [InlineData(@"folder/app.exe")]
    [InlineData(@"a\b.exe")]
    [InlineData(@"..\x.exe")]
    [InlineData("app|pipe.exe")]
    [InlineData("app<>.exe")]
    [InlineData(@"app"".exe")]
    [InlineData("a:b.exe")]
    [InlineData("a*b.exe")]
    [InlineData("a?b.exe")]
    [InlineData(".exe")]
    [InlineData("   .exe")]
    public void ANameNoFileCanHave_IsRefused_AndNothingIsWritten(string exeName)
    {
        var svc = NewService();

        Assert.Equal(AppBlockerService.BlockResult.InvalidName, svc.TryBlockApp(exeName));
        Assert.False(svc.UnblockApp(exeName));
        Assert.False(svc.IsBlocked(exeName));
        Assert.Empty(Blocked());
    }

    [Fact]
    public void ANameWithALineBreakOrAnotherControlCharacter_IsRefused_AndNothingIsWritten()
    {
        // Built here rather than as test data, so no test name carries them. The trailing line break is the one a
        // pattern anchored with ^ and $ let through.
        var svc = NewService();

        foreach (var name in new[] { "a\nb.exe", "notepad.exe\n", "a\rb.exe", "a\tb.exe", "a\0b.exe", "a\u001Fb.exe" })
            Assert.Equal(AppBlockerService.BlockResult.InvalidName, svc.TryBlockApp(name));

        Assert.Empty(Blocked());
    }

    // Any name a file can have, so any name Browse returns: "+", brackets, letters of other alphabets, and the "&" and
    // ";" the registry gives no meaning to (#2850).
    [Theory]
    [InlineData("notepad", "notepad.exe")]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("my-app.exe", "my-app.exe")]
    [InlineData("My App.exe", "My App.exe")]
    [InlineData("app_v2.0.exe", "app_v2.0.exe")]
    [InlineData("notepad++.exe", "notepad++.exe")]
    [InlineData("LibreOffice (x64).exe", "LibreOffice (x64).exe")]
    [InlineData("Setup (1).exe", "Setup (1).exe")]
    [InlineData("Tool [beta].exe", "Tool [beta].exe")]
    [InlineData("Dicționar.exe", "Dicționar.exe")]
    [InlineData("Größe.exe", "Größe.exe")]
    [InlineData("Café", "Café.exe")]
    [InlineData("Tom & Jerry.exe", "Tom & Jerry.exe")]
    [InlineData("a;b.exe", "a;b.exe")]
    public void ANameAFileCanHave_IsBlockedAndLifted(string exeName, string stored)
    {
        var svc = NewService();

        Assert.Equal(AppBlockerService.BlockResult.Success, svc.TryBlockApp(exeName));
        Assert.Equal(stored, Assert.Single(Blocked()));
        Assert.True(svc.IsBlocked(stored));
        Assert.Contains(svc.GetBlockedApps()!, a => a.ExecutableName == stored);

        Assert.True(svc.UnblockApp(stored));
        Assert.Empty(Blocked());
    }

    [Theory]
    [InlineData(@"C:\Windows\notepad.exe", AppBlockerService.NameFault.Folder, '\\')]
    [InlineData("a/b?.exe", AppBlockerService.NameFault.Folder, '/')]
    [InlineData("a?b:.exe", AppBlockerService.NameFault.Character, '?')]
    [InlineData("a\"b.exe", AppBlockerService.NameFault.Character, '"')]
    [InlineData(".exe", AppBlockerService.NameFault.NoName, '\0')]
    [InlineData("  .EXE", AppBlockerService.NameFault.NoName, '\0')]
    [InlineData("notepad++.exe", AppBlockerService.NameFault.None, '\0')]
    public void CheckName_SaysWhatIsWrong_AFolderBeforeACharacter(
        string exeName, AppBlockerService.NameFault fault, char character)
    {
        // A pasted path is called one, not refused for its drive's colon.
        Assert.Equal((fault, character), AppBlockerService.CheckName(exeName));
    }

    [Fact]
    public void CheckName_TakesAsLongANameAsARegistryKeyHolds_AndNoLonger()
    {
        Assert.Equal(AppBlockerService.NameFault.None,
            AppBlockerService.CheckName(new string('a', 251) + ".exe").Fault);
        Assert.Equal(AppBlockerService.NameFault.TooLong,
            AppBlockerService.CheckName(new string('a', 252) + ".exe").Fault);
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
    // consent.exe is the UAC consent UI. Blocking it leaves no way to elevate, and unblocking writes to
    // HKLM, which needs elevation — so the refusal must happen before any registry write, exactly as for
    // the boot-critical set.
    [InlineData("consent.exe")]
    public void BlockApp_BootCriticalExecutable_IsRefused(string exeName)
    {
        // Blocking a boot/logon-critical process via IFEO would render Windows
        // unbootable. The denylist must reject it before any registry write.
        Assert.False(NewService().BlockApp(exeName));
        Assert.Empty(Blocked());
    }

    [Fact]
    public void GetBlockedApps_ReturnsListWithoutThrowing()
    {
        // Should not throw even without admin
        var result = new AppBlockerService().GetBlockedApps();
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
        var ex = Record.Exception(() => new AppBlockerService().IsBlocked("notepad.exe"));
        Assert.Null(ex);
    }
}
