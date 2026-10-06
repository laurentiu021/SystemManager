// SysManager · ExtensionPresenterTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The sentences Browser Cleaner's Extensions view says about each extension and each browser (#1526): where an
/// extension came from and when, who can remove it, the toolbar's count, and what "Manage in …" did.
/// </summary>
public class ExtensionPresenterTests
{
    // Midday UTC, so the day is the same in every time zone the user could be in.
    private static readonly DateTime Midday = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static BrowserExtension Extension(ExtensionOrigin origin, DateTime? installed = null, string store = "",
        bool off = false, string name = "Example", bool everySite = false) =>
        new(name, "1.0", origin, off, installed, store, [], everySite, null);

    private static ExtensionProfile Profile(string product, params BrowserExtension[] extensions) =>
        new(product, product, "chrome://extensions", "chrome.exe", null, extensions, false);

    [Theory]
    [InlineData(ExtensionOrigin.Store, "the Chrome Web Store", true, "Installed 29 Sep 2026 · from the Chrome Web Store")]
    [InlineData(ExtensionOrigin.File, "", true, "Installed 29 Sep 2026 · not from an extension store")]
    [InlineData(ExtensionOrigin.AnotherProgram, "", true, "Installed 29 Sep 2026 · not from an extension store")]
    [InlineData(ExtensionOrigin.AnotherProgram, "the Chrome Web Store", true, "Installed 29 Sep 2026 · from the Chrome Web Store")]
    [InlineData(ExtensionOrigin.Organisation, "", true, "Installed 29 Sep 2026 · your organisation manages this one, so only it can remove it")]
    [InlineData(ExtensionOrigin.Organisation, "", false, "Your organisation manages this one, so only it can remove it")]
    [InlineData(ExtensionOrigin.Browser, "", true, "Came with the browser")]
    [InlineData(ExtensionOrigin.Folder, "", true, "Loaded from a folder on this PC")]
    [InlineData(ExtensionOrigin.Unknown, "", true, "Installed 29 Sep 2026")]
    [InlineData(ExtensionOrigin.Unknown, "", false, "")]
    [InlineData(ExtensionOrigin.File, "", false, "Not from an extension store")]
    public void TheLineUnderTheName_SaysWhenAndWhereFrom(ExtensionOrigin origin, string store, bool dated, string expected) =>
        Assert.Equal(expected, ExtensionPresenter.Meta(Extension(origin, dated ? Midday : null, store)));

    [Fact]
    public void AnExtensionAnotherProgramAdded_AndTurnedOff_CarriesBothMarks()
    {
        var flags = ExtensionPresenter.Flags(Extension(ExtensionOrigin.AnotherProgram, off: true));

        Assert.Equal(
            [new ExtensionFlag("Added by another program", ExtensionFlagKind.AnotherProgram), new ExtensionFlag("Off", ExtensionFlagKind.Off)],
            flags);
    }

    [Fact]
    public void AnExtensionTheOrganisationAdded_SaysSo()
    {
        var flags = ExtensionPresenter.Flags(Extension(ExtensionOrigin.Organisation));

        Assert.Equal([new ExtensionFlag("Added by your organisation", ExtensionFlagKind.Organisation)], flags);
    }

    [Theory]
    [InlineData(ExtensionOrigin.Store)]
    [InlineData(ExtensionOrigin.Browser)]
    [InlineData(ExtensionOrigin.File)]
    public void AnExtensionTheUserOrTheBrowserAdded_AndOn_CarriesNoMark(ExtensionOrigin origin) =>
        Assert.Empty(ExtensionPresenter.Flags(Extension(origin)));

    [Fact]
    public void TheCount_SaysHowManyInHowManyBrowsers_AndHowManyAnotherProgramPutThere()
    {
        var summary = ExtensionPresenter.Summary(
        [
            Profile("Google Chrome", Extension(ExtensionOrigin.AnotherProgram), Extension(ExtensionOrigin.Store)),
            Profile("Firefox", Extension(ExtensionOrigin.Store)),
        ]);

        Assert.Equal("3 extensions in 2 browsers · 1 was put there by another program", summary);
    }

    [Fact]
    public void TheCount_SaysWere_ForMoreThanOne_AndCountsABrowsersProfilesOnce()
    {
        var summary = ExtensionPresenter.Summary(
        [
            Profile("Microsoft Edge", Extension(ExtensionOrigin.AnotherProgram)),
            Profile("Microsoft Edge", Extension(ExtensionOrigin.AnotherProgram)),
        ]);

        Assert.Equal("2 extensions in 1 browser · 2 were put there by another program", summary);
    }

    [Fact]
    public void TheCount_LeavesOutABrowserWithNothingListed()
    {
        // An Edge profile whose list could not be read is shown with its note, but it is not a browser with extensions.
        var summary = ExtensionPresenter.Summary(
        [
            Profile("Google Chrome", Extension(ExtensionOrigin.Store)),
            Profile("Microsoft Edge"),
        ]);

        Assert.Equal("1 extension in 1 browser", summary);
    }

    [Fact]
    public void TheCount_LeavesOutAnotherProgram_WhenNoneDid_AndIsEmptyWithNothingToCount()
    {
        Assert.Equal("1 extension in 1 browser", ExtensionPresenter.Summary([Profile("Firefox", Extension(ExtensionOrigin.Store))]));
        Assert.Equal("", ExtensionPresenter.Summary([]));
        Assert.Equal("", ExtensionPresenter.Summary([Profile("Firefox")]));
    }

    [Theory]
    [InlineData(ExtensionsPageOpening.Opened, true, "Opened Google Chrome. If its extensions page did not open, paste chrome://extensions into the address bar — it is on your clipboard.")]
    [InlineData(ExtensionsPageOpening.Opened, false, "Opened Google Chrome. If its extensions page did not open, type chrome://extensions into the address bar.")]
    [InlineData(ExtensionsPageOpening.NotOpened, true, "Paste chrome://extensions into Google Chrome's address bar to see its extensions — it is on your clipboard.")]
    [InlineData(ExtensionsPageOpening.NotOpened, false, "Type chrome://extensions into Google Chrome's address bar to see its extensions.")]
    [InlineData(ExtensionsPageOpening.NotWhileElevated, true, "Open Google Chrome yourself and paste chrome://extensions into its address bar — it is on your clipboard. SysManager is running as administrator, so a browser it opened would run as administrator too.")]
    [InlineData(ExtensionsPageOpening.NotWhileElevated, false, "Open Google Chrome yourself and type chrome://extensions into its address bar. SysManager is running as administrator, so a browser it opened would run as administrator too.")]
    public void ManageIn_SaysWhatHappened_AndWhereToGoIfThePageDidNotOpen(ExtensionsPageOpening opening, bool copied, string expected) =>
        Assert.Equal(expected, ExtensionPresenter.ManageStatus(Profile("Google Chrome"), opening, copied));

    [Fact]
    public void TheDate_IsTheDayInWords()
    {
        Assert.Equal("29 Sep 2026", ExtensionPresenter.Date(Midday));
    }

    [Fact]
    public void AnIcon_IsDecodedOffTheUiThread_AndFrozen()
    {
        var icon = ExtensionPresenter.Icon(Png());

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    public void BytesThatAreNotAnImage_ShowTheGlyphInstead(byte[]? bytes) =>
        Assert.Null(ExtensionPresenter.Icon(bytes));

    [Fact]
    public void ARow_CarriesTheNameAndVersionForAScreenReader()
    {
        var row = ExtensionPresenter.Row(Extension(ExtensionOrigin.Store, name: "Video Speed Controller"));

        Assert.Equal("Video Speed Controller, version 1.0", row.SpokenName);
    }

    /// <summary>A real two-by-two PNG, encoded here so no binary fixture has to be kept.</summary>
    internal static byte[] Png()
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
