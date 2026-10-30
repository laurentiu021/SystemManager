// SysManager · LogServiceScrubBoundaryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Where the log scrub's user name ends (#2713).
/// </summary>
/// <remarks>
/// The scrub took everything after <c>Users\</c> up to the next backslash: spaces, quotes, line breaks, the exception
/// type and the rest of the event. A first path with no backslash after it ran on into a second one and took its
/// drive, so the second name was never scrubbed. Each test builds the scrubs for a profile of its own, so what it
/// shows does not depend on the name of whoever runs it.
/// </remarks>
public class LogServiceScrubBoundaryTests
{
    private static string Scrub(string text, string profile = @"C:\Users\Bob") =>
        LogService.SanitizePath(text, LogService.BuildUserPathRegexes(profile));

    [Fact]
    public void AnAccessDeniedEvent_KeepsItsMessageAndStackTrace_AndLosesEveryName()
    {
        const string Event =
            "Failed to open folder C:\\Users\\Public\r\n" +
            "System.UnauthorizedAccessException: Access to the path 'C:\\Users\\Bob' is denied.\r\n" +
            "   at SysManager.Services.CleanupService.Scan()";

        Assert.Equal(
            "Failed to open folder C:\\Users\\[user]\r\n" +
            "System.UnauthorizedAccessException: Access to the path 'C:\\Users\\[user]' is denied.\r\n" +
            "   at SysManager.Services.CleanupService.Scan()",
            Scrub(Event));
    }

    [Theory]
    [InlineData(@"Opened C:\Users\Bob and then failed", @"Opened C:\Users\[user] and then failed")]
    [InlineData(@"Saved to C:\Users\Bob.", @"Saved to C:\Users\[user].")]
    [InlineData(@"""C:\Users\Bob\Documents\a b.txt"" was locked", @"""C:\Users\[user]\Documents\a b.txt"" was locked")]
    [InlineData(@"C:\Users\Bob", @"C:\Users\[user]")]
    [InlineData(@"c:\USERS\bob\x", @"c:\USERS\[user]\x")]
    public void TheUsersOwnName_IsReplaced_AndWhatFollowsItIsKept(string text, string expected)
    {
        Assert.Equal(expected, Scrub(text));
    }

    [Theory]
    [InlineData(@"C:\Users\Ann O'Neil", @"Access to the path 'C:\Users\Ann O'Neil\Documents' is denied.",
        @"Access to the path 'C:\Users\[user]\Documents' is denied.")]
    [InlineData(@"C:\Users\Ann O'Neil", @"Moved C:\Users\Ann O'Neil", @"Moved C:\Users\[user]")]
    [InlineData(@"C:\Users\Bob.CORP", @"C:\Users\Bob.CORP\AppData\x", @"C:\Users\[user]\AppData\x")]
    public void AnOwnNameWithASpaceAQuoteOrADot_IsReplacedWhole(string profile, string text, string expected)
    {
        Assert.Equal(expected, Scrub(text, profile));
    }

    [Theory]
    [InlineData(@"'C:\Users\Carol' is denied.", @"'C:\Users\[user]' is denied.")]
    [InlineData(@"C:\Users\Carol Smith\Desktop", @"C:\Users\[user]\Desktop")]
    [InlineData(@"C:\Users\Bobby\x", @"C:\Users\[user]\x")]
    [InlineData("C:\\Users\\Carol\r\nnext line", "C:\\Users\\[user]\r\nnext line")]
    public void AnotherFolderInTheUsersFolder_IsReplacedUpToTheEndOfItsName(string text, string expected)
    {
        Assert.Equal(expected, Scrub(text));
    }

    [Fact]
    public void TwoPathsOnOneLine_AreBothScrubbed_WhenTheFirstHasNoBackslashAfterIt()
    {
        Assert.Equal(@"from C:\Users\[user] to 'C:\Users\[user]'", Scrub(@"from C:\Users\Public to 'C:\Users\Bob'"));
    }

    [Fact]
    public void WithNoProfileFolder_AnyDrivesUsersFolderIsScrubbed()
    {
        var scrubs = LogService.BuildUserPathRegexes(null);

        Assert.Null(scrubs.Own);
        Assert.Equal(@"D:\Users\[user]\x 'E:\Users\[user]' is denied.",
            LogService.SanitizePath(@"D:\Users\Dave\x 'E:\Users\Erin' is denied.", scrubs));
    }

    [Fact]
    public void TextWithNoUsersFolder_IsLeftAsItIs()
    {
        const string Text = "System.IO.IOException: The process cannot access the file 'D:\\Games\\save.dat'.";

        Assert.Equal(Text, Scrub(Text));
    }
}
