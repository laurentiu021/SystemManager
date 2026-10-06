// SysManager · BrowserCleanerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BrowserCleanerViewModel"/>: its Extensions half (#1526), and the toast it shows after a clean
/// (#2456) — the rescan replaces the status line at once, so the toast is often the only result the user sees.
/// </summary>
// Serialized: CleanAsync_WhileDiskCategoryLocked swaps the static DialogService.Instance and touches the
// process-wide OperationLockService.Instance. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public class BrowserCleanerViewModelTests
{
    // Points the service at an empty temp tree rather than the real profile folders, so the scan this VM
    // runs on construction finds nothing real and completes fast — no browser needs to be installed here.
    // The extension service is substituted, so no profile is read and no browser is started, and the clipboard
    // is a recorder, which a test thread could not reach anyway.
    private static BrowserCleanerViewModel NewVm(IBrowserExtensionService? extensions = null, List<string>? copied = null,
        bool clipboardWorks = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "smtest_browsercleaner_" + Guid.NewGuid().ToString("N"));
        var vm = new BrowserCleanerViewModel(
            new BrowserCleanerService(Path.Combine(root, "Local"), Path.Combine(root, "Roaming")),
            extensions ?? Substitute.For<IBrowserExtensionService>(),
            text =>
            {
                copied?.Add(text);
                return clipboardWorks;
            });
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    private static BrowserExtension Extension(string name, ExtensionOrigin origin = ExtensionOrigin.Store) =>
        new(name, "1.0", origin, false, null, origin == ExtensionOrigin.Store ? "the Chrome Web Store" : "", [], false, null);

    private static ExtensionProfile Profile(string browser, string product, bool couldNotRead, params BrowserExtension[] extensions) =>
        new(browser, product, "chrome://extensions", "chrome.exe", null, extensions, couldNotRead);

    private static IBrowserExtensionService Finding(params ExtensionProfile[] profiles)
    {
        var service = Substitute.For<IBrowserExtensionService>();
        service.ScanAsync(Arg.Any<CancellationToken>()).Returns(profiles);
        return service;
    }

    // ── The Extensions half (#1526) ─────────────────────────────────────────

    [Fact]
    public void TheTab_OpensOnTheBrowsingData_AndThePillsSwitchHalves()
    {
        var vm = NewVm();
        Assert.True(vm.IsBrowsingData);
        Assert.False(vm.IsExtensions);

        vm.IsExtensions = true;
        Assert.Equal(BrowserCleanerSection.Extensions, vm.Section);
        Assert.False(vm.IsBrowsingData);

        // Unchecking a pill is the other pill being checked; on its own it moves nothing.
        vm.IsExtensions = false;
        Assert.Equal(BrowserCleanerSection.Extensions, vm.Section);

        vm.IsBrowsingData = true;
        Assert.Equal(BrowserCleanerSection.BrowsingData, vm.Section);
    }

    [Fact]
    public void F5_LooksForExtensions_WhileTheyAreShown_AndScans_Otherwise()
    {
        var vm = NewVm();
        Assert.Same(vm.ScanCommand, vm.RefreshOnF5);

        vm.Section = BrowserCleanerSection.Extensions;

        Assert.Same(vm.ScanExtensionsCommand, vm.RefreshOnF5);
    }

    [Theory]
    [InlineData("extensions")]
    [InlineData("Add-ons")]
    [InlineData("addon")]
    [InlineData("my search changed")]
    [InlineData("browser ads")]
    public void ASearchInTheUsersWordsForAnExtension_OpensTheExtensions(string typed)
    {
        var vm = NewVm();

        vm.ArriveFromSearch(typed);

        Assert.True(vm.IsExtensions);
    }

    [Theory]
    [InlineData("cookies")]
    [InlineData("browser cache")]
    [InlineData("")]
    public void AnyOtherSearch_LeavesTheTabWhereItWas(string typed)
    {
        var vm = NewVm();

        vm.ArriveFromSearch(typed);

        Assert.True(vm.IsBrowsingData);
    }

    [Fact]
    public void BeforeTheFirstLook_TheTabSaysWhatTheButtonDoes_NotThatThereAreNone()
    {
        var vm = NewVm();

        Assert.True(vm.ShowExtensionsPrompt);
        Assert.False(vm.ShowNoExtensions);
        Assert.False(vm.HasExtensions);
    }

    [Fact]
    public async Task LookingForExtensions_ListsEachProfile_WithItsRows_AndCountsThem()
    {
        var vm = NewVm(Finding(
            Profile("Google Chrome", "Google Chrome", false, Extension("Search Pro", ExtensionOrigin.AnotherProgram), Extension("Docs")),
            Profile("Firefox", "Firefox", false, Extension("Speed"))));

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        Assert.Equal(["Google Chrome", "Firefox"], vm.ExtensionGroups.Select(g => g.Title));
        Assert.Equal(["Search Pro", "Docs"], vm.ExtensionGroups[0].Rows.Select(r => r.Name));
        Assert.Equal("2 extensions", vm.ExtensionGroups[0].CountText);
        Assert.Equal("Manage in Google Chrome", vm.ExtensionGroups[0].ManageText);
        Assert.Equal("3 extensions in 2 browsers · 1 was put there by another program", vm.ExtensionsSummary);
        Assert.Equal("Found 3 extensions in 2 browsers · 1 was put there by another program.", vm.StatusMessage);
        Assert.True(vm.HasExtensions);
        Assert.False(vm.ShowExtensionsPrompt);
        Assert.False(vm.ShowNoExtensions);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ALookThatFindsNothing_SaysSo()
    {
        var vm = NewVm(Finding());

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        Assert.True(vm.ShowNoExtensions);
        Assert.False(vm.HasExtensions);
        Assert.Equal("", vm.ExtensionsSummary);
        Assert.Equal("No extensions found in any browser on this PC.", vm.StatusMessage);
    }

    [Fact]
    public async Task AProfileWhoseListCouldNotBeRead_IsShownWithItsNote_NeverAsNone()
    {
        var vm = NewVm(Finding(Profile("Microsoft Edge — Profile 2", "Microsoft Edge", true)));

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        var group = Assert.Single(vm.ExtensionGroups);
        Assert.Equal(
            "Microsoft Edge — Profile 2: its extension list could not be read. If Microsoft Edge is open, close it and look again.",
            group.UnreadableNote);
        // Nothing was counted, so the header does not claim "0 extensions".
        Assert.Equal("", group.CountText);
        Assert.True(vm.HasExtensions);
        Assert.False(vm.ShowNoExtensions);
        Assert.Equal("No extensions could be listed: a browser's list could not be read.", vm.StatusMessage);
    }

    [Fact]
    public async Task AGroupOfOne_SaysExtension_NotExtensions()
    {
        var vm = NewVm(Finding(Profile("Firefox", "Firefox", false, Extension("Speed"))));

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        Assert.Equal("1 extension", Assert.Single(vm.ExtensionGroups).CountText);
    }

    [Fact]
    public async Task AProfileThatWasRead_HasNoNote()
    {
        var vm = NewVm(Finding(Profile("Firefox", "Firefox", false, Extension("Speed"))));

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        Assert.Equal("", Assert.Single(vm.ExtensionGroups).UnreadableNote);
    }

    [Fact]
    public async Task ACancelledLook_SaysSo_AndLeavesTheTabFree()
    {
        var service = Substitute.For<IBrowserExtensionService>();
        service.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ExtensionProfile>>(new OperationCanceledException()));
        var vm = NewVm(service);

        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        Assert.Equal("Cancelled.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasLookedForExtensions);
    }

    [Fact]
    public async Task ManageIn_OpensTheBrowser_PutsItsPageOnTheClipboard_AndSaysWhatToDo()
    {
        var profile = Profile("Google Chrome", "Google Chrome", false, Extension("Docs"));
        var service = Finding(profile);
        service.OpenExtensionsPage(profile).Returns(ExtensionsPageOpening.Opened);
        List<string> copied = [];
        var vm = NewVm(service, copied);
        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        vm.ExtensionGroups[0].ManageCommand.Execute(null);

        service.Received(1).OpenExtensionsPage(profile);
        Assert.Equal(["chrome://extensions"], copied);
        Assert.Equal(
            "Opened Google Chrome. If its extensions page did not open, paste chrome://extensions into the address bar — it is on your clipboard.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task ManageIn_WhenTheBrowserCannotBeStarted_SaysWhereToPasteThePage()
    {
        var profile = Profile("Opera GX", "Opera GX", false, Extension("Docs"));
        var service = Finding(profile);
        service.OpenExtensionsPage(profile).Returns(ExtensionsPageOpening.NotOpened);
        var vm = NewVm(service);
        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        vm.ExtensionGroups[0].ManageCommand.Execute(null);

        Assert.Equal("Paste chrome://extensions into Opera GX's address bar to see its extensions — it is on your clipboard.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task ManageIn_WhileSysManagerRunsAsAdministrator_SaysWhyTheBrowserDidNotOpen()
    {
        var profile = Profile("Google Chrome", "Google Chrome", false, Extension("Docs"));
        var service = Finding(profile);
        service.OpenExtensionsPage(profile).Returns(ExtensionsPageOpening.NotWhileElevated);
        List<string> copied = [];
        var vm = NewVm(service, copied);
        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        vm.ExtensionGroups[0].ManageCommand.Execute(null);

        Assert.Equal(["chrome://extensions"], copied);
        Assert.Equal(
            "Open Google Chrome yourself and paste chrome://extensions into its address bar — it is on your clipboard. "
            + "SysManager is running as administrator, so a browser it opened would run as administrator too.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task ManageIn_WhenTheClipboardIsBusy_DoesNotClaimThePageIsOnIt()
    {
        var profile = Profile("Google Chrome", "Google Chrome", false, Extension("Docs"));
        var service = Finding(profile);
        service.OpenExtensionsPage(profile).Returns(ExtensionsPageOpening.Opened);
        var vm = NewVm(service, clipboardWorks: false);
        await vm.ScanExtensionsCommand.ExecuteAsync(null);

        vm.ExtensionGroups[0].ManageCommand.Execute(null);

        Assert.DoesNotContain("clipboard", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanAsync_WhileDiskCategoryLocked_RefusesAndCleansNothing()
    {
        // #2510. A deletion batch that is cancelled with nothing to name it when SysManager closes
        // mid-run now shares the disk-scanning/deleting tabs' lock.
        var vm = NewVm();
        vm.Items.Add(new BrowserCleanupItem
        {
            Browser = "Test Browser",
            Category = "Cache",
            Description = "test",
            Paths = [],
            IsSelected = true,
        });

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(held);
        try
        {
            await vm.CleanCommand.ExecuteAsync(null);

            Assert.Equal("Cannot start — Deep Cleanup is already running.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
            Assert.Single(vm.Items);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public void CleanToast_WhenNothingWasRemoved_DoesNotSayTheDataWasCleaned()
    {
        // An open browser can hold every file. The toast said "Browser data cleaned — 0 files removed."
        var (title, detail) = BrowserCleanerViewModel.CleanToast(0);

        Assert.Equal("Nothing was removed", title);
        Assert.Contains("Close it and clean again", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanToast_WhenFilesWereRemoved_SaysHowMany()
        => Assert.Equal(("Browser data cleaned", "12 files removed."), BrowserCleanerViewModel.CleanToast(12));
}
