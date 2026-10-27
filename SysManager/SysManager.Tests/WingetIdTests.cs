// SysManager · WingetIdTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Pins the shared winget package-ID validator (<see cref="WingetId.IsValid"/>),
/// which is the single source of truth used by WingetService, UninstallerService
/// and BulkInstallerService before a package ID is interpolated into a winget
/// command line. This is a security boundary (the sole barrier against
/// command-injection through a crafted ID), so both the accept and reject paths
/// are asserted directly here rather than only through the services.
/// </summary>
public class WingetIdTests
{
    [Theory]
    [InlineData("Microsoft.VisualStudioCode")]
    [InlineData("Notepad++.Notepad++")]
    [InlineData("Microsoft.VisualStudio.2022.Community")]
    [InlineData("7zip.7zip")]
    [InlineData("Mozilla.Firefox")]
    public void IsValid_AcceptsRealWorldIds(string id) =>
        Assert.True(WingetId.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pkg; calc.exe")]
    [InlineData("pkg && calc")]
    [InlineData("pkg | more")]
    [InlineData("pkg`whoami`")]
    [InlineData("pkg$(whoami)")]
    [InlineData("pkg\"quote")]
    [InlineData("pkg\nnewline")]      // trailing/embedded newline — the \z anchor must reject
    [InlineData("pkg\ttab")]
    public void IsValid_RejectsInjectionAndBlank(string? id) =>
        Assert.False(WingetId.IsValid(id));

    [Fact]
    public void IsValid_RejectsOverLengthId() =>
        Assert.False(WingetId.IsValid(new string('a', 257)));

    [Fact]
    public void IsValid_AcceptsMaxLengthId() =>
        Assert.True(WingetId.IsValid(new string('a', 256)));

    // ---- the LOCAL ids winget prints for an app no source matches (#2849) ----

    /// <summary>
    /// The shapes winget actually emits, taken from real local ids on a live machine.
    /// </summary>
    /// <remarks>
    /// These are the ids that could be ticked, confirmed with "This cannot be undone", and then never
    /// removed, because the general allowlist has no backslash in it — most of the Store apps on a normal
    /// machine, plus every registry entry whose name did not match a source.
    /// </remarks>
    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"MSIX\Microsoft.WindowsNotepad_11.2302.16.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"ARP\Machine\X64\{90160000-008C-0000-1000-0000000FF1CE}")]
    [InlineData(@"ARP\Machine\X86\{8A69D345-D564-463C-AFF1-A69D9E530F96}")]
    [InlineData(@"ARP\User\X64\SomeRegistryKey")]
    [InlineData(@"ARP\Machine\X64\Some_App(64-bit)_is1")]   // parentheses, as real keys carry
    // Two shapes no local id on the machine this was derived from had, allowed on purpose so a machine
    // that could not be tested is not refused by a list: an ARM architecture, and a space in a registry
    // key name (arbitrary text, and a space cannot split a quoted argument).
    [InlineData(@"ARP\Machine\ARM64\{11111111-2222-3333-4444-555555555555}")]
    [InlineData(@"ARP\Machine\X64\Some App 3.12 (64-bit)_is1")]
    public void IsLocalUninstallId_AcceptsTheShapesWingetEmits(string id) =>
        Assert.True(WingetId.IsLocalUninstallId(id));

    /// <summary>
    /// The id is interpolated into <c>--id "{id}"</c>, so these must all be refused.
    /// </summary>
    /// <remarks>
    /// The quote would close the argument early and the TRAILING BACKSLASH would escape the closing quote,
    /// which is the one that looks harmless and is not. The rest are the ordinary injection shapes, asserted
    /// against the new rule rather than assumed to be covered because the old one covered them.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"MSIX\")]                                  // prefix alone, nothing to uninstall
    [InlineData(@"MSIX\pkg\")]                              // trailing backslash — escapes the closing quote
    [InlineData(@"ARP\Machine\X64\key\")]                   // same, on the other shape
    [InlineData(@"MSIX\pkg""; calc.exe")]                   // quote closes the argument
    [InlineData(@"MSIX\pkg with space")]
    [InlineData("MSIX\\pkg\nnewline")]                      // the \z anchor must refuse it
    [InlineData("MSIX\\pkg\ttab")]
    [InlineData(@"MSIX\pkg; calc.exe")]
    [InlineData(@"MSIX\pkg && calc")]
    [InlineData(@"MSIX\pkg | more")]
    [InlineData(@"MSIX\pkg`whoami`")]
    [InlineData(@"MSIX\pkg$(whoami)")]
    [InlineData(@"MSIX\..\..\Windows\System32\calc.exe")]   // extra segments beyond the shape
    [InlineData(@"ARP\Machine\X64\a\b")]                     // one segment too many
    [InlineData(@"ARP\Machine\key")]                         // one segment too few
    [InlineData(@"ARP\Other\X64\key")]                       // scope is Machine or User, nothing else
    [InlineData(@"MSIXpkg")]                                 // no separator at all
    [InlineData(@"msix\pkg")]                                // the prefix is case-sensitive, as winget prints it
    [InlineData(@"\MSIX\pkg")]                               // leading backslash
    [InlineData(@"MSIX\pkg/../other")]                       // a slash is not in the MSIX tail
    public void IsLocalUninstallId_RefusesAnythingThatCouldBreakTheQuoting(string? id) =>
        Assert.False(WingetId.IsLocalUninstallId(id));

    [Fact]
    public void IsLocalUninstallId_RejectsAnOverLongId() =>
        Assert.False(WingetId.IsLocalUninstallId(@"MSIX\" + new string('a', 251)));

    /// <summary>The two rules stay separate: a local id is NOT a generally valid winget id.</summary>
    /// <remarks>
    /// This is the assertion that fails if someone later "simplifies" the two into one allowlist — which
    /// would put a backslash into the rule guarding every other winget command line in the app.
    /// </remarks>
    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"ARP\Machine\X64\{90160000-008C-0000-1000-0000000FF1CE}")]
    public void IsValid_StillRefusesALocalId(string id) =>
        Assert.False(WingetId.IsValid(id));

    /// <summary>And an ordinary source id is not a local one, so the local rule cannot stand in for it.</summary>
    [Theory]
    [InlineData("Mozilla.Firefox")]
    [InlineData("Notepad++.Notepad++")]
    public void IsLocalUninstallId_DoesNotAcceptAnOrdinarySourceId(string id) =>
        Assert.False(WingetId.IsLocalUninstallId(id));
}
