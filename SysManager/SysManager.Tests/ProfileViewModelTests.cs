// SysManager · ProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProfileViewModel"/>'s import report (#2454). The import itself runs behind a file dialog,
/// so what is tested here is the sentence built from what <see cref="SysManager.Services.ProfileService.ApplySections"/>
/// returned against what the confirm dialog listed.
/// </summary>
public class ProfileViewModelTests
{
    [Fact]
    public void DescribeImport_WhenEverySectionLanded_KeepsTheFullImportSentence()
        => Assert.Equal("Imported 3 sections. Restart SysManager to apply everything.",
            ProfileViewModel.DescribeImport(applied: 3, total: 3));

    [Fact]
    public void DescribeImport_WhenSomeSectionsWereSkipped_SaysHowMany()
    {
        // A section from a newer SysManager, one that fails the import check, or one that cannot be written is
        // skipped and only logged. The status used to report the applied count alone.
        var text = ProfileViewModel.DescribeImport(applied: 2, total: 3);

        Assert.StartsWith("Imported 2 of 3 sections — 1 could not be applied", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeImport_WhenNothingLanded_SaysNothingWasImported()
    {
        var text = ProfileViewModel.DescribeImport(applied: 0, total: 2);

        Assert.StartsWith("Nothing was imported", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Imported 0", text, StringComparison.Ordinal);
    }
}
