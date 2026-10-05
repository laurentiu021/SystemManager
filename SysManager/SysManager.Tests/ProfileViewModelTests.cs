// SysManager · ProfileViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ProfileViewModel"/>: the import and export reports (#2454), what an export writes
/// (#2477), and where an import's privacy choices go (#1530). Import and export run behind file dialogs, so what
/// is tested is what the view-model builds and says around them. Runs against a temp config directory and
/// substituted privacy toggles, so neither the real SysManager settings nor this PC's privacy settings are read.
/// </summary>
[Collection("ProcessWideStatics")]
public class ProfileViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly PrivacyChoicesHandoff _handoff = new();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();

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

    /// <summary>
    /// The tab over the temp folder, its first read settled. Every privacy protection is off unless the test
    /// passes the toggles it wants, so a profile carries privacy choices only where a test asks for them.
    /// </summary>
    private async Task<ProfileViewModel> NewVmAsync(IPrivacyService? privacy = null)
    {
        var vm = new ProfileViewModel(new ProfileService(privacy ?? FakePrivacy.AllOff(), _dir), _handoff, _navigation);
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

    // ---------- a profile file someone else made ----------

    [Fact]
    public async Task Import_OfAFileWithSectionsItCannotUse_ImportsTheRestInsteadOfThrowing()
    {
        // A null in the list threw while the confirmation was built, and a known key with no content threw in the
        // write after it; neither is an exception the import catches, so the tab went down over a bad file.
        using var dialog = new DialogAnswer(confirm: true);
        var vm = await NewVmAsync();
        var json = "{\"SchemaVersion\":1,\"AppVersion\":\"1.0.0\",\"ExportedAt\":\"2026-01-01T00:00:00\",\"Sections\":["
            + "null,"
            + "{\"Key\":\"theme\",\"DisplayName\":\"Theme & appearance\",\"FileName\":\"theme.json\"},"
            + "{\"Key\":\"volume\",\"DisplayName\":\"Volume presets\",\"FileName\":\"volume-presets.json\",\"Json\":\"[]\"}"
            + "]}";

        vm.Import(ProfileService.Deserialize(json));

        Assert.Equal(["volume-presets.json"], Directory.EnumerateFiles(_dir).Select(Path.GetFileName));
        Assert.Equal("Imported 1 section. Restart SysManager to apply everything.", vm.StatusMessage);
    }

    // ---------- privacy choices travel with the profile, and are never applied by it (#1530) ----------

    /// <summary>A profile as an import reads it: a theme file and privacy choices with one protection on.</summary>
    private static ConfigProfile ProfileWithPrivacy(bool withTheme = true)
    {
        List<ConfigSection> sections = [];
        if (withTheme)
            sections.Add(new ConfigSection("theme", "Theme & appearance", "theme.json", "{\"preset\":\"imported\"}"));
        sections.Add(new ConfigSection(ProfileService.PrivacySectionKey, "Privacy & Telemetry choices",
            "privacy-profile.json", "{\"Protections\":{\"tips\":true,\"widgets\":false}}"));
        return new ConfigProfile(ProfileService.CurrentSchemaVersion, "1.115.0", new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Local))
        {
            Sections = sections,
        };
    }

    /// <summary>The toggles the privacy choices above are read against, both off on this PC.</summary>
    private static IPrivacyService ThisPc() =>
        FakePrivacy.Returning(FakePrivacy.Toggle("tips", on: false), FakePrivacy.Toggle("widgets", on: false));

    [Fact]
    public async Task Export_CarriesThePrivacyChoicesWhenTheyAreTicked()
    {
        var vm = await NewVmAsync(FakePrivacy.Returning(FakePrivacy.Toggle("tips", on: true)));

        var row = Assert.Single(vm.Sections);
        Assert.Equal("Privacy & Telemetry choices", row.DisplayName);
        var profile = await vm.BuildExportAsync(vm.SelectedKeys());

        Assert.Equal(ProfileService.PrivacySectionKey, Assert.Single(profile.Sections).Key);
    }

    [Fact]
    public async Task Import_HandsThePrivacyChoicesToThePrivacyTab_AndWritesNoFileForThem()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = await NewVmAsync(ThisPc());

        vm.Import(ProfileWithPrivacy());

        var choices = _handoff.Take();
        Assert.NotNull(choices);
        Assert.True(choices!.Protections["tips"]);
        Assert.False(choices.Protections["widgets"]);
        _navigation.Received(1).GoTo("nav-privacy-settings", Arg.Any<string?>());

        // The theme was written and the privacy choices were not: the only file is the theme.
        Assert.Equal(["theme.json"], Directory.EnumerateFiles(_dir).Select(Path.GetFileName));
        Assert.Equal("Imported 2 sections. Restart SysManager to apply everything. The profile's privacy choices are "
            + "waiting on the Privacy & Telemetry tab, and nothing changes in Windows until you press Apply there.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task Import_OfPrivacyChoicesAlone_AsksForNoRestart()
    {
        // Nothing the restart would apply was written: the choices wait for Apply on their own tab.
        using var dialog = new DialogAnswer(confirm: true);
        var vm = await NewVmAsync(ThisPc());

        vm.Import(ProfileWithPrivacy(withTheme: false));

        Assert.DoesNotContain("Restart", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("waiting on the Privacy & Telemetry tab", vm.StatusMessage, StringComparison.Ordinal);
        Assert.NotNull(_handoff.Take());
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task Import_WhenDeclined_HandsNothingOverAndGoesNowhere()
    {
        using var dialog = new DialogAnswer(confirm: false);
        var vm = await NewVmAsync(ThisPc());

        vm.Import(ProfileWithPrivacy());

        Assert.Equal(1, dialog.Calls);
        Assert.Null(_handoff.Take());
        _navigation.DidNotReceiveWithAnyArgs().GoTo(default!);
        Assert.Empty(Directory.EnumerateFiles(_dir));
        Assert.Equal("Import cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task Import_PrivacyChoicesItCannotRead_AreReportedAsNotApplied_AndNothingIsHandedOver()
    {
        using var dialog = new DialogAnswer(confirm: true);
        // None of the profile's toggles exists in this build, so there is nothing to stage.
        var vm = await NewVmAsync(FakePrivacy.Returning(FakePrivacy.Toggle("cortana", on: false)));

        vm.Import(ProfileWithPrivacy());

        Assert.Null(_handoff.Take());
        _navigation.DidNotReceiveWithAnyArgs().GoTo(default!);
        Assert.StartsWith("Imported 1 of 2 sections — 1 could not be applied", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportConfirmation_SaysThePrivacyChoicesAreOnlyStaged()
    {
        var text = ProfileViewModel.DescribeImportConfirmation(ProfileWithPrivacy());

        Assert.Contains("  • Privacy & Telemetry choices", text, StringComparison.Ordinal);
        Assert.Contains("This overwrites the matching SysManager settings on this PC.", text, StringComparison.Ordinal);
        Assert.Contains("The privacy choices in this profile are not applied here.", text, StringComparison.Ordinal);
        Assert.Contains("nothing changes in Windows until you press Apply there", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportConfirmation_PromisesOnlyWhatTheProfileHolds()
    {
        // A profile of privacy choices alone overwrites no setting, and one without them stages nothing.
        var privacyOnly = ProfileViewModel.DescribeImportConfirmation(ProfileWithPrivacy(withTheme: false));
        Assert.DoesNotContain("overwrites", privacyOnly, StringComparison.Ordinal);

        var filesOnly = ProfileViewModel.DescribeImportConfirmation(
            ProfileWithPrivacy() with { Sections = [ProfileWithPrivacy().Sections[0]] });
        Assert.DoesNotContain("privacy", filesOnly, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This overwrites the matching SysManager settings on this PC.", filesOnly, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, "Imported 1 section. The profile's privacy choices are waiting on the Privacy & Telemetry tab, "
        + "and nothing changes in Windows until you press Apply there.")]
    [InlineData(2, 3, "Imported 2 of 3 sections — 1 could not be applied, and the log has the reason. Restart "
        + "SysManager to apply the imported settings. The profile's privacy choices are waiting on the Privacy & "
        + "Telemetry tab, and nothing changes in Windows until you press Apply there.")]
    [InlineData(1, 2, "Imported 1 of 2 sections — 1 could not be applied, and the log has the reason. The profile's "
        + "privacy choices are waiting on the Privacy & Telemetry tab, and nothing changes in Windows until you press "
        + "Apply there.")]
    public void DescribeImport_WithPrivacyChoicesHandedOver_PointsAtTheirTab(int applied, int total, string expected)
        => Assert.Equal(expected, ProfileViewModel.DescribeImport(applied, total, privacyHandedOver: true));

    [Fact]
    public async Task AProfilesPrivacyChoices_ArriveOnThePrivacyTabAsPendingChanges()
    {
        // End to end, through the same handoff the two tabs share: one PC exports with a protection on, the other
        // imports, and its Privacy & Telemetry tab shows that switch moved and waiting for Apply, nothing written.
        var exporter = await NewVmAsync(
            FakePrivacy.Returning(FakePrivacy.Toggle("tips", on: true), FakePrivacy.Toggle("widgets", on: false)));
        var exported = await exporter.BuildExportAsync(exporter.SelectedKeys());

        using var dialog = new DialogAnswer(confirm: true);
        var otherPc = ThisPc();
        var importer = await NewVmAsync(otherPc);
        importer.Import(ProfileService.Deserialize(ProfileService.Serialize(exported)));

        var privacyTab = new PrivacyViewModel(otherPc, Substitute.For<ISessionRestorePoint>(), _handoff);
        await privacyTab.InitializationComplete;

        Assert.Equal(1, privacyTab.PendingChangeCount);
        Assert.True(privacyTab.Toggles.Single(t => t.Key == "tips").IsEnabled);
        otherPc.DidNotReceiveWithAnyArgs().ApplyAll(default!);
        otherPc.DidNotReceiveWithAnyArgs().ApplyToggle(default!);
    }
}
