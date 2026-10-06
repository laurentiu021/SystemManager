// SysManager · BrowserProfiles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// Every browser profile Browser Cleaner knows how to read — each Chromium browser's profiles, every Opera channel,
/// each Firefox profile — and the name the tab shows for each. Shared by the cleaner and by the extension list
/// (#1526), so the two views find a profile the same way and call it by the same name.
/// <para>The base data directories are injectable so the discovery can be unit-tested against a temp directory
/// tree without touching the real browser profiles.</para>
/// </summary>
internal sealed class BrowserProfiles(string localAppData, string roamingAppData)
{
    /// <summary>
    /// The Chromium browsers with the standard <c>User Data\&lt;profile&gt;</c> layout, in the order the tab lists
    /// them, by the name it shows and the folder their profiles live in under LocalAppData.
    /// </summary>
    /// <remarks>
    /// Vivaldi is plain Chromium with the standard layout, so it needs no special case of its own — the same
    /// expansion that finds a second Chrome profile finds it. Opera is the exception in the family (no profile
    /// segment, two roots) and is listed separately in <see cref="OperaChannels"/>.
    /// </remarks>
    internal static readonly (string Browser, string UserDataRel)[] Chromium =
    [
        ("Google Chrome", @"Google\Chrome\User Data"),
        ("Microsoft Edge", @"Microsoft\Edge\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("Vivaldi", @"Vivaldi\User Data"),
    ];

    /// <summary>
    /// Every Opera channel, as the folder it keeps its profile in and the name the Browser column shows.
    /// </summary>
    /// <remarks>
    /// Opera installs its channels side by side under one <c>Opera Software</c> parent, and each is a
    /// separate product with its own profile — not a profile of another, which is why each gets its own
    /// Browser name rather than the "— <c>profile</c>" suffix a second Chromium profile gets.
    /// <para>Opera GX is the reason this is a list at all: it is the build made for gamers, so it is the
    /// likeliest Opera on the machine of the user this tab's Ping-preset audience describes, and the
    /// channel folder was hard-coded to Stable — so a GX user's cache was invisible to the tab. Beta and
    /// Developer are the same shape and included for completeness; a channel that is not installed costs
    /// nothing, since the cleaner's scan already drops paths that do not exist.</para>
    /// <para>Stable keeps the bare name "Opera" so every row an existing user already sees reads exactly
    /// as it did before.</para>
    /// </remarks>
    internal static readonly (string Folder, string Browser)[] OperaChannels =
    [
        ("Opera Stable", "Opera"),
        ("Opera GX Stable", "Opera GX"),
        ("Opera Beta", "Opera Beta"),
        ("Opera Developer", "Opera Developer"),
    ];

    /// <summary>Where Firefox keeps its profiles, under both LocalAppData and Roaming AppData.</summary>
    internal const string FirefoxProfilesRel = @"Mozilla\Firefox\Profiles";

    /// <summary>
    /// One Chromium browser's profiles that exist on disk: the profile folder's name, the name the Browser column
    /// shows for it, and the suffix its descriptions carry.
    /// <para>
    /// Only "Default" and "Profile N" directories are considered — Chromium keeps plenty of other
    /// folders under <c>User Data</c> (<c>Crashpad</c>, <c>ShaderCache</c>, <c>System Profile</c>, …)
    /// and none of them are user profiles, so matching every subdirectory would point a delete at
    /// paths this tab never advertised. Reparse points are skipped and enumeration failures are
    /// swallowed, matching <see cref="FirefoxProfileFolders"/>.
    /// </para>
    /// <para>
    /// When the browser is not installed this yields nothing, so no rows appear.
    /// </para>
    /// </summary>
    internal IEnumerable<(string Dir, string Display, string Label)> ChromiumProfiles(string browser, string userDataRel) =>
        ChromiumProfileFolders(browser, userDataRel).Where(p => !p.IsLink).Select(p => (p.Dir, p.Display, p.Label));

    /// <summary>
    /// Every profile folder <see cref="ChromiumProfiles"/> considers, named the same way, with the links among them
    /// included and marked: the cleaner never goes through one, and the extension list says it could not read one
    /// rather than leaving it out (#1526).
    /// </summary>
    internal IEnumerable<(string Dir, string Display, string Label, bool IsLink)> ChromiumProfileFolders(
        string browser, string userDataRel)
    {
        var userDataAbs = Path.Combine(localAppData, userDataRel);
        if (!Directory.Exists(userDataAbs) || SafeFileWalk.IsReparsePoint(userDataAbs)) yield break;

        string[] candidates;
        try { candidates = Directory.GetDirectories(userDataAbs); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        // "Default" first, then Profile 1, Profile 2, … so the grid reads in a stable, predictable
        // order instead of whatever order the filesystem returned.
        foreach (var dir in candidates
                     .Select(Path.GetFileName)
                     .Where(name => !string.IsNullOrEmpty(name) && IsProfileFolder(name!))
                     .OrderBy(name => IsDefaultProfile(name!) ? 0 : 1)
                     .ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var isLink = SafeFileWalk.IsReparsePoint(Path.Combine(userDataAbs, dir!));

            // Name the profile in the Browser column so the user can see WHICH Chrome is being
            // cleaned — the thing a flat "Google Chrome" checkbox in other cleaners never tells her.
            // The default profile stays unlabelled, so the common single-profile case reads exactly
            // as it did before and no existing row text changes.
            var isDefault = IsDefaultProfile(dir!);
            yield return (dir!, isDefault ? browser : $"{browser} — {dir}", isDefault ? string.Empty : $" in {dir}", isLink);
        }
    }

    private static bool IsDefaultProfile(string name) =>
        string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for "Default" and "Profile N" (N a positive integer) — the only directory names Chromium
    /// uses for user profiles.
    /// </summary>
    internal static bool IsProfileFolder(string name)
    {
        if (IsDefaultProfile(name)) return true;
        if (!name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) return false;

        var suffix = name["Profile ".Length..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Every Firefox profile on disk: the folder it lives in, the name the Browser column shows for it,
    /// and the suffix its descriptions carry — the same three pieces <see cref="ChromiumProfiles"/>
    /// builds for a Chromium profile, so the two families read alike in the grid.
    /// </summary>
    /// <remarks>
    /// Firefox salts its profile folders (<c>8char.default-release</c>) and the salt means nothing to the
    /// user, so the readable half is what the row shows: <c>Firefox — dev-edition</c>. The release default
    /// keeps the bare name "Firefox", so the single-profile case — every existing user — reads exactly as
    /// it did before.
    /// <para>The name is the row's IDENTITY, not decoration: <c>BrowserCleanerViewModel</c> carries ticks
    /// across a rescan keyed on (Browser, Category), so two rows sharing a name means one profile's choice
    /// is applied to the other (see <c>SelectionCarry</c>). Every case that could produce a duplicate is
    /// therefore resolved here rather than left to chance — a legacy <c>.default</c> sitting beside a
    /// <c>.default-release</c> does not also claim the bare name, and two profiles sharing a readable half
    /// keep their salt to stay apart.</para>
    /// </remarks>
    internal (string Folder, string Display, string Label)[] FirefoxProfiles()
    {
        const string browser = "Firefox";
        var folders = FirefoxProfileFolders();
        if (folders.Length == 0) return [];

        // One profile may own the bare name: the release default, or a legacy ".default" when that is all
        // there is. If two folders tie for it, neither takes it — an ambiguous bare name would be the very
        // collision this naming exists to prevent.
        var bestRank = folders.Min(f => FirefoxDefaultRank(FirefoxProfileLabel(f)));
        var contenders = folders.Where(f => FirefoxDefaultRank(FirefoxProfileLabel(f)) == bestRank).ToArray();
        var defaultFolder = bestRank < NotADefaultProfile && contenders.Length == 1 ? contenders[0] : null;

        // A readable half only survives as the name while it is unique among the rest.
        var shared = folders
            .Where(f => !IsSameFolder(f, defaultFolder))
            .GroupBy(FirefoxProfileLabel, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Default first, then alphabetical, so the grid reads in a stable order however the filesystem
        // returned the folders — the ordering ChromiumProfiles already applies.
        return [.. folders
            .OrderBy(f => IsSameFolder(f, defaultFolder) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(Resolve)];

        (string, string, string) Resolve(string folder)
        {
            if (IsSameFolder(folder, defaultFolder)) return (folder, browser, string.Empty);
            var label = FirefoxProfileLabel(folder);
            var shown = shared.Contains(label) ? folder : label;
            return (folder, $"{browser} — {shown}", $" in {shown}");
        }
    }

    /// <summary>
    /// The folder of the profile Firefox opens when it is started without being told one, as its own
    /// <c>profiles.ini</c> records it: the default each install names (Firefox 67 and later), or else the profile
    /// marked default, which older versions used and newer ones leave behind. <c>Told</c> is false when there is no
    /// such file to ask. The folder is null when the file names none, or names different ones for different
    /// installs, since which of them <c>firefox.exe</c> opens cannot then be known.
    /// </summary>
    internal (bool Told, string? Folder) FirefoxDefaultFolder()
    {
        var bytes = ExtensionFiles.Read(Path.Combine(roamingAppData, @"Mozilla\Firefox\profiles.ini"), ExtensionFiles.MaxManifestBytes);
        if (bytes is null) return (false, null);

        List<string> installDefaults = [];
        List<string> markedDefaults = [];
        var section = "";
        string? path = null;
        var marked = false;

        void EndOfSection()
        {
            if (section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && marked && path is not null)
                markedDefaults.Add(path);
            path = null;
            marked = false;
        }

        foreach (var raw in Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                EndOfSection();
                section = line[1..^1];
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (section.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Default", StringComparison.OrdinalIgnoreCase)) installDefaults.Add(value);
            }
            else if (section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) path = value;
                else if (key.Equals("Default", StringComparison.OrdinalIgnoreCase)) marked = value == "1";
            }
        }
        EndOfSection();

        var folders = (installDefaults.Count > 0 ? installDefaults : markedDefaults)
            .Select(p => p.Replace('\\', '/').TrimEnd('/'))
            .Select(p => p[(p.LastIndexOf('/') + 1)..])
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (true, folders.Count == 1 ? folders[0] : null);
    }

    private static bool IsSameFolder(string folder, string? other) =>
        string.Equals(folder, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The readable half of a salted Firefox profile folder: <c>8char.dev-edition</c> → <c>dev-edition</c>.
    /// A folder with no salt (or nothing after the dot) is its own label.
    /// </summary>
    private static string FirefoxProfileLabel(string folder)
    {
        var dot = folder.IndexOf('.');
        return dot >= 0 && dot < folder.Length - 1 ? folder[(dot + 1)..] : folder;
    }

    private const int NotADefaultProfile = 2;

    /// <summary>
    /// How strong a claim a profile has on the bare name "Firefox". Modern Firefox uses
    /// <c>.default-release</c>; <c>.default</c> is the pre-67 name and often survives as a leftover
    /// beside it, so it ranks below rather than tying with it.
    /// </summary>
    private static int FirefoxDefaultRank(string label) =>
        string.Equals(label, "default-release", StringComparison.OrdinalIgnoreCase) ? 0
        : string.Equals(label, "default", StringComparison.OrdinalIgnoreCase) ? 1
        : NotADefaultProfile;

    /// <summary>
    /// Every Firefox profile folder NAME found under either root, de-duplicated. Reparse points are
    /// skipped and enumeration failures are swallowed, matching <see cref="ChromiumProfiles"/>. Empty
    /// when Firefox isn't installed, so no rows appear. Never returns the Profiles root itself, so a
    /// clean can only ever touch the named targets, never saved logins/bookmarks/prefs.
    /// </summary>
    /// <remarks>
    /// BOTH roots are read because Firefox splits one profile across them — <c>cache2</c> under
    /// LocalAppData, cookies and sessions under Roaming — and the name a profile shows must not depend on
    /// which root it happened to turn up in, or a single profile's rows would split across two names.
    /// A folder that exists in only one root still yields defs for both; the cleaner's scan drops the
    /// paths that do not exist, exactly as it does for a browser that is not installed.
    /// </remarks>
    private string[] FirefoxProfileFolders()
    {
        List<string> folders = [];
        string[] roots = [localAppData, roamingAppData];
        foreach (var root in roots)
        {
            var profilesAbs = Path.Combine(root, FirefoxProfilesRel);
            if (!Directory.Exists(profilesAbs) || SafeFileWalk.IsReparsePoint(profilesAbs)) continue;

            string[] profileDirs;
            try { profileDirs = Directory.GetDirectories(profilesAbs); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            folders.AddRange(profileDirs
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!));
        }

        return [.. folders.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
