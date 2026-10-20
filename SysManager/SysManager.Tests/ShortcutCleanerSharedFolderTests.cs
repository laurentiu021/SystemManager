// SysManager · ShortcutCleanerSharedFolderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using static SysManager.Services.ShortcutCleanerService;

namespace SysManager.Tests;

/// <summary>
/// A shortcut in a folder OneDrive shares with the user's other PCs is not called broken because its target is
/// missing on this one.
/// </summary>
/// <remarks>
/// Once OneDrive backs up the Desktop, its shortcuts sync to every PC on the account. One that points at a program
/// installed on another of them has its target missing here, and it was listed as broken, ticked, and deleted from
/// every PC at once. The decision and the folders are asserted here; the scan over real shortcut files is in the
/// integration suite.
/// </remarks>
public sealed class ShortcutCleanerSharedFolderTests
{
    // Named rather than typed: the two enums are internal, and a public test method cannot take them.
    [Theory]
    [InlineData("Missing", false, "Broken")]
    [InlineData("Missing", true, "Shared")]
    [InlineData("Unreachable", false, "Unreachable")]
    [InlineData("Unreachable", true, "Unreachable")]
    [InlineData("Present", false, "Fine")]
    [InlineData("Present", true, "Fine")]
    public void AMissingTarget_IsBrokenOnlyOutsideASharedFolder(string verdict, bool shared, string expected)
        => Assert.Equal(Enum.Parse<ShortcutOutcome>(expected), Judge(Enum.Parse<TargetVerdict>(verdict), shared));

    [Fact]
    public void TheSharedFolders_AreTheOnesOneDriveNames()
    {
        var environment = new Dictionary<string, string?>
        {
            ["OneDrive"] = @"C:\Users\me\OneDrive",
            ["OneDriveConsumer"] = @"C:\Users\me\onedrive",       // the same folder, named twice
            ["OneDriveCommercial"] = @"C:\Users\me\OneDrive - Contoso",
        };

        Assert.Equal([@"C:\Users\me\OneDrive", @"C:\Users\me\OneDrive - Contoso"],
            SyncRoots(name => environment.GetValueOrDefault(name)));
    }

    [Fact]
    public void ANameThatIsNotAFullPath_IsNoSharedFolder()
    {
        // An empty or relative value would make every path, or none, look shared.
        var environment = new Dictionary<string, string?> { ["OneDrive"] = "", ["OneDriveConsumer"] = @"OneDrive" };

        Assert.Empty(SyncRoots(name => environment.GetValueOrDefault(name)));
    }

    [Fact]
    public void TheNotice_SaysWhyASharedShortcutWasLeftAlone()
    {
        var report = new ShortcutScanReport { SharedTargets = 1 };

        Assert.Equal("1 shortcut in a folder OneDrive shares with your other PCs points at something that is not on this "
            + "PC. It was left alone: it may work on another of your PCs, and deleting it here would delete it there too.",
            report.Notice);
        Assert.False(report.CheckedEverything);
    }

    [Fact]
    public void TheNotice_NamesBothKindsOfShortcutLeftAlone()
    {
        var report = new ShortcutScanReport { UnreachableTargets = 2, SharedTargets = 3 };

        Assert.StartsWith("2 shortcuts point somewhere this scan could not look", report.Notice, StringComparison.Ordinal);
        Assert.EndsWith("3 shortcuts in a folder OneDrive shares with your other PCs point at something that is not on "
            + "this PC. They were left alone: they may work on another of your PCs, and deleting them here would delete "
            + "them there too.", report.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void AScanThatDecidedEverything_HasNoNotice()
    {
        var report = new ShortcutScanReport();

        Assert.Null(report.Notice);
        Assert.True(report.CheckedEverything);
    }
}
