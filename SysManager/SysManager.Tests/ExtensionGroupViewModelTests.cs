// SysManager · ExtensionGroupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// One browser profile in Browser Cleaner's Extensions view: how many extensions it says, why a list it could not read
/// is empty, what its button opens, and what a screen reader announces.
/// </summary>
/// <remarks>No test referenced the group before (#2741).</remarks>
public class ExtensionGroupViewModelTests
{
    private static ExtensionProfile Profile(bool couldNotRead = false, bool behindALink = false) => new(
        "Microsoft Edge — Profile 1", "Microsoft Edge", "edge://extensions", "msedge.exe", "Profile 1", [],
        couldNotRead, BehindALink: behindALink);

    private static ExtensionRow Row(string name, string version = "1.0") => new(name, version, "", [], [], null);

    private static ExtensionGroupViewModel Group(ExtensionProfile profile, int rows, Action<ExtensionProfile>? manage = null) =>
        new(profile, Enumerable.Range(1, rows).Select(i => Row($"Extension {i}")).ToList(), manage ?? (_ => { }));

    [Theory]
    [InlineData(0, false, "0 extensions")]
    [InlineData(1, false, "1 extension")]
    [InlineData(2, false, "2 extensions")]
    [InlineData(0, true, "")]               // the note says why the list is empty, so no count says "0"
    [InlineData(1, true, "1 extension")]
    public void TheCount_SaysHowMany_AndIsLeftOutForAnEmptyListThatCouldNotBeRead(int rows, bool couldNotRead, string expected)
        => Assert.Equal(expected, Group(Profile(couldNotRead), rows).CountText);

    [Fact]
    public void AProfileThatWasRead_HasNoNote() => Assert.Equal("", Group(Profile(), 0).UnreadableNote);

    [Fact]
    public void AProfileThatCouldNotBeRead_SaysToCloseTheBrowserAndLookAgain()
        => Assert.Equal(
            "Microsoft Edge — Profile 1: its extension list could not be read. If Microsoft Edge is open, close it and look again.",
            Group(Profile(couldNotRead: true), 0).UnreadableNote);

    [Fact]
    public void AProfileBehindALink_SaysTheLinkIsNotFollowed()
        => Assert.Equal(
            "Microsoft Edge — Profile 1: its profile is kept in another folder through a link, which SysManager does not "
            + "follow, so its extensions could not be listed.",
            Group(Profile(couldNotRead: true, behindALink: true), 0).UnreadableNote);

    [Fact]
    public void TheTitleAndTheButton_NameTheProfileAndItsBrowser()
    {
        var group = Group(Profile(), 0);

        Assert.Equal("Microsoft Edge — Profile 1", group.Title);
        Assert.Equal("Microsoft Edge — Profile 1", group.ToString());   // what a screen reader announces
        Assert.Equal("Manage in Microsoft Edge", group.ManageText);
    }

    [Fact]
    public void Manage_HandsTheTabThisGroupsProfile()
    {
        List<ExtensionProfile> managed = [];
        var profile = Profile();

        Group(profile, 1, managed.Add).ManageCommand.Execute(null);

        Assert.Same(profile, Assert.Single(managed));
    }

    [Theory]
    [InlineData("2.1.0", "uBlock Origin, version 2.1.0")]
    [InlineData("", "uBlock Origin")]
    public void ARow_IsAnnouncedByItsNameAndVersion(string version, string spoken)
    {
        var row = Row("uBlock Origin", version);

        Assert.Equal(spoken, row.SpokenName);
        Assert.Equal(spoken, row.ToString());
    }

    [Fact]
    public void AFlag_IsAnnouncedByItsWords() => Assert.Equal("Off", new ExtensionFlag("Off", ExtensionFlagKind.Off).ToString());
}
