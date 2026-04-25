// SysManager · LogServiceSanitizeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

public class LogServiceSanitizeTests
{
    [Fact]
    public void SanitizePath_ReplacesUsername()
    {
        var result = LogService.SanitizePath(@"C:\Users\JohnDoe\AppData\Local\SysManager");
        Assert.Equal(@"C:\Users\[user]\AppData\Local\SysManager", result);
    }

    [Fact]
    public void SanitizePath_CaseInsensitive()
    {
        var result = LogService.SanitizePath(@"c:\users\Admin\Documents");
        Assert.Equal(@"c:\users\[user]\Documents", result);
    }

    [Fact]
    public void SanitizePath_NullReturnsEmpty()
    {
        Assert.Equal("", LogService.SanitizePath(null));
    }

    [Fact]
    public void SanitizePath_EmptyReturnsEmpty()
    {
        Assert.Equal("", LogService.SanitizePath(""));
    }

    [Fact]
    public void SanitizePath_NoUserPath_Unchanged()
    {
        var input = @"D:\Games\SomeGame";
        Assert.Equal(input, LogService.SanitizePath(input));
    }

    [Fact]
    public void SanitizePath_MultipleUserPaths()
    {
        var result = LogService.SanitizePath(@"C:\Users\Alice\file.txt and C:\Users\Bob\file.txt");
        Assert.Equal(@"C:\Users\[user]\file.txt and C:\Users\[user]\file.txt", result);
    }
}
