// SysManager · SignedInAccount
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Where the account signed in to SysManager's session keeps its own things, when SysManager may run as another one.
/// </summary>
/// <remarks>
/// Started as administrator with another account's password, SysManager runs as that administrator inside the signed-in
/// person's session (#2826): its <c>%LocalAppData%</c> and its <c>HKEY_CURRENT_USER</c> are the administrator's. A tab
/// that watches or keeps the signed-in person's own things reads them from here instead. Each answer is null when the
/// account cannot be told or its folder cannot be read, so the caller says so rather than using the administrator's.
/// </remarks>
public static class SignedInAccount
{
    /// <summary>The key under an account's hive that says where its <c>%LocalAppData%</c> is.</summary>
    internal const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>Where Windows keeps each account's profile folder, by SID.</summary>
    internal const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>Who the signed-in account is, as SysManager sees it.</summary>
    /// <param name="IsSysManagersOwn">SysManager runs as the signed-in account.</param>
    /// <param name="Sid">
    /// The signed-in account's SID when SysManager runs as another; null when it cannot be told.
    /// </param>
    internal readonly record struct Account(bool IsSysManagersOwn, string? Sid);

    /// <summary>The signed-in account, told by <see cref="AdminHelper.RunsAsTheSignedInAccount"/>.</summary>
    internal static Account Who() => AdminHelper.RunsAsTheSignedInAccount()
        ? new Account(true, null)
        : new Account(false, AdminHelper.OwnSessionSid());

    /// <summary>The signed-in account's <c>%LocalAppData%</c>, or null when it cannot be found.</summary>
    public static string? LocalAppData() => LocalAppData(Who(), Registry.Users, Registry.LocalMachine);

    /// <summary>
    /// The signed-in account's own key <paramref name="subKey"/>, as a hive and the path under it: under
    /// <c>HKEY_CURRENT_USER</c> when SysManager runs as that account, under its hive in <c>HKEY_USERS</c> when elevated
    /// as another, null when it cannot be told.
    /// </summary>
    public static (RegistryKey Hive, string Path)? UserKey(string subKey) => UserKey(Who(), Registry.Users, subKey);

    /// <summary><see cref="LocalAppData()"/> for <paramref name="who"/>, over <paramref name="users"/> and
    /// <paramref name="machine"/>.</summary>
    /// <remarks>
    /// Another account's folder is read from its own hive's <c>User Shell Folders</c>, with <c>%USERPROFILE%</c> its
    /// profile folder from the machine's profile list. Expanding the value as it is read would put the administrator's
    /// own profile in it. A hive with no such value has the place Windows gives every account.
    /// </remarks>
    internal static string? LocalAppData(Account who, RegistryKey users, RegistryKey machine)
    {
        if (who.IsSysManagersOwn)
        {
            var own = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return own.Length > 0 ? own : null;
        }
        if (!IsSid(who.Sid)) return null;

        try
        {
            string? value;
            using (var folders = users.OpenSubKey($@"{who.Sid}\{ShellFoldersKey}"))
                value = folders?.GetValue("Local AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    as string;
            string? profile;
            using (var entry = machine.OpenSubKey($@"{ProfileListKey}\{who.Sid}"))
                profile = entry?.GetValue("ProfileImagePath") as string;
            return Expand(value, profile);
        }
        catch (Exception ex)
            when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warning("Signed-in account: its folders could not be read. {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary><see cref="UserKey(string)"/> for <paramref name="who"/>, under <paramref name="users"/>.</summary>
    internal static (RegistryKey Hive, string Path)? UserKey(Account who, RegistryKey users, string subKey)
    {
        if (who.IsSysManagersOwn) return (Registry.CurrentUser, subKey);
        return IsSid(who.Sid) ? (users, $@"{who.Sid}\{subKey}") : null;
    }

    /// <summary>
    /// The folder <paramref name="value"/> names once <c>%USERPROFILE%</c> in it is <paramref name="profile"/>, or null
    /// when the profile is not a full path or the folder names anything else that needs expanding.
    /// </summary>
    /// <remarks>
    /// The profile is expanded as Windows writes it, <c>%SystemDrive%\Users\name</c>, the same for every account.
    /// </remarks>
    internal static string? Expand(string? value, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        profile = Environment.ExpandEnvironmentVariables(profile.Trim());
        if (!Path.IsPathFullyQualified(profile)) return null;

        var raw = string.IsNullOrWhiteSpace(value) ? @"%USERPROFILE%\AppData\Local" : value.Trim();
        var path = raw.Replace("%USERPROFILE%", Path.TrimEndingDirectorySeparator(profile),
                               StringComparison.OrdinalIgnoreCase);
        if (path.Contains('%', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)) return null;
        return Path.GetFullPath(path);
    }

    /// <summary>Whether <paramref name="sid"/> is a SID written as Windows writes one, so it can name a key.</summary>
    internal static bool IsSid(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        try
        {
            return new SecurityIdentifier(sid).Value.Equals(sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
