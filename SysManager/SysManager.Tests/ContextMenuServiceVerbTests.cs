// SysManager · ContextMenuServiceVerbTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the verb half of <see cref="ContextMenuService"/>: hiding and showing a verb through
/// <c>LegacyDisable</c>, and which verbs count as Windows' own (#2822).
/// </summary>
/// <remarks>
/// The verb path wrote <c>Registry.ClassesRoot</c> and <c>Registry.CurrentUser</c> directly, so none of it could
/// be asserted without changing the machine running the suite. It now goes through the service's injected hives,
/// and every hive here is a stand-in under one disposable tree in HKCU, which a single delete cleans up.
/// </remarks>
public sealed class ContextMenuServiceVerbTests : IDisposable
{
    private readonly string _treeName = @"Software\SysManagerTests\ContextMenuVerbs_" + Guid.NewGuid().ToString("N");

    /// <summary>Stands in for HKEY_CLASSES_ROOT.</summary>
    private readonly RegistryKey _root;

    /// <summary>Stands in for HKEY_LOCAL_MACHINE, which the verb path never writes.</summary>
    private readonly RegistryKey _machine;

    /// <summary>Stands in for HKEY_CURRENT_USER, where the per-user key over a protected verb goes.</summary>
    private readonly RegistryKey _user;

    public ContextMenuServiceVerbTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_treeName + @"\Classes", writable: true)!;
        _machine = Registry.CurrentUser.CreateSubKey(_treeName + @"\Machine", writable: true)!;
        _user = Registry.CurrentUser.CreateSubKey(_treeName + @"\User", writable: true)!;
    }

    public void Dispose()
    {
        _root.Dispose();
        _machine.Dispose();
        _user.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_treeName, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { /* best-effort cleanup */ }
    }

    private const string Verb = @"*\shell\TestExample";
    private const string Override = @"Software\Classes\" + Verb;

    private ContextMenuService Service() => new(_root, _machine, _user);

    /// <summary>Registers a verb with a command, the way a program's installer does.</summary>
    private void GivenVerb(string path = Verb, string? muiVerb = null, bool hidden = false)
    {
        using var key = _root.CreateSubKey(path, writable: true)!;
        if (muiVerb is not null) key.SetValue("MUIVerb", muiVerb);
        if (hidden) key.SetValue("LegacyDisable", "", RegistryValueKind.String);
        using var cmd = key.CreateSubKey("command", writable: true)!;
        cmd.SetValue("", @"""C:\Program Files\Example\app.exe"" ""%1""");
    }

    /// <summary>A per-user key over the verb, the way the fallback for a protected verb writes one.</summary>
    private void GivenOverride(params (string Name, string Value)[] values)
    {
        using var key = _user.CreateSubKey(Override, writable: true)!;
        foreach (var (name, value) in values) key.SetValue(name, value, RegistryValueKind.String);
    }

    private static ContextMenuEntry VerbRow(bool enabled) => new()
    {
        Name = "Test Example",
        Command = @"C:\Program Files\Example\app.exe",
        RegistryPath = @"HKCR\" + Verb,
        Location = "Files",
        IsEnabled = enabled,
    };

    [Fact]
    public void DisableEntry_HidesAVerbThroughItsOwnKey_AndWritesNoPerUserKey()
    {
        GivenVerb();
        var entry = VerbRow(enabled: true);

        Assert.True(Service().DisableEntry(entry));

        Assert.False(entry.IsEnabled);
        using var key = _root.OpenSubKey(Verb);
        Assert.NotNull(key!.GetValue("LegacyDisable"));
        Assert.Null(_user.OpenSubKey(Override));
    }

    /// <summary>
    /// A verb whose key is gone is not "disabled" by creating a key for it (#2822).
    /// </summary>
    /// <remarks>
    /// The HKCR open returns null for a missing key rather than throwing, so the per-user fallback ran, created a
    /// brand-new key holding only LegacyDisable, and reported success; the "key not found" warning could never be
    /// reached.
    /// </remarks>
    [Fact]
    public void DisableEntry_OnAVerbWhoseKeyIsGone_FailsAndCreatesNothing()
    {
        var entry = VerbRow(enabled: true);

        Assert.False(Service().DisableEntry(entry));

        Assert.True(entry.IsEnabled);
        Assert.Null(_user.OpenSubKey(Override));
        Assert.Null(_root.OpenSubKey(Verb));
    }

    /// <summary>
    /// Turning a verb back on also removes the per-user key over it once it holds nothing else (#2822).
    /// </summary>
    /// <remarks>
    /// Turning it on deleted LegacyDisable and left the key, and once the program that owned the machine-wide verb
    /// was uninstalled, the empty per-user key was a verb with no command.
    /// </remarks>
    [Fact]
    public void EnableEntry_ShowsTheVerb_AndRemovesTheEmptyPerUserKeyOverIt()
    {
        GivenVerb(hidden: true);
        GivenOverride(("LegacyDisable", ""));
        var entry = VerbRow(enabled: false);

        Assert.True(Service().EnableEntry(entry));

        Assert.True(entry.IsEnabled);
        using var key = _root.OpenSubKey(Verb);
        Assert.Null(key!.GetValue("LegacyDisable"));
        Assert.Null(_user.OpenSubKey(Override));
    }

    /// <summary>A per-user key that holds something else is kept; only LegacyDisable comes off it.</summary>
    [Fact]
    public void EnableEntry_KeepsAPerUserKeyThatHoldsSomethingElse()
    {
        GivenVerb();
        GivenOverride(("LegacyDisable", ""), ("Icon", "app.exe,0"));

        Assert.True(Service().EnableEntry(VerbRow(enabled: false)));

        using var key = _user.OpenSubKey(Override);
        Assert.NotNull(key);
        Assert.Null(key.GetValue("LegacyDisable"));
        Assert.Equal("app.exe,0", key.GetValue("Icon"));
    }

    /// <summary>
    /// Turning on a verb that is gone succeeds and clears what was left over it, instead of failing (#2822).
    /// </summary>
    /// <remarks>Nothing hides a verb with no key, and run elevated the old failure blamed TrustedInstaller.</remarks>
    [Fact]
    public void EnableEntry_OnAVerbThatIsGone_SucceedsAndClearsItsPerUserKey()
    {
        GivenOverride(("LegacyDisable", ""));
        var entry = VerbRow(enabled: false);

        Assert.True(Service().EnableEntry(entry));

        Assert.True(entry.IsEnabled);
        Assert.Null(_user.OpenSubKey(Override));
    }

    /// <summary>
    /// A verb labelled only through MUIVerb, with an indirect string from a Windows module, is a Windows entry
    /// (#2822).
    /// </summary>
    /// <remarks>
    /// Windows' two "File ownership" verbs have no (Default) and a MUIVerb of <c>@efscore.dll,-101</c>. Their
    /// label was read from (Default) or the key name only, so they counted as third-party, and Win10 Default and
    /// Win11 Default switched them off with nothing to turn them back on. A third-party module's string stays
    /// third-party.
    /// </remarks>
    [Fact]
    public void ScanEntries_CountsAVerbLabelledThroughAWindowsModulesMuiVerb_AsWindows()
    {
        GivenVerb(@"*\shell\UpdateEncryptionSettingsWork", muiVerb: "@efscore.dll,-101");
        GivenVerb(@"Directory\shell\VendorTool", muiVerb: @"@C:\Program Files\Vendor\menu.dll,-5");

        var entries = Service().ScanEntries();

        Assert.True(Assert.Single(entries, e => e.RegistryPath.EndsWith("UpdateEncryptionSettingsWork",
            StringComparison.Ordinal)).IsSystemEntry);
        Assert.False(Assert.Single(entries, e => e.RegistryPath.EndsWith("VendorTool",
            StringComparison.Ordinal)).IsSystemEntry);
    }

    [Theory]
    [InlineData("@efscore.dll,-101", true)]
    [InlineData(@"@%SystemRoot%\system32\shell32.dll,-8506", true)]
    [InlineData(@"@""%SystemRoot%\System32\ntshrui.dll"",-1", true)]
    [InlineData(@"@C:\Program Files\Vendor\menu.dll,-5", false)]
    [InlineData(@"@%ProgramFiles%\Vendor\menu.dll,-5", false)]
    [InlineData("@{Microsoft.Windows.Photos_8wekyb3d8bbwe?ms-resource://Microsoft.Windows.Photos/Resources/Edit}", false)]
    [InlineData("Open with Example", false)]
    [InlineData("@", false)]
    [InlineData("@,-1", false)]
    [InlineData(null, false)]
    public void IsWindowsIndirectString_IsTrueForAWindowsModulesStringOnly(string? value, bool expected)
        => Assert.Equal(expected, ContextMenuService.IsWindowsIndirectString(value));
}
