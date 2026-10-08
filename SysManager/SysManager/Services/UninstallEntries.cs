// SysManager · UninstallEntries — every uninstall entry Windows holds
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Microsoft.Win32;
using Serilog;

namespace SysManager.Services;

/// <summary>One entry in Windows' list of installed programs, as the registry holds it.</summary>
/// <param name="Name">The DisplayName, or empty for an entry that has none.</param>
/// <param name="Publisher">The Publisher, or empty.</param>
/// <param name="InstallLocation">The InstallLocation, or empty.</param>
/// <param name="UninstallCommand">The UninstallString, or empty.</param>
/// <param name="IsSystemComponent">SystemComponent is 1: a part of something else that Windows' own list hides.</param>
/// <param name="IsUpdate">An update to another program, not a program: it names a parent, or its release type says so.</param>
public sealed record UninstallEntry(string Name, string Publisher, string InstallLocation, string UninstallCommand,
                                    bool IsSystemComponent, bool IsUpdate)
{
    /// <summary>
    /// Whether Windows' own Installed apps list shows it: it has a name, and is neither a hidden component nor an update.
    /// </summary>
    public bool IsListed => Name.Length > 0 && !IsSystemComponent && !IsUpdate;
}

/// <summary>Windows' list of installed programs, as one read found it.</summary>
/// <param name="Entries">Every entry read, the machine's first and then the user's, in the order the registry lists them.</param>
/// <param name="Complete">
/// False when one of the lists could not be read at all, so programs that are installed are missing from
/// <paramref name="Entries"/>. A protected entry, or one removed while it was read, does not count: it is the same at
/// every read, or really gone.
/// </param>
public sealed record UninstallList(IReadOnlyList<UninstallEntry> Entries, bool Complete);

/// <summary>Reads every uninstall entry: the machine's, both 64-bit and 32-bit, and then the current user's.</summary>
/// <remarks>
/// One reader for the uninstall entries that are read whole, so the leftover search and Recent Changes see the same
/// list. The keys are <see cref="UninstallerService.UninstallKey"/> and <see cref="UninstallerService.UninstallKeyWow64"/>,
/// declared once there. A protected entry, or one removed while it is read, is skipped and the rest still count.
/// </remarks>
public static class UninstallEntries
{
    private static readonly (RegistryKey Hive, string Path)[] Keys =
    [
        (Registry.LocalMachine, UninstallerService.UninstallKey),
        (Registry.LocalMachine, UninstallerService.UninstallKeyWow64),
        (Registry.CurrentUser, UninstallerService.UninstallKey),
    ];

    /// <summary>Release types that mark an entry as an update rather than a program, as Windows' own list reads them.</summary>
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Update Rollup", "ServicePack",
    };

    /// <summary>Every entry, machine first and then user, and whether every list there could be read.</summary>
    public static UninstallList ReadAll()
    {
        var entries = new List<UninstallEntry>();
        var complete = true;
        foreach (var (hive, path) in Keys)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = key.OpenSubKey(name);
                        if (entry is not null) entries.Add(Read(entry));
                    }
                    catch (System.Security.SecurityException) { /* one protected entry: the rest still count */ }
                    catch (UnauthorizedAccessException) { /* ditto */ }
                    catch (IOException) { /* deleted while being read */ }
                }
            }
            catch (System.Security.SecurityException ex)
            {
                complete = false;
                Log.Debug("Uninstall entries: {Path} unreadable: {Error}", path, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                complete = false;
                Log.Debug("Uninstall entries: {Path} denied: {Error}", path, ex.Message);
            }
            catch (IOException ex)
            {
                complete = false;
                Log.Debug("Uninstall entries: {Path} failed: {Error}", path, ex.Message);
            }
        }
        return new UninstallList(entries, complete);
    }

    private static UninstallEntry Read(RegistryKey entry) => new(
        Text(entry, "DisplayName"), Text(entry, "Publisher"), Text(entry, "InstallLocation"), Text(entry, "UninstallString"),
        IsSystemComponent: entry.GetValue("SystemComponent") is int component && component == 1,
        IsUpdate: Text(entry, "ParentKeyName").Length > 0 || UpdateReleaseTypes.Contains(Text(entry, "ReleaseType")));

    private static string Text(RegistryKey key, string name) => (key.GetValue(name) as string)?.Trim() ?? "";
}
