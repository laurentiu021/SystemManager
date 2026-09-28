// SysManager · ProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProfileViewModel"/>: the import and export reports (#2454), and what an export writes
/// (#2477). Import and export run behind file dialogs, so what is tested is what the view-model builds and says
/// around them. Runs against a temp config directory, so the real SysManager settings are never read.
/// </summary>
public class ProfileViewModelTests : IDisposable
{
    private readonly string _dir;

    public ProfileViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerProfileVmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteConfig(string fileName, string json) => File.WriteAllText(Path.Combine(_dir, fileName), json);

    private async Task<ProfileViewModel> NewVmAsync()
    {
        var vm = new ProfileViewModel(new ProfileService(_dir));
        await vm.InitializationComplete;
        return vm;
    }

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

    // ---------- export writes what is on disk now (#2477) ----------
    // The tab is built once and kept for the whole session. The list keeps each file's contents from when it was
    // read, and export used to write those, so anything changed since the tab opened was missing from the file.

    [Fact]
    public async Task Export_WritesEachTickedSectionAsItIsNow_NotAsItWasWhenTheTabOpened()
    {
        WriteConfig("theme.json", "{\"preset\":\"before\"}");
        WriteConfig("volume-presets.json", "[\"before\"]");
        var vm = await NewVmAsync();
        vm.Sections.Single(s => s.Section.Key == "volume").IsSelected = false;

        WriteConfig("theme.json", "{\"preset\":\"after\"}");
        var profile = await vm.BuildExportAsync(vm.SelectedKeys());

        var theme = Assert.Single(profile.Sections);   // the unticked section stays out
        Assert.Equal("theme", theme.Key);
        Assert.Contains("after", theme.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("before", theme.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_LeavesOutATickedSectionWhoseFileIsGone()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        WriteConfig("volume-presets.json", "[]");
        var vm = await NewVmAsync();

        File.Delete(Path.Combine(_dir, "volume-presets.json"));
        var profile = await vm.BuildExportAsync(vm.SelectedKeys());

        Assert.Equal(["theme"], profile.Sections.Select(s => s.Key));
    }

    [Fact]
    public void DescribeExport_WhenATickedSectionWasLeftOut_SaysSo()
    {
        Assert.Equal("Exported 2 sections to p.json.", ProfileViewModel.DescribeExport(2, 2, "p.json"));
        Assert.Equal("Exported 1 of 2 sections to p.json. 1 is no longer saved on this PC, so it was left out.",
            ProfileViewModel.DescribeExport(1, 2, "p.json"));
        Assert.Equal("Exported 1 of 3 sections to p.json. 2 are no longer saved on this PC, so they were left out.",
            ProfileViewModel.DescribeExport(1, 3, "p.json"));
    }

    // ---------- the list is re-read when the tab comes back on screen (#2477) ----------

    [Fact]
    public async Task ShowingTheTabAgain_ListsASettingSavedSinceItOpened()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        var vm = await NewVmAsync();
        Assert.Single(vm.Sections);

        WriteConfig("volume-presets.json", "[]");
        vm.IsActive = true;
        await vm.ShownRefresh;

        Assert.Equal(["theme", "volume"], vm.Sections.Select(s => s.Section.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ShowingTheTabAgain_KeepsTheUsersTicks()
    {
        WriteConfig("theme.json", "{\"preset\":\"midnight\"}");
        WriteConfig("volume-presets.json", "[]");
        var vm = await NewVmAsync();
        vm.Sections.Single(s => s.Section.Key == "theme").IsSelected = false;

        vm.IsActive = true;
        await vm.ShownRefresh;

        Assert.Equal(["volume"], vm.SelectedKeys());
    }

    [Fact]
    public async Task TheShellMarksTheTabAsShown()
    {
        // The wiring: MainWindowViewModel.SetActive is what tells a tab it is on screen.
        var vm = await NewVmAsync();

        MainWindowViewModel.SetActive(vm, active: true);
        await vm.ShownRefresh;
        Assert.True(vm.IsActive);

        MainWindowViewModel.SetActive(vm, active: false);
        Assert.False(vm.IsActive);
    }
}
