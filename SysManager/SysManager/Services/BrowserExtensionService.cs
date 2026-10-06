// SysManager · BrowserExtensionService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Lists the extensions in every browser profile Browser Cleaner reads — each Chromium browser's profiles, every
/// Opera channel and each Firefox profile, found by <see cref="BrowserProfiles"/> exactly as the cleaner finds them —
/// and opens a browser on its own extensions page (#1526). Read-only: see <see cref="ChromiumExtensionReader"/> and
/// <see cref="FirefoxExtensionReader"/> for what is read.
/// </summary>
/// <remarks>
/// The base data directories are injectable so discovery and ordering are unit-tested against a temp directory
/// tree, and so are the launcher and the elevation check, so a test can check what would be started without
/// starting a browser, on any host.
/// </remarks>
public sealed class BrowserExtensionService : IBrowserExtensionService
{
    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly BrowserProfiles _profiles;
    private readonly Func<ProcessStartInfo, bool> _launch;
    private readonly Func<bool> _isElevated;

    /// <summary>Lists the extensions under the given data directories, or the user's own when none are given.</summary>
    /// <param name="launch">Starts a process and says whether it started; the real one unless a test passes its own.</param>
    /// <param name="isElevated">Whether SysManager runs as administrator; the real check unless a test passes its own.</param>
    public BrowserExtensionService(string? localAppData = null, string? roamingAppData = null,
        Func<ProcessStartInfo, bool>? launch = null, Func<bool>? isElevated = null)
    {
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _profiles = new BrowserProfiles(_localAppData, _roamingAppData);
        _launch = launch ?? Launch;
        _isElevated = isElevated ?? AdminHelper.IsElevated;
    }

    /// <summary>How each Chromium browser is started on its extensions page, by the name the tab shows.</summary>
    private static readonly Dictionary<string, (string Executable, string Page)> ChromiumLaunch = new(StringComparer.Ordinal)
    {
        ["Google Chrome"] = ("chrome.exe", "chrome://extensions"),
        ["Microsoft Edge"] = ("msedge.exe", "edge://extensions"),
        ["Brave"] = ("brave.exe", "brave://extensions"),
        ["Vivaldi"] = ("vivaldi.exe", "vivaldi://extensions"),
    };

    // Opera's channels share one name for their program, so starting "opera.exe" could open the wrong one: the list
    // names the page instead. Firefox has one program and starts in its last-used profile.
    internal const string OperaPage = "opera://extensions";
    internal const string FirefoxExecutable = "firefox.exe";
    internal const string FirefoxPage = "about:addons";

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExtensionProfile>> ScanAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ExtensionProfile>>(() =>
        {
            var culture = CultureInfo.CurrentUICulture;
            List<ExtensionProfile> profiles = [];

            foreach (var (browser, userDataRel) in BrowserProfiles.Chromium)
            {
                var (executable, page) = ChromiumLaunch[browser];
                // The browser's whole data folder behind a link: not followed, and said rather than left out.
                if (ExtensionFiles.KindOf(Path.Combine(_localAppData, userDataRel)) is ExtensionFiles.EntryKind.Link
                    or ExtensionFiles.EntryKind.Unreadable)
                {
                    profiles.Add(new ExtensionProfile(browser, browser, page, executable, null, [], CouldNotRead: true));
                    continue;
                }
                foreach (var (dir, display, _, isLink) in _profiles.ChromiumProfileFolders(browser, userDataRel))
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = string.Equals(dir, "Default", StringComparison.OrdinalIgnoreCase) ? null : dir;
                    if (isLink)
                    {
                        profiles.Add(new ExtensionProfile(display, browser, page, executable, directory, [], CouldNotRead: true));
                        continue;
                    }
                    var (extensions, couldNotRead) =
                        ChromiumExtensionReader.Read(Path.Combine(_localAppData, userDataRel, dir), culture);
                    Add(profiles, new ExtensionProfile(display, browser, page, executable, directory,
                        Ordered(extensions), couldNotRead));
                }
            }

            foreach (var (folder, browser) in BrowserProfiles.OperaChannels)
            {
                ct.ThrowIfCancellationRequested();
                var profileDir = Path.Combine(_roamingAppData, "Opera Software", folder);
                switch (ExtensionFiles.KindOf(profileDir))
                {
                    case ExtensionFiles.EntryKind.Folder:
                        var (extensions, couldNotRead) = ChromiumExtensionReader.Read(profileDir, culture);
                        Add(profiles, new ExtensionProfile(browser, browser, OperaPage, null, null, Ordered(extensions), couldNotRead));
                        break;
                    case ExtensionFiles.EntryKind.Link or ExtensionFiles.EntryKind.Unreadable:
                        profiles.Add(new ExtensionProfile(browser, browser, OperaPage, null, null, [], CouldNotRead: true));
                        break;
                }
            }

            // Firefox's profiles all sit under one folder: behind a link, none of them is read through it.
            var firefoxProfiles = Path.Combine(_roamingAppData, BrowserProfiles.FirefoxProfilesRel);
            switch (ExtensionFiles.KindOf(firefoxProfiles))
            {
                case ExtensionFiles.EntryKind.Folder:
                    var (toldDefault, defaultFolder) = _profiles.FirefoxDefaultFolder();
                    foreach (var (folder, display, label) in _profiles.FirefoxProfiles())
                    {
                        ct.ThrowIfCancellationRequested();
                        // Firefox starts in its default profile, so only that one is started from here: started for
                        // another, it would show the wrong profile's add-ons. Its profiles.ini says which; without
                        // one, the profile the tab shows by the bare name is taken for it.
                        var isDefault = toldDefault
                            ? string.Equals(folder, defaultFolder, StringComparison.OrdinalIgnoreCase)
                            : label.Length == 0;
                        var executable = isDefault ? FirefoxExecutable : null;
                        var profileDir = Path.Combine(firefoxProfiles, folder);
                        switch (ExtensionFiles.KindOf(profileDir))
                        {
                            case ExtensionFiles.EntryKind.Folder:
                                var (extensions, couldNotRead) = FirefoxExtensionReader.Read(profileDir);
                                Add(profiles, new ExtensionProfile(display, "Firefox", FirefoxPage, executable, null,
                                    Ordered(extensions), couldNotRead));
                                break;
                            case ExtensionFiles.EntryKind.Link or ExtensionFiles.EntryKind.Unreadable:
                                profiles.Add(new ExtensionProfile(display, "Firefox", FirefoxPage, executable, null, [], CouldNotRead: true));
                                break;
                        }
                    }
                    break;
                case ExtensionFiles.EntryKind.Link or ExtensionFiles.EntryKind.Unreadable:
                    profiles.Add(new ExtensionProfile("Firefox", "Firefox", FirefoxPage, FirefoxExecutable, null, [], CouldNotRead: true));
                    break;
            }

            return profiles;
        }, ct);

    // A profile with nothing in it says nothing, unless what it says is that it could not be read.
    private static void Add(List<ExtensionProfile> profiles, ExtensionProfile profile)
    {
        if (profile.Extensions.Count > 0 || profile.CouldNotRead) profiles.Add(profile);
    }

    /// <summary>
    /// The order the list shows: an extension another program added first — the likeliest answer to "it installed
    /// itself" — then those that can read every website, then by name. One that is turned off keeps its place.
    /// </summary>
    internal static IReadOnlyList<BrowserExtension> Ordered(IEnumerable<BrowserExtension> extensions) =>
        [.. extensions
            .OrderBy(e => e.Origin == ExtensionOrigin.AnotherProgram ? 0 : 1)
            .ThenBy(e => e.CanReadEverySite ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing is started while SysManager runs as administrator. A browser started from here would run as
    /// administrator too, and the program a name like <c>chrome.exe</c> resolves to can be set per user, so an
    /// elevated start would run whatever the user's own settings name with administrator rights. A browser with no
    /// reliable way to be started is answered first: it would not be started either way, and saying it was held back
    /// would explain the wrong thing.
    /// </remarks>
    public ExtensionsPageOpening OpenExtensionsPage(ExtensionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (StartInfoFor(profile) is not { } start) return ExtensionsPageOpening.NotOpened;
        if (_isElevated()) return ExtensionsPageOpening.NotWhileElevated;
        return _launch(start) ? ExtensionsPageOpening.Opened : ExtensionsPageOpening.NotOpened;
    }

    /// <summary>
    /// What starts the profile's browser on its extensions page, or null when it cannot be started that way. Only a
    /// browser and page this service knows, and only a real profile folder name, are passed on: the profile comes
    /// from the caller, and nothing it carries reaches a command line unchecked.
    /// </summary>
    internal static ProcessStartInfo? StartInfoFor(ExtensionProfile profile)
    {
        var known = ChromiumLaunch.Values.Any(l => l.Executable == profile.Executable && l.Page == profile.Page)
                    || (profile.Executable == FirefoxExecutable && profile.Page == FirefoxPage);
        if (!known || profile.Executable is null) return null;
        if (profile.ProfileDirectory is { } dir && (profile.Executable == FirefoxExecutable || !BrowserProfiles.IsProfileFolder(dir)))
            return null;

        var arguments = profile.ProfileDirectory is { } folder
            ? $"--profile-directory=\"{folder}\" {profile.Page}"
            : profile.Page;
        return new ProcessStartInfo(profile.Executable) { Arguments = arguments, UseShellExecute = true };
    }

    private static bool Launch(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
            return true;
        }
        catch (Win32Exception ex) { Log.Debug("Could not start {Program}: {Error}", start.FileName, ex.Message); return false; }
        catch (InvalidOperationException ex) { Log.Debug("Could not start {Program}: {Error}", start.FileName, ex.Message); return false; }
    }
}
