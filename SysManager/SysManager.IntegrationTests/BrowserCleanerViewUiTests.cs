// SysManager · BrowserCleanerViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Browser Cleaner really draws its Extensions half (#1526): the pills switch halves, each browser profile is a
/// group with its count and "Manage in …", and each extension's row carries its name, version, marks, the line
/// under the name and its permission chips.
/// </summary>
/// <remarks>
/// The unit tests prove the view model builds the groups and the words. They cannot prove the view shows them: a
/// binding on the wrong path compiles and draws nothing, and a chip whose warning trigger misses its target looks
/// like every other. So this loads the real XAML on the STA thread with the app's dictionaries, as
/// <see cref="AudioMixerViewUiTests"/> does. The extension service is a fake written here, and the cleaner points at
/// an empty temp tree: no browser profile is read and no browser is started.
/// </remarks>
public sealed class BrowserCleanerViewUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private static readonly DateTime Midday = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeExtensions(params ExtensionProfile[] profiles) : IBrowserExtensionService
    {
        public Task<IReadOnlyList<ExtensionProfile>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExtensionProfile>>(profiles);

        public ExtensionsPageOpening OpenExtensionsPage(ExtensionProfile profile) => ExtensionsPageOpening.NotOpened;
    }

    private static ExtensionProfile Chrome(bool couldNotRead = false, params BrowserExtension[] extensions) =>
        new("Google Chrome", "Google Chrome", "chrome://extensions", "chrome.exe", null, extensions, couldNotRead);

    private static readonly BrowserExtension SearchPro = new(
        "Search Pro New Tab", "2.4.1", ExtensionOrigin.AnotherProgram, IsOff: true, Midday, Store: "",
        [new("Changes your search engine", true), new("Can see your open tabs", false)], CanReadEverySite: false, Png());

    private static readonly BrowserExtension Docs = new(
        "Docs Offline", "1.92.1", ExtensionOrigin.Browser, IsOff: false, null, Store: "", [], CanReadEverySite: false, null);

    private async Task<BrowserCleanerViewModel> VmAsync(IBrowserExtensionService extensions, bool look)
    {
        var vm = new BrowserCleanerViewModel(
            new BrowserCleanerService(Path.Combine(_dir, "Local"), Path.Combine(_dir, "Roaming")), extensions, _ => true);
        await vm.InitializationComplete;
        vm.Section = BrowserCleanerSection.Extensions;
        if (look) await vm.ScanExtensionsCommand.ExecuteAsync(null);
        return vm;
    }

    private static BrowserCleanerView Laid(BrowserCleanerViewModel vm)
    {
        AppResources.Ensure();
        var view = new BrowserCleanerView { DataContext = vm };
        view.Measure(new Size(1200, 1600));
        view.Arrange(new Rect(0, 0, 1200, 1600));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task ThePills_ShowOneHalfAtATime()
    {
        var vm = await VmAsync(new FakeExtensions(), look: false);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            Assert.True(Shown(Named<Button>(view, "Look for extensions in every browser")));
            Assert.False(Shown(Named<Button>(view, "Scan browsers")));
            Assert.False(Shown(Named<DataGrid>(view, "Browser data categories")));

            Single<RadioButton>(view, r => (string)r.Content == "Browsing data").IsChecked = true;
            view.UpdateLayout();

            Assert.Equal(BrowserCleanerSection.BrowsingData, vm.Section);
            Assert.True(Shown(Named<Button>(view, "Scan browsers")));
            Assert.False(Shown(Named<Button>(view, "Look for extensions in every browser")));
        });
    }

    [Fact]
    public async Task BeforeTheFirstLook_TheHalfSaysWhatTheButtonDoes()
    {
        var vm = await VmAsync(new FakeExtensions(), look: false);

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Contains("See what each browser has added", shown);
            Assert.DoesNotContain("No extensions found", shown);
        });
    }

    [Fact]
    public async Task AfterALook_EachGroupAndRowShowsWhatTheBrowserRecorded()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(false, SearchPro, Docs)), look: true);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var list = Named<ItemsControl>(view, "Browser extensions");
            var shown = ShownTexts(list);

            Assert.Contains("Google Chrome", shown);
            Assert.Contains("2 extensions", shown);
            Assert.Contains("Search Pro New Tab", shown);
            Assert.Contains("2.4.1", shown);
            Assert.Contains("Added by another program", shown);
            Assert.Contains("Off", shown);
            Assert.Contains("Installed 29 Sep 2026 · not from an extension store", shown);
            Assert.Contains("Changes your search engine", shown);
            Assert.Contains("Can see your open tabs", shown);
            Assert.Contains("Came with the browser", shown);
            Assert.Contains("2 extensions in 1 browser · 1 was put there by another program", ShownTexts(view));
        });
    }

    [Fact]
    public async Task ManageIn_IsTheGroupsOwnCommand()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(false, Docs)), look: true);

        StaHelper.Run(() =>
        {
            var manage = Named<Button>(Laid(vm), "Manage in Google Chrome");

            Assert.Equal("Manage in Google Chrome", manage.Content);
            Assert.Same(vm.ExtensionGroups[0].ManageCommand, manage.Command);
        });
    }

    [Fact]
    public async Task AChipThatDeservesAttention_IsDrawnInTheWarningColour_AndTheOthersAreNot()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(false, SearchPro)), look: true);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var warning = (Brush)Application.Current.FindResource("WarningText");

            Assert.Same(warning, Text(view, "Changes your search engine").Foreground);
            Assert.NotSame(warning, Text(view, "Can see your open tabs").Foreground);
            Assert.Same(warning, Text(view, "Added by another program").Foreground);
        });
    }

    [Fact]
    public async Task AnExtensionsOwnIcon_IsDrawn_AndOneWithoutAnIcon_GetsTheGlyph()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(false, SearchPro, Docs)), look: true);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var rows = Descendants<Border>(view).Where(b => AutomationProperties.GetName(b) is { Length: > 0 } n && n.Contains(", version ", StringComparison.Ordinal)).ToList();
            var withIcon = Assert.Single(rows, r => AutomationProperties.GetName(r) == "Search Pro New Tab, version 2.4.1");
            var withoutIcon = Assert.Single(rows, r => AutomationProperties.GetName(r) == "Docs Offline, version 1.92.1");

            Assert.True(Shown(Assert.Single(Descendants<Image>(withIcon))));
            Assert.NotNull(Assert.Single(Descendants<Image>(withIcon)).Source);
            Assert.Null(Assert.Single(Descendants<Image>(withoutIcon)).Source);
            Assert.False(Shown(Assert.Single(Descendants<Image>(withoutIcon))));
            Assert.Contains("\uEA86", ShownGlyphs(withoutIcon));
            Assert.DoesNotContain("\uEA86", ShownGlyphs(withIcon));
        });
    }

    [Fact]
    public async Task AProfileWhoseListCouldNotBeRead_ShowsItsNote()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(couldNotRead: true)), look: true);

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Contains("Google Chrome: its extension list could not be read (it may be in use). Close Google Chrome and look again.", shown);
            Assert.DoesNotContain("No extensions found", shown);
        });
    }

    [Fact]
    public async Task ALookThatFindsNothing_SaysSo()
    {
        var vm = await VmAsync(new FakeExtensions(), look: true);

        StaHelper.Run(() =>
        {
            var shown = ShownTexts(Laid(vm));

            Assert.Contains("No extensions found", shown);
            Assert.DoesNotContain("See what each browser has added", shown);
        });
    }

    [Fact]
    public async Task TheList_SaysItOnlyReads()
    {
        var vm = await VmAsync(new FakeExtensions(Chrome(false, Docs)), look: true);

        StaHelper.Run(() =>
        {
            Assert.Contains(
                "SysManager only reads this list. To turn an extension off or remove it, use \"Manage in …\" — the browser asks you itself.",
                ShownTexts(Laid(vm)));
        });
    }

    private static TextBlock Text(DependencyObject root, string text) =>
        Assert.Single(Descendants<TextBlock>(root), t => t.Text == text);

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == name);

    private static T Single<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject =>
        Assert.Single(Descendants<T>(root), e => match(e));

    /// <summary>
    /// Whether nothing between <paramref name="element"/> and the view is collapsed. <c>IsVisible</c> cannot say: it
    /// is false for anything not shown in a window, and these views are laid out without one.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    private static List<string> ShownTexts(DependencyObject root) => Shown(root, glyphs: false);

    private static List<string> ShownGlyphs(DependencyObject root) => Shown(root, glyphs: true);

    /// <summary>The text actually on screen under <paramref name="root"/>, in tree order: words, or icon glyphs.</summary>
    private static List<string> Shown(DependencyObject root, bool glyphs)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && IsGlyph(block.Text) == glyphs) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>A real two-by-two PNG, encoded here so no binary fixture has to be kept.</summary>
    private static byte[] Png()
    {
        var pixels = new byte[2 * 2 * 4];
        Array.Fill(pixels, (byte)0x80);
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 2 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
