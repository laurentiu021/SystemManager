// SysManager · SignedInAccountTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SignedInAccount"/>: where the signed-in account's own folder and keys are when SysManager runs
/// as another account. The hives are throwaway keys under this account's own <c>HKEY_CURRENT_USER</c>.
/// </summary>
public sealed class SignedInAccountTests : IDisposable
{
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private const string Profile = @"C:\Users\Someone Else";

    private readonly string _rootName = @"Software\SysManagerTests\SignedIn_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _users;
    private readonly RegistryKey _machine;

    public SignedInAccountTests()
    {
        _users = Registry.CurrentUser.CreateSubKey(_rootName + @"\Users", writable: true);
        _machine = Registry.CurrentUser.CreateSubKey(_rootName + @"\Machine", writable: true);
    }

    public void Dispose()
    {
        _users.Dispose();
        _machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false);
    }

    private static SignedInAccount.Account Another(string? sid = Sid) => new(false, sid);

    private void WriteProfile(string? profile = Profile, string? localAppData = @"%USERPROFILE%\AppData\Local")
    {
        if (profile is not null)
        {
            using var entry = _machine.CreateSubKey($@"{SignedInAccount.ProfileListKey}\{Sid}");
            entry.SetValue("ProfileImagePath", profile, RegistryValueKind.ExpandString);
        }
        if (localAppData is not null)
        {
            using var folders = _users.CreateSubKey($@"{Sid}\{SignedInAccount.ShellFoldersKey}");
            folders.SetValue("Local AppData", localAppData, RegistryValueKind.ExpandString);
        }
    }

    [Fact]
    public void LocalAppData_OfAnotherAccount_IsInItsOwnProfile_NotSysManagers()
    {
        WriteProfile();

        var folder = SignedInAccount.LocalAppData(Another(), _users, _machine);

        Assert.Equal(@"C:\Users\Someone Else\AppData\Local", folder);
        Assert.NotEqual(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    [Fact]
    public void LocalAppData_OfAnotherAccount_FollowsItsOwnRedirect()
    {
        WriteProfile(localAppData: @"%userprofile%\Local Stuff");

        Assert.Equal(@"C:\Users\Someone Else\Local Stuff", SignedInAccount.LocalAppData(Another(), _users, _machine));
    }

    [Fact]
    public void LocalAppData_OfAnotherAccount_WithNoShellFolderValue_IsWindowsOwnPlace()
    {
        WriteProfile(localAppData: null);

        Assert.Equal(@"C:\Users\Someone Else\AppData\Local", SignedInAccount.LocalAppData(Another(), _users, _machine));
    }

    [Fact]
    public void LocalAppData_OfAnotherAccount_WithNoProfile_IsUnknown()
    {
        WriteProfile(profile: null);

        Assert.Null(SignedInAccount.LocalAppData(Another(), _users, _machine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a sid")]
    [InlineData(@"S-1-5-21-1\..\S-1-5-18")]
    public void LocalAppData_OfAnAccountThatCannotBeTold_IsUnknown(string? sid)
    {
        WriteProfile();

        Assert.Null(SignedInAccount.LocalAppData(Another(sid), _users, _machine));
    }

    [Fact]
    public void LocalAppData_OfSysManagersOwnAccount_IsItsOwn()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SignedInAccount.LocalAppData(new SignedInAccount.Account(true, null), _users, _machine));
    }

    [Fact]
    public void UserKey_OfAnotherAccount_IsUnderItsHive()
    {
        var key = SignedInAccount.UserKey(Another(), _users, @"Software\Some\Key");

        Assert.NotNull(key);
        Assert.Same(_users, key.Value.Hive);
        Assert.Equal($@"{Sid}\Software\Some\Key", key.Value.Path);
    }

    [Fact]
    public void UserKey_OfSysManagersOwnAccount_IsUnderCurrentUser()
    {
        var key = SignedInAccount.UserKey(new SignedInAccount.Account(true, null), _users, @"Software\Some\Key");

        Assert.NotNull(key);
        Assert.Same(Registry.CurrentUser, key.Value.Hive);
        Assert.Equal(@"Software\Some\Key", key.Value.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a sid")]
    public void UserKey_OfAnAccountThatCannotBeTold_IsUnknown(string? sid)
    {
        Assert.Null(SignedInAccount.UserKey(Another(sid), _users, @"Software\Some\Key"));
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\AppData\Local", @"C:\Users\A", @"C:\Users\A\AppData\Local")]
    [InlineData(@"%USERPROFILE%\AppData\Local", @"C:\Users\A\", @"C:\Users\A\AppData\Local")]
    [InlineData(@"D:\Moved\Local", @"C:\Users\A", @"D:\Moved\Local")]
    [InlineData("", @"C:\Users\A", @"C:\Users\A\AppData\Local")]
    public void Expand_PutsTheAccountsOwnProfileInTheFolder(string value, string profile, string expected)
    {
        Assert.Equal(expected, SignedInAccount.Expand(value, profile));
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\AppData\Local", null)]
    [InlineData(@"%USERPROFILE%\AppData\Local", "Users\\A")]
    [InlineData(@"%HOMEDRIVE%\Local", @"C:\Users\A")]
    [InlineData(@"Relative\Local", @"C:\Users\A")]
    public void Expand_RefusesWhatItCannotNameForThatAccount(string value, string? profile)
    {
        Assert.Null(SignedInAccount.Expand(value, profile));
    }

    [Fact]
    public void Expand_ReadsTheProfileAsWindowsWritesIt()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        Assert.False(string.IsNullOrEmpty(drive), "SystemDrive is not set, so this proves nothing");

        Assert.Equal($@"{drive}\Users\A\AppData\Local", SignedInAccount.Expand(null, @"%SystemDrive%\Users\A"));
    }

    [Theory]
    [InlineData(Sid, true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-1000\\x", false)]
    [InlineData("", false)]
    public void IsSid_TakesOnlyASidAsWindowsWritesIt(string sid, bool expected)
    {
        Assert.Equal(expected, SignedInAccount.IsSid(sid));
    }
}
