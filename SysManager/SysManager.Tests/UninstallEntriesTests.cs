// SysManager · UninstallEntriesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The list of installed programs the Uninstaller's leftover search refuses folders by, and Recent Changes reads.
/// </summary>
/// <remarks>
/// Read from a throwaway key under <c>HKEY_CURRENT_USER\Software\SysManagerTests</c> laid out as the three uninstall
/// lists, so what the reader decides about each entry is checked against a real registry without touching the PC's
/// own list. Every other test of the search replaces this read with a list it wrote, so a change that dropped one of
/// the lists, misread a value or reported a part-read as whole would weaken the refusal with those tests still green.
/// </remarks>
public sealed class UninstallEntriesTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\UninstallEntries_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public UninstallEntriesTests() => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); }
        catch (System.Security.SecurityException) { /* left under SysManagerTests: harmless */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>The three lists in production's order: the machine's 64-bit and 32-bit, then the user's.</summary>
    private (RegistryKey Hive, string Path)[] Lists => [(_root, "Machine64"), (_root, "Machine32"), (_root, "User")];

    private void Entry(string list, string name, params (string Value, object Data)[] values)
    {
        using var key = _root.CreateSubKey($@"{list}\{name}", writable: true)!;
        foreach (var (value, data) in values) key.SetValue(value, data);
    }

    [Fact]
    public void TheMachinesListsAndTheUsers_AreTheOnesRead()
    {
        // A per-user install, Discord or VS Code say, is only in the user's list; a 32-bit one only in WOW6432Node.
        Assert.Equal(
            [
                ("HKEY_LOCAL_MACHINE", UninstallerService.UninstallKey),
                ("HKEY_LOCAL_MACHINE", UninstallerService.UninstallKeyWow64),
                ("HKEY_CURRENT_USER", UninstallerService.UninstallKey),
            ],
            UninstallEntries.Keys.Select(k => (k.Hive.Name, k.Path)));
    }

    [Fact]
    public void EveryList_IsRead_InOrder_WithWhatEachEntryNames()
    {
        var uninstaller = @"""C:\Program Files\Acme Notes\uninst.exe"" /S";
        Entry("Machine64", "{A}", ("DisplayName", "Acme Notes"), ("Publisher", "Acme"),
            ("InstallLocation", @" C:\Program Files\Acme Notes "), ("UninstallString", uninstaller),
            ("DisplayIcon", @"C:\Program Files\Acme Notes\notes.exe,0"));
        Entry("Machine32", "Acme Draw", ("DisplayName", "Acme Draw"));
        Entry("User", "Acme Sync", ("DisplayName", "Acme Sync"));

        var list = UninstallEntries.ReadAll(Lists);

        Assert.True(list.Complete);
        Assert.Equal(["Acme Notes", "Acme Draw", "Acme Sync"], list.Entries.Select(e => e.Name));
        var notes = list.Entries[0];
        Assert.Equal("Acme", notes.Publisher);
        Assert.Equal(@"C:\Program Files\Acme Notes", notes.InstallLocation);
        Assert.Equal(uninstaller, notes.UninstallCommand);
        Assert.Equal(@"C:\Program Files\Acme Notes\notes.exe,0", notes.DisplayIcon);
        Assert.Equal($@"{_root.Name}\Machine64\{{A}}", notes.Key);
        Assert.True(notes.IsListed);
    }

    [Fact]
    public void OnlyADwordOfOne_MakesAnEntryAHiddenComponent()
    {
        Entry("Machine64", "Driver", ("DisplayName", "Acme Audio Driver"), ("SystemComponent", 1));
        Entry("Machine64", "Shown", ("DisplayName", "Acme Notes"), ("SystemComponent", 0));
        Entry("Machine64", "AsText", ("DisplayName", "Acme Draw"), ("SystemComponent", "1"));

        var entries = UninstallEntries.ReadAll(Lists).Entries.ToDictionary(e => e.Name);

        Assert.True(entries["Acme Audio Driver"].IsSystemComponent);
        Assert.False(entries["Acme Audio Driver"].IsListed);
        Assert.False(entries["Acme Notes"].IsSystemComponent);
        Assert.False(entries["Acme Draw"].IsSystemComponent);
    }

    [Theory]
    [InlineData("ParentKeyName", "{A}")]
    [InlineData("ReleaseType", "Security Update")]
    [InlineData("ReleaseType", "hotfix")]
    public void AnUpdate_IsNotAProgram(string value, string data)
    {
        Entry("Machine64", "KB1", ("DisplayName", "Update for Acme Notes"), (value, data));
        Entry("Machine64", "Program", ("DisplayName", "Acme Notes"), ("ReleaseType", "Release"));

        var entries = UninstallEntries.ReadAll(Lists).Entries.ToDictionary(e => e.Name);

        Assert.True(entries["Update for Acme Notes"].IsUpdate);
        Assert.False(entries["Update for Acme Notes"].IsListed);
        Assert.False(entries["Acme Notes"].IsUpdate);
        Assert.True(entries["Acme Notes"].IsListed);
    }

    [Fact]
    public void AListThatIsNotThere_LeavesTheReadWhole()
    {
        // A 32-bit Windows has no WOW6432Node list, and a user who never installed anything for themselves has no
        // list of their own: there is nothing in either to miss.
        Entry("Machine64", "A", ("DisplayName", "Acme Notes"));

        var list = UninstallEntries.ReadAll(Lists);

        Assert.True(list.Complete);
        Assert.Equal("Acme Notes", Assert.Single(list.Entries).Name);
    }

    [Fact]
    public void AListThatCannotBeRead_MakesTheReadIncomplete()
    {
        // The leftover search refuses everything found by a name while this is false, so it must be false exactly
        // when installed programs can be missing.
        Entry("Machine64", "A", ("DisplayName", "Acme Notes"));
        Entry("User", "B", ("DisplayName", "Acme Sync"));

        var list = WhileUnreadable("User", () => UninstallEntries.ReadAll(Lists));

        Assert.False(list.Complete);
        Assert.Equal(["Acme Notes"], list.Entries.Select(e => e.Name));
    }

    [Fact]
    public void OneEntryThatCannotBeRead_IsSkipped_AndTheReadStaysWhole()
    {
        // A protected entry is the same at every read, so it does not make the list one that can be missing apps.
        Entry("Machine64", "A", ("DisplayName", "Acme Notes"));
        Entry("Machine64", "B", ("DisplayName", "Acme Draw"));

        var list = WhileUnreadable(@"Machine64\B", () => UninstallEntries.ReadAll(Lists));

        Assert.True(list.Complete);
        Assert.Equal(["Acme Notes"], list.Entries.Select(e => e.Name));
    }

    [Fact]
    public void TheLeftoverSearch_GetsEveryEntrysFolderAndUninstaller_AndWhetherTheReadWasWhole()
    {
        // A hidden driver's folder is exactly what a leftover must not be, so hidden entries are passed on too.
        var uninstaller = @"""C:\Program Files\Acme\Driver\uninst.exe"" /S";
        var entry = new UninstallEntry("Acme Audio Driver", "Acme", @"C:\Program Files\Acme\Driver", uninstaller,
            IsSystemComponent: true, IsUpdate: false);
        var env = new SystemLeftoverEnvironment(() => new UninstallList([entry], Complete: false));

        var (apps, complete) = env.RegisteredApps();

        Assert.False(complete);
        Assert.Equal(new UninstallProbe("Acme Audio Driver", "Acme", "", @"C:\Program Files\Acme\Driver", uninstaller),
            Assert.Single(apps));
    }

    /// <summary>
    /// Runs <paramref name="read"/> while this user is denied reading <paramref name="subKey"/>, and checks the deny
    /// really applied, so a test of an unreadable key cannot pass having read it.
    /// </summary>
    private T WhileUnreadable<T>(string subKey, Func<T> read)
    {
        using var key = _root.OpenSubKey(subKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
        using var identity = WindowsIdentity.GetCurrent();
        var deny = new RegistryAccessRule(identity.User!,
            RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys, AccessControlType.Deny);
        var security = key.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(deny);
        key.SetAccessControl(security);
        try
        {
            Assert.Throws<System.Security.SecurityException>(() => _root.OpenSubKey(subKey)?.Dispose());
            return read();
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            key.SetAccessControl(security);
        }
    }
}
