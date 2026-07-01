// SysManager · LogServiceSanitizeEdgeCaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using SysManager.Services;

namespace SysManager.Tests;

public class LogServiceSanitizeEdgeCaseTests
{
    [Fact]
    public void SanitizePath_NullInput_ReturnsEmpty()
    {
        Assert.Equal("", LogService.SanitizePath(null));
    }

    [Fact]
    public void SanitizePath_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", LogService.SanitizePath(""));
    }

    [Fact]
    public void SanitizePath_PathWithUsername_ReplacesUsername()
    {
        var result = LogService.SanitizePath(@"C:\Users\john.doe\Documents\file.txt");
        Assert.Equal(@"C:\Users\[user]\Documents\file.txt", result);
    }

    [Fact]
    public void SanitizePath_PathWithUsernameUpperCase_ReplacesUsername()
    {
        var result = LogService.SanitizePath(@"C:\USERS\ADMIN\Desktop\file.txt");
        Assert.Equal(@"C:\USERS\[user]\Desktop\file.txt", result);
    }

    [Fact]
    public void SanitizePath_NoUserPath_ReturnsUnchanged()
    {
        var result = LogService.SanitizePath(@"D:\Projects\MyApp\bin\app.exe");
        Assert.Equal(@"D:\Projects\MyApp\bin\app.exe", result);
    }

    [Fact]
    public void SanitizePath_MultipleUsersInPath_ReplacesAll()
    {
        var result = LogService.SanitizePath(@"C:\Users\alice\backup\C:\Users\bob\file.txt");
        Assert.Contains("[user]", result);
        Assert.DoesNotContain("alice", result);
        Assert.DoesNotContain("bob", result);
    }

    [Fact]
    public void SanitizePath_UsernameWithDots_ReplacesCorrectly()
    {
        var result = LogService.SanitizePath(@"C:\Users\first.last\AppData\Local\file.txt");
        Assert.Equal(@"C:\Users\[user]\AppData\Local\file.txt", result);
    }

    [Fact]
    public void SanitizePath_UsernameWithHyphen_ReplacesCorrectly()
    {
        var result = LogService.SanitizePath(@"C:\Users\my-user\Documents\test.txt");
        Assert.Equal(@"C:\Users\[user]\Documents\test.txt", result);
    }

    [Fact]
    public void SanitizePath_JustUserFolder_ReplacesUsername()
    {
        var result = LogService.SanitizePath(@"C:\Users\testuser");
        Assert.Equal(@"C:\Users\[user]", result);
    }

    [Fact]
    public void SanitizePath_WindowsTempPath_NoChange()
    {
        var result = LogService.SanitizePath(@"C:\Windows\Temp\file.tmp");
        Assert.Equal(@"C:\Windows\Temp\file.tmp", result);
    }
}
