// SysManager · LeftoverFinder — what an uninstall left behind, and how sure that is
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>What a leftover search found to offer, and what it found and deliberately did not offer.</summary>
public sealed record LeftoverFindings(IReadOnlyList<LeftoverItem> Items, IReadOnlyList<string> NotOffered);

/// <summary>
/// Finds what an uninstalled app left behind, most certain first, and refuses anything that could belong to Windows,
/// to the user's own files or to another app (#1527).
/// </summary>
/// <remarks>
/// Name-based matching is the whole hazard: a folder called "Google" holds Chrome's profile as well as whatever was
/// just uninstalled. So the matching is exact and conservative, every source is ranked, and only the install folder
/// the app's own uninstall entry named arrives ticked.
/// <list type="number">
/// <item>The install location, read before uninstalling: <see cref="LeftoverConfidence.Certain"/>.</item>
/// <item>A folder named exactly after the app, directly under AppData, Local AppData or ProgramData:
/// <see cref="LeftoverConfidence.Probably"/>.</item>
/// <item>The app's own key, <c>HKCU\Software\&lt;Publisher&gt;\&lt;App&gt;</c>: <see cref="LeftoverConfidence.OwnKey"/>.</item>
/// <item>A folder named after the publisher, directly under AppData or Local AppData, only when no app still
/// installed has that publisher: <see cref="LeftoverConfidence.Guess"/>. Never under ProgramData, where a
/// publisher's folder is usually shared by its drivers and services.</item>
/// </list>
/// <para>"Still installed" is the Uninstaller's list AND every uninstall entry Windows holds, hidden ones included:
/// the list leaves out drivers and runtimes, and those are exactly what a vendor folder is often shared with.</para>
/// <para>Never offered: anything inside Windows; the roots themselves (a drive, Program Files, ProgramData, the
/// profile, the AppData folders); Documents, Desktop, Downloads, Pictures, Music and Videos and everything in them;
/// SysManager's own folders; a folder that holds one of those; a folder Windows and many apps share, such as
/// <c>Common Files</c>; anything not on one of this PC's own fixed drives, which have no Recycle Bin to put it
/// back from; a link or anything reached through one; a folder an app still installed lives in, or that holds one;
/// and a name another installed app also answers to. A match that is refused is still reported, as a sentence, so
/// the user knows it was seen.</para>
/// <para>The File Shredder's protected list is not reused, on purpose: it refuses everything under Program Files,
/// which is where the install folders this exists to offer usually are.</para>
/// </remarks>
public static partial class LeftoverFinder
{
    /// <summary>
    /// Names Windows and too many apps share to be anybody's in particular, never offered as a folder or a key name.
    /// </summary>
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Windows", "Packages", "Programs", "Temp", "Common Files",
        "Package Cache", "Installer", "Classes", "Policies", "Wow6432Node", "Software",
    };

    /// <summary>
    /// Names too generic to say which app a folder belonged to. An install folder's own name is one of the names an
    /// app answers to, and <c>C:\Program Files\App\bin</c> would otherwise send the search after any "bin" folder.
    /// </summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "app", "apps", "application", "program", "client", "current", "data", "files", "common", "shared",
        "tools", "update", "updater", "x64", "x86", "win32", "win64",
    };

    /// <summary>Finds what <paramref name="probe"/>'s app left behind.</summary>
    /// <param name="probe">What was known about the app before it was uninstalled.</param>
    /// <param name="env">Where to look, and what is never offered.</param>
    /// <param name="stillInstalled">
    /// The apps still on the Uninstaller's list. Every uninstall entry <paramref name="env"/> reports is added to it.
    /// </param>
    public static LeftoverFindings Find(UninstallProbe probe, ILeftoverEnvironment env,
                                        IReadOnlyCollection<UninstallProbe> stillInstalled)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(stillInstalled);

        var others = OthersStillInstalled(stillInstalled, env);
        var items = new List<LeftoverItem>();
        var notOffered = new List<string>();
        var names = AppNames(probe);
        var publishers = PublisherNames(probe, env);
        // Only folders that resolve to a full path: an empty one would turn every name into a path relative to
        // wherever SysManager happens to be running.
        var appHomes = new[] { env.RoamingAppData, env.LocalAppData, env.ProgramData }
            .Select(Normalize).OfType<string>().ToArray();
        var publisherHomes = new[] { env.RoamingAppData, env.LocalAppData }
            .Select(Normalize).OfType<string>().ToArray();

        // 1. The folder the uninstall entry named. Not looked at at all unless it is on one of this PC's own
        // drives: a path on a network share can take a minute to answer, and has no Recycle Bin anyway.
        if (Normalize(probe.InstallLocation) is { } installLocation
            && IsOnOwnFixedDrive(installLocation, env)
            && Directory.Exists(installLocation))
        {
            AddFolder(items, notOffered, installLocation, LeftoverConfidence.Certain, "",
                Refusal(installLocation, env) ?? SharedWith(installLocation, others, env), env);
        }

        // 2. A folder named exactly after the app. Each path is normalized as it is built, so "App." and "App " name
        // the folder Windows would open, and the location shown is the one that is removed.
        foreach (var home in appHomes)
        {
            foreach (var name in names)
            {
                if (Normalize(Path.Combine(home, name)) is not { } path || !Directory.Exists(path)) continue;
                var why = Refusal(path, env)
                    ?? SharedWith(path, others, env)
                    ?? (IsShared(name, others) ? "may belong to another installed app" : null);
                AddFolder(items, notOffered, path, LeftoverConfidence.Probably, "", why, env);
            }
        }

        // The user's own folders are searched only to say what was seen there and left alone.
        foreach (var (folderName, folderPath) in env.UserFolders)
        {
            if (Normalize(folderPath) is not { } folder) continue;
            foreach (var name in names)
            {
                if (Normalize(Path.Combine(folder, name)) is { } path && Directory.Exists(path))
                    notOffered.Add($"Not offered: {path} is in {folderName}, which SysManager never offers.");
            }
        }

        // 3. The app's own registry key.
        foreach (var publisher in publishers)
        {
            foreach (var name in names)
            {
                var key = $@"Software\{publisher}\{name}";
                if (!IsSafeKeyPath(key) || !env.RegistryKeyExists(key)) continue;
                if (items.Any(i => i.Kind == LeftoverKind.RegistryKey
                                   && string.Equals(i.Location, key, StringComparison.OrdinalIgnoreCase))) continue;
                if (IsShared(name, others))
                {
                    notOffered.Add($@"Not offered: HKEY_CURRENT_USER\{key} may belong to another installed app.");
                    continue;
                }
                items.Add(new LeftoverItem
                {
                    Location = key,
                    Kind = LeftoverKind.RegistryKey,
                    Confidence = LeftoverConfidence.OwnKey,
                });
            }
        }

        // 4. A folder named after the publisher.
        foreach (var home in publisherHomes)
        {
            foreach (var publisher in publishers)
            {
                if (Normalize(Path.Combine(home, publisher)) is not { } path || !Directory.Exists(path)) continue;
                var why = Refusal(path, env)
                    ?? SharedWith(path, others, env)
                    ?? (IsShared(publisher, others) ? $"is shared with another installed {publisher} app" : null);
                AddFolder(items, notOffered, path, LeftoverConfidence.Guess, publisher, why, env);
            }
        }

        return new LeftoverFindings(items, notOffered);
    }

    /// <summary>
    /// The apps that still count as installed: the Uninstaller's list, and every uninstall entry Windows holds.
    /// </summary>
    internal static IReadOnlyList<UninstallProbe> OthersStillInstalled(
        IReadOnlyCollection<UninstallProbe> stillInstalled, ILeftoverEnvironment env) =>
        [.. stillInstalled, .. env.RegisteredApps()];

    /// <summary>
    /// Why <paramref name="path"/> is never offered, as the end of a sentence, or null when it may be.
    /// </summary>
    /// <remarks>
    /// Run again on every item just before it is removed, so a folder replaced by a link after the search found it is
    /// still refused. The checks that need no disk come first, so a path on a network share is turned down before
    /// anything tries to reach it.
    /// </remarks>
    public static string? Refusal(string path, ILeftoverEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (IsNetworkOrDevicePath(path)) return "is not on one of this PC's own drives";
        if (Normalize(path) is not { } full) return "is not a folder SysManager can read";
        if (IsDriveRoot(full)) return "is the root of a drive";
        if (!IsOnOwnFixedDrive(full, env)) return "is not on one of this PC's own drives";

        if (SystemPaths.IsInsideSubtree(full, Normalize(env.WindowsDirectory)))
            return "is inside Windows, which SysManager never offers";

        foreach (var (name, folder) in env.UserFolders)
        {
            if (Normalize(folder) is { } userFolder && SystemPaths.IsInsideSubtree(full, userFolder))
                return $"is in {name}, which SysManager never offers";
        }

        foreach (var own in env.OwnFolders)
        {
            if (Normalize(own) is { } ownFolder && SystemPaths.IsInsideSubtree(full, ownFolder))
                return "belongs to SysManager";
        }

        // A folder that IS a root, or holds a root, the user's folders or SysManager: removing it removes them.
        foreach (var protectedFolder in ProtectedFolders(env))
        {
            if (SystemPaths.IsInsideSubtree(protectedFolder, full))
                return "holds folders SysManager never offers";
        }

        if (SharedNames.Contains(Path.GetFileName(full)))
            return "is a folder Windows and many apps share";

        if (SafeFileWalk.IsReparsePoint(full))
            return "is a link to another folder";
        if (FileShredderService.ResolveFinalPath(full) is { } final
            && Normalize(final) is { } resolved
            && !string.Equals(resolved, full, StringComparison.OrdinalIgnoreCase))
            return "is reached through a link";

        return null;
    }

    /// <summary>Whether removing <paramref name="path"/> needs administrator rights SysManager does not have now.</summary>
    internal static bool NeedsAdministrator(string path, ILeftoverEnvironment env) =>
        !env.IsElevated
        && Normalize(path) is { } full
        && (SystemPaths.IsInsideSubtree(full, Normalize(env.ProgramFiles))
            || SystemPaths.IsInsideSubtree(full, Normalize(env.ProgramFilesX86))
            || SystemPaths.IsInsideSubtree(full, Normalize(env.ProgramData)));

    /// <summary>
    /// Why <paramref name="path"/> belongs to an app still installed, or null: that app's install folder or the
    /// folder its uninstaller sits in is <paramref name="path"/>, is inside it, or holds it.
    /// </summary>
    internal static string? SharedWith(string path, IReadOnlyCollection<UninstallProbe> others, ILeftoverEnvironment env)
    {
        foreach (var other in others)
        {
            foreach (var home in HomesOf(other, env))
            {
                if (SystemPaths.IsInsideSubtree(home, path) || SystemPaths.IsInsideSubtree(path, home))
                    return "is also where another installed app lives";
            }
        }
        return null;
    }

    /// <summary>
    /// The size of <paramref name="items"/> without counting a folder twice: one inside another listed folder is
    /// already in that folder's size.
    /// </summary>
    internal static long DistinctBytes(IReadOnlyCollection<LeftoverItem> items)
    {
        var folders = items.Where(i => i.Kind == LeftoverKind.Folder).ToList();
        return folders
            .Where(f => !folders.Any(outer => !ReferenceEquals(outer, f)
                                              && !string.Equals(outer.Location, f.Location, StringComparison.OrdinalIgnoreCase)
                                              && SystemPaths.IsInsideSubtree(f.Location, outer.Location)))
            .DistinctBy(f => f.Location, StringComparer.OrdinalIgnoreCase)
            .Sum(f => f.SizeBytes);
    }

    /// <summary>
    /// The names an app answers to: its display name, the part of its winget id after the last dot, and its install
    /// folder's own name. "VLC media player", <c>VideoLAN.VLC</c> and <c>C:\Program Files\VideoLAN\VLC</c> give
    /// "VLC media player" and "VLC".
    /// </summary>
    internal static IReadOnlyList<string> AppNames(UninstallProbe probe)
    {
        var names = new List<string>();
        AddName(names, probe.Name);
        AddName(names, IdPart(probe.Id, last: true));
        AddName(names, LeafOf(probe.InstallLocation));
        return names;
    }

    /// <summary>
    /// The names its publisher answers to: the uninstall entry's publisher, the part of the winget id before the first
    /// dot, and the folder its install folder sits in, unless that folder is a root.
    /// </summary>
    internal static IReadOnlyList<string> PublisherNames(UninstallProbe probe, ILeftoverEnvironment env)
    {
        var names = new List<string>();
        AddName(names, probe.Publisher);
        AddName(names, IdPart(probe.Id, last: false));
        if (Normalize(probe.InstallLocation) is { } location
            && Normalize(Path.GetDirectoryName(location)) is { } parent
            && !IsDriveRoot(parent)
            && !ProtectedFolders(env).Any(p => string.Equals(p, parent, StringComparison.OrdinalIgnoreCase)))
        {
            AddName(names, Path.GetFileName(parent));
        }
        return names;
    }

    /// <summary>
    /// Whether <paramref name="name"/> also belongs to an app still installed: it appears, as whole words, in that
    /// app's name or publisher, or is one of the segments of its winget id.
    /// </summary>
    internal static bool IsShared(string name, IReadOnlyCollection<UninstallProbe> stillInstalled)
    {
        var words = Words(name);
        if (words.Count == 0) return true;
        foreach (var other in stillInstalled)
        {
            if (ContainsWords(Words(other.Name), words) || ContainsWords(Words(other.Publisher), words)) return true;
            if (other.Id.Split('.').Any(segment => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>Whether a key path is one SysManager may pass to <c>reg.exe</c> and delete.</summary>
    /// <remarks>
    /// Exactly <c>Software\&lt;Publisher&gt;\&lt;App&gt;</c>, with no quote or NUL that could break out of the
    /// argument, the same rule <see cref="ContextMenuService.IsSafeRegistryPath"/> holds Context Menu to.
    /// </remarks>
    internal static bool IsSafeKeyPath(string key)
    {
        if (!ContextMenuService.IsSafeRegistryPath(key)) return false;
        var parts = key.Split('\\');
        return parts.Length == 3
               && string.Equals(parts[0], "Software", StringComparison.OrdinalIgnoreCase)
               && parts[1].Length > 0 && parts[2].Length > 0
               && !SharedNames.Contains(parts[1]) && !SharedNames.Contains(parts[2]);
    }

    /// <summary>Whether <paramref name="full"/> is on a fixed drive of this PC, the only kind with a Recycle Bin.</summary>
    internal static bool IsOnOwnFixedDrive(string full, ILeftoverEnvironment env) =>
        !IsNetworkOrDevicePath(full)
        && Path.GetPathRoot(full) is { Length: > 0 } root
        && env.DriveTypeOf(root) == DriveType.Fixed;

    private static void AddFolder(List<LeftoverItem> items, List<string> notOffered, string path,
                                  LeftoverConfidence confidence, string publisher, string? refusal, ILeftoverEnvironment env)
    {
        // A folder already listed, or inside one that is, adds nothing: removing the outer one removes it.
        if (items.Any(i => i.Kind == LeftoverKind.Folder && SystemPaths.IsInsideSubtree(path, i.Location))) return;

        if (refusal is not null)
        {
            // Said once per folder: the install folder refused as Certain comes round again by its name.
            var said = $"Not offered: {path} ";
            if (!notOffered.Any(n => n.StartsWith(said, StringComparison.OrdinalIgnoreCase)))
                notOffered.Add($"{said}{refusal}.");
            return;
        }

        var needsAdmin = NeedsAdministrator(path, env);
        items.Add(new LeftoverItem
        {
            Location = path,
            Kind = LeftoverKind.Folder,
            Confidence = confidence,
            Publisher = publisher,
            NeedsAdministrator = needsAdmin,
            IsSelected = confidence == LeftoverConfidence.Certain && !needsAdmin,
        });
    }

    /// <summary>The folders an app lives in: its install folder and the folder its uninstaller sits in.</summary>
    /// <remarks>
    /// Only a folder that is the app's own counts. Some entries name a root as their install location, such as
    /// <c>C:\Program Files</c>, or keep their uninstaller in Windows' own folders; taken at their word, one such
    /// entry would make every install folder under that root look shared, and nothing would ever be offered.
    /// </remarks>
    private static IEnumerable<string> HomesOf(UninstallProbe app, ILeftoverEnvironment env)
    {
        foreach (var home in new[] { Normalize(app.InstallLocation), UninstallerFolder(app.UninstallCommand) })
        {
            if (home is null || IsDriveRoot(home) || SharedNames.Contains(Path.GetFileName(home))) continue;
            if (SystemPaths.IsInsideSubtree(home, Normalize(env.WindowsDirectory))) continue;
            if (ProtectedFolders(env).Any(p => string.Equals(p, home, StringComparison.OrdinalIgnoreCase))) continue;
            yield return home;
        }
    }

    /// <summary>The folder an uninstall command's executable sits in, or null when it names none.</summary>
    /// <remarks>
    /// Parsed by the rule the Uninstaller runs the command with, so both agree on which executable it names.
    /// <c>MsiExec</c> and <c>rundll32</c> come back as bare names and name no folder of the app's own.
    /// </remarks>
    private static string? UninstallerFolder(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            var (exe, _) = UninstallerService.ParseUninstallCommand(command);
            return Normalize(Path.GetDirectoryName(exe));
        }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The roots, the user's folders and SysManager's own: what no offered folder may be, or hold.</summary>
    private static IEnumerable<string> ProtectedFolders(ILeftoverEnvironment env)
    {
        // AppData itself and LocalLow sit beside Roaming and Local, and the folder of all profiles above the user's.
        var appData = ParentOf(env.LocalAppData);
        string?[] roots =
        [
            env.RoamingAppData, env.LocalAppData, appData, appData is null ? null : Path.Combine(appData, "LocalLow"),
            env.ProgramData, env.UserProfile, ParentOf(env.UserProfile),
            env.WindowsDirectory, env.ProgramFiles, env.ProgramFilesX86,
        ];
        foreach (var root in roots.Concat(env.UserFolders.Select(f => f.Path)).Concat(env.OwnFolders))
        {
            if (Normalize(root) is { } full) yield return full;
        }
    }

    private static string? ParentOf(string? path) =>
        Normalize(path) is { } full ? Path.GetDirectoryName(full) : null;

    /// <summary>A UNC share (<c>\\server\share</c>) or a device path (<c>\\?\</c>, <c>\\.\</c>): never this PC's own drive.</summary>
    private static bool IsNetworkOrDevicePath(string? path) =>
        path is not null && (path.TrimStart().StartsWith(@"\\", StringComparison.Ordinal)
                             || path.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static bool IsDriveRoot(string full) =>
        Path.GetPathRoot(full) is { } root
        && string.Equals(root.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void AddName(List<string> names, string? candidate)
    {
        var name = candidate?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 2) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") return;
        if (SharedNames.Contains(name) || GenericNames.Contains(name)) return;
        // A version number is not a name: "app-1.0.9013" sits under the app's own folder, "1.0.9013" says nothing.
        if (!name.Any(char.IsLetter)) return;
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }

    private static string? IdPart(string? id, bool last)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.Contains('.')) return null;
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : last ? parts[^1] : parts[0];
    }

    private static string? LeafOf(string? path) =>
        Normalize(path) is { } full && !IsDriveRoot(full) ? Path.GetFileName(full) : null;

    /// <summary>
    /// The path in full, with short 8.3 names expanded and no trailing separator, or null. A network or device path
    /// is null too: it is never offered, and expanding a short name would reach out to the share.
    /// </summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsNetworkOrDevicePath(path)) return null;
        try
        {
            var trimmed = path.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed)) return null;
            return Path.TrimEndingDirectorySeparator(FileShredderService.ExpandShortPath(Path.GetFullPath(trimmed)));
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static List<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant())];

    /// <summary>A run of letters or digits: "VideoLAN, Inc." is the two words "videolan" and "inc".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial System.Text.RegularExpressions.Regex WordPattern();

    private static bool ContainsWords(List<string> haystack, List<string> needle)
    {
        if (needle.Count == 0 || haystack.Count < needle.Count) return false;
        for (var start = 0; start <= haystack.Count - needle.Count; start++)
        {
            var match = true;
            for (var i = 0; i < needle.Count && match; i++) match = haystack[start + i] == needle[i];
            if (match) return true;
        }
        return false;
    }
}
