// SysManager · HexColorTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows.Media;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="HexColor"/> is the boundary between colour text the app did not write and WPF's colour parser, which
/// reads names, scRGB and colour-profile addresses as well as hex. These pin what it reads and what it refuses.
/// </summary>
public sealed class HexColorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("smtest_hexcolor_");

    public void Dispose() => _dir.Delete(recursive: true);

    [Theory]
    [InlineData("#1E2530", 0xFF, 0x1E, 0x25, 0x30)]      // six digits are opaque, as WPF read them
    [InlineData("#801E2530", 0x80, 0x1E, 0x25, 0x30)]    // alpha first, as Color.ToString writes it
    [InlineData("#abcdef", 0xFF, 0xAB, 0xCD, 0xEF)]      // either case
    [InlineData("#ABCDEF", 0xFF, 0xAB, 0xCD, 0xEF)]
    [InlineData("  #1E2530\t", 0xFF, 0x1E, 0x25, 0x30)]  // surrounding white space trimmed, as WPF trimmed it
    [InlineData("#1E2530\n", 0xFF, 0x1E, 0x25, 0x30)]
    [InlineData("#1E2530\r\n", 0xFF, 0x1E, 0x25, 0x30)]
    public void TryParse_ReadsSixOrEightHexDigits(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(HexColor.TryParse(text, out var color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Red")]                  // a name
    [InlineData("#F00")]                 // the short forms
    [InlineData("#8F00")]
    [InlineData("sc#1")]                 // scRGB
    [InlineData("sc#1 0 0")]
    [InlineData("ContextColor x 1")]     // a colour profile
    [InlineData("#1E253")]               // five, seven and nine digits
    [InlineData("#1E25300")]
    [InlineData("#1E2530FF0")]
    [InlineData("#GG2530")]              // not hex
    [InlineData("1E2530")]               // no #
    [InlineData("#1E2530\n#000000")]     // a second line
    [InlineData("#+1E2530")]             // a sign uint.Parse would take with other styles
    public void TryParse_RefusesEveryOtherColourForm_WithoutThrowing(string? text)
    {
        Assert.False(HexColor.TryParse(text, out var color));
        Assert.Equal(default, color);
    }

    /// <summary>
    /// A ContextColor value names a colour profile that WPF's converter opens. Refused without opening it: the address
    /// is a file that does not exist in this test's own folder, never a share or a web address, so even with the
    /// refusal undone the open would stay on this machine.
    /// </summary>
    [Fact]
    public void TryParse_RefusesAColourProfileUri_WithoutOpeningIt()
    {
        var profile = new Uri(Path.Combine(_dir.FullName, "missing-profile.icc")).AbsoluteUri;

        Assert.False(HexColor.TryParse($"ContextColor {profile} 1,1,1,1", out _));
    }
}
