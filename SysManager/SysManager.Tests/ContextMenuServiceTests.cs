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

    // ── Registry paths reach a reg.exe command line ─────────────────────────────────────────────────
    // BackupRegistry interpolates the path into `reg export "{path}" "{file}" /y`. The path is built
    // from a subkey NAME enumerated out of HKEY_CLASSES_ROOT, and HKCR\*\shell merges
    // HKCU\Software\Classes — which an unprivileged user can write. A key name may legally contain a
    // double quote, which closed the intended argument early and let further reg.exe arguments through,
    // controlling the export destination. This validation is the ONLY defense, so it is tested
    // explicitly, negative cases first.

    [Theory]
    [InlineData(@"HKCR\*\shell\evil"" ""C:\Windows\System32\out.reg"" /y")]  // the attack: destination hijack
    [InlineData(@"HKCR\*\shell\has""quote")]                                 // a bare embedded quote
    [InlineData("\"")]                                                       // nothing but a quote
    [InlineData("HKCR\\*\\shell\\trailing\0dropped")]                        // NUL truncates the command line
    [InlineData("")]                                                         // no path at all
    [InlineData("   ")]                                                      // whitespace only
    public void IsSafeRegistryPath_RejectsWhatCannotBePassedSafely(string path)
        => Assert.False(ContextMenuService.IsSafeRegistryPath(path));

    [Theory]
    [InlineData(@"HKCR\*\shell\Open with Notepad")]     // spaces are fine — the argument stays quoted
    [InlineData(@"HKCR\Directory\shell\cmd")]
    [InlineData(@"HKCR\*\shell\7-Zip")]
    [InlineData(@"HKCR\*\shell\Scan with Defender (2)")]  // parentheses are not shell metacharacters here
    [InlineData(@"HKCR\*\shell\C# project")]              // # is legal in a key name
    public void IsSafeRegistryPath_AcceptsRealWorldKeyNames(string path)
        => Assert.True(ContextMenuService.IsSafeRegistryPath(path),
            $"a legitimate key name must not be refused: {path}");

    [Fact]
    public void BackupRegistry_WithAHostilePath_ReturnsWithoutRunningRegExe()
    {
        // End-to-end on the real method: refusing must not become an exception either, because the
        // backup is best-effort and a throw here would block the enable/disable the user asked for.
        //
        // Asserts only that the call returns. It deliberately does NOT count files in the real
        // %LOCALAPPDATA%\SysManager\Backups: BackupRegistry has no injectable directory, so reading that
        // folder would make the test depend on the developer's own data — the failure mode this repo has
        // already had to fix twice (#1758, #1772). The refusal itself is proven by the IsSafeRegistryPath
        // theories above; what this adds is that the guard short-circuits instead of throwing.
        Assert.Null(Record.Exception(() =>
            ContextMenuService.BackupRegistry(@"HKCR\*\shell\evil"" ""C:\Windows\System32\pwned.reg"" /y")));
    }
}
