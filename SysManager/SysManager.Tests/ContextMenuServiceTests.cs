// SysManager · ContextMenuServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ContextMenuService"/> name cleanup — specifically that Windows
/// accelerator ampersands are stripped so a context-menu label never renders a literal
/// '&amp;' mid-word in its plain TextBlock.
/// </summary>
public class ContextMenuServiceTests
{
    [Theory]
    [InlineData("&Open", "Open")]                                        // leading accelerator
    [InlineData("Scan with Microsoft &Defender", "Scan with Microsoft Defender")] // mid-phrase
    [InlineData("P&roperties", "Properties")]                            // interior accelerator
    [InlineData("Open", "Open")]                                         // no ampersand — unchanged
    [InlineData("Fish && Chips", "Fish & Chips")]                        // escaped literal ampersand
    [InlineData("&Save && Exit", "Save & Exit")]                         // accelerator + escaped literal
    public void StripMnemonic_RemovesAccelerator_KeepsEscapedLiteral(string input, string expected)
    {
        Assert.Equal(expected, ContextMenuService.StripMnemonic(input));
    }

    [Fact]
    public void StripMnemonic_NullOrEmpty_ReturnsInput()
    {
        Assert.Equal("", ContextMenuService.StripMnemonic(""));
        Assert.Null(ContextMenuService.StripMnemonic(null!));
    }

    [Fact]
    public void SplitCamelCase_CapitalizesInvariantly_OnTurkishCulture()
    {
        // Regression: char.ToUpper on the first letter is culture-sensitive — under tr-TR it
        // maps 'i' → 'İ' (dotted capital I), corrupting entry names. ToUpperInvariant keeps
        // shell-verb / app identifiers ASCII-cased regardless of the user's locale.
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            Assert.Equal("Iexplore", ContextMenuService.SplitCamelCase("iexplore"));
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }
}
