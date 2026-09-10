// SysManager · SecurityPromiseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;

namespace SysManager.Tests;

/// <summary>
/// One guard per promise in <c>SECURITY.md</c>'s "By design — forbidden" list.
/// </summary>
/// <remarks>
/// Those seven statements are the strongest claims the project makes to someone deciding whether to run a
/// privileged utility, and until now not one of them was enforced by anything. A refactor that added a
/// registry delete to the cleanup engine, or an <c>HttpClient</c> to a diagnostics path, would have shipped
/// green while falsifying a published security promise. One of the seven was already a documented manual
/// step — Gate-REVIEW says to grep the diff for network calls in any diagnostics or export path — which is
/// exactly the kind of rule that survives only as long as someone remembers it.
/// <para><b>Every guard here carries a measured floor</b>, because every one is a "no match" assertion and
/// a no-match assertion is what passes silently when its pattern rots. The floors are counts re-derived
/// against the current source; if one stops being met the guard fails and says the detection broke, rather
/// than reporting clean.</para>
/// <para><b>Comments are stripped before any code assertion.</b> This codebase documents what it
/// deliberately does NOT touch, naming the very files and paths these guards forbid — the password-store
/// filenames appear only inside such comments. A guard reading them as code would fail on the explanation
/// of its own rule.</para>
/// </remarks>
public class SecurityPromiseTests
{
    private const string Forbidden = "SECURITY.md, \"By design — forbidden\"";

    // ── 1. No reading of any browser password store ──

    /// <summary>
    /// Promise: "Reading or exfiltrating saved passwords or any browser password store."
    /// </summary>
    /// <remarks>
    /// The filenames are the whole test: <c>Login Data</c> and <c>Web Data</c> are Chromium's credential
    /// stores, <c>logins.json</c> and <c>key4.db</c> are Firefox's. Any code path naming one is reading a
    /// password store, whatever it means to do with it. They currently appear only in comments explaining
    /// which folder the Browser Cleaner stops short of, which is why comments come out first.
    /// </remarks>
    [Fact]
    public void NoCode_NamesABrowserPasswordStore()
    {
        var offenders = new List<string>();
        var files = 0;

        foreach (var (path, code) in AppSources())
        {
            files++;
            foreach (var store in new[] { "Login Data", "Web Data", "logins.json", "key4.db", "signons" })
            {
                if (code.Contains(store, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(path)} names \"{store}\"");
            }
        }

        Assert.True(files >= 200, $"only {files} source files were read — the sweep is not seeing the app");
        Assert.True(offenders.Count == 0,
            $"{Forbidden} says the app never reads a browser password store. These code paths name one:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 2. The Deep Cleanup engine never touches browser data ──

    /// <summary>
    /// Promise: "The Deep Cleanup engine touching browser data."
    /// </summary>
    /// <remarks>
    /// Only the dedicated Browser Cleaner may name a browser's own profile folder. The Deep Cleanup engine
    /// legitimately mentions browsers in its category *descriptions* — Steam and Epic both ship an embedded
    /// browser whose cache it clears — so the assertion is on the profile paths that identify user data,
    /// not on the word "browser".
    /// </remarks>
    [Fact]
    public void TheDeepCleanupEngine_NamesNoBrowserProfileFolder()
    {
        var code = CodeOf("Services", "DeepCleanupService.cs");

        string[] profiles =
        [
            @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware",
            @"Mozilla\Firefox\Profiles", @"Opera Software", @"Vivaldi\User Data",
        ];

        var offenders = profiles.Where(p => code.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();

        // Floor: the file must still be the engine, not a stub that trivially names nothing.
        Assert.True(code.Length > 10_000,
            $"DeepCleanupService.cs is {code.Length} chars of code — too small to be the engine");
        Assert.True(offenders.Count == 0,
            $"{Forbidden} says the Deep Cleanup engine never touches browser data; only the Browser Cleaner "
            + $"tab does. The engine now names:\n  {string.Join("\n  ", offenders)}");
    }

    // ── 3. No registry writes for cleanup ──

    /// <summary>
    /// Promise: "Touching the Windows registry for cleanup."
    /// </summary>
    /// <remarks>
    /// The app deletes registry values in fourteen places and every one is a feature undoing its own
    /// change — the app blocker releasing an IFEO key, a context-menu entry being re-enabled, an
    /// environment variable removed, a notification toggle restored. None of that is cleanup, and the
    /// distinction is the promise: a cleaner that edits the registry is the class of tool this project
    /// deliberately is not.
    /// <para>The floor is the fourteen legitimate sites. If it drops, either the detection broke or a
    /// feature lost its undo — both worth failing on.</para>
    /// </remarks>
    [Fact]
    public void NoCleanupService_DeletesFromTheRegistry()
    {
        var deleteSites = 0;
        var offenders = new List<string>();

        foreach (var (path, code) in AppSources())
        {
            var name = Path.GetFileName(path);
            var deletes = Regex.Matches(code, @"\.Delete(?:SubKey|Value)(?:Tree)?\s*\(").Count;
            deleteSites += deletes;

            if (deletes > 0 && (name.Contains("Cleanup", StringComparison.Ordinal)
                                || name.Contains("Cleaner", StringComparison.Ordinal)))
            {
                offenders.Add($"{name} deletes {deletes} registry key(s) or value(s)");
            }
        }

        Assert.True(deleteSites >= 12,
            $"only {deleteSites} registry-delete sites found, out of 14 measured — the pattern has stopped "
            + "matching, so this guard is no longer looking at anything.");
        Assert.True(offenders.Count == 0,
            $"{Forbidden} says the app never touches the registry for cleanup:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 4. Never a game's own files ──

    /// <summary>
    /// Promise: "Deleting game files … never touches <c>steamapps\common</c>".
    /// </summary>
    /// <remarks>
    /// <c>steamapps</c> is the folder that holds both: <c>steamapps\shadercache</c> is a rebuildable cache
    /// and <c>steamapps\common</c> is where the games themselves live. One character of difference between
    /// clearing a cache and deleting somebody's install, so every use of that path segment is checked
    /// rather than the presence of the word.
    /// </remarks>
    [Fact]
    public void EverySteamappsPath_StopsAtACacheFolder()
    {
        var uses = 0;
        var offenders = new List<string>();

        foreach (var (path, code) in AppSources())
        {
            foreach (var m in Regex.Matches(code, @"steamapps""?\s*,\s*""(?<next>[^""]+)""").Cast<Match>())
            {
                uses++;
                var next = m.Groups["next"].Value;
                if (!next.Contains("cache", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(path)} builds steamapps\\{next}");
            }
        }

        Assert.True(uses >= 2,
            $"only {uses} steamapps path builds found, out of 2 measured — the pattern stopped matching. "
            + "A third occurrence lives in a category description and is prose, not a path.");
        Assert.True(offenders.Count == 0,
            $"{Forbidden} says the cleanup engine never touches steamapps\\common or an installed game:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 5. The Large Files scan cannot delete ──

    /// <summary>
    /// Promise: "Deleting from the Large Files scan … intentionally read-only, even with admin rights."
    /// </summary>
    /// <remarks>
    /// The one feature in the app that shows a list of the user's biggest files and deliberately offers no
    /// action on them. A delete command wired to that list is a one-line change and exactly the kind a
    /// reviewer would read as an obvious convenience.
    /// </remarks>
    [Fact]
    public void NothingDeletes_FromTheLargeFilesScan()
    {
        var mentions = 0;
        var offenders = new List<string>();

        foreach (var (path, code) in AppSources())
        {
            foreach (var line in code.Split('\n'))
            {
                if (!line.Contains("LargeFile", StringComparison.Ordinal)) continue;
                mentions++;

                if (Regex.IsMatch(line, @"\b(Delete|Remove|Shred|Recycle)\w*\s*\("))
                    offenders.Add($"{Path.GetFileName(path)}: {line.Trim()}");
            }
        }

        Assert.True(mentions >= 10,
            $"only {mentions} LargeFile references found — the scan appears to have been renamed, so move "
            + "this guard with it rather than leaving it matching nothing.");
        Assert.True(offenders.Count == 0,
            $"{Forbidden} says the Large Files scan is read-only even with admin rights:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 6. No telemetry, and a closed set of things that may reach the network ──

    /// <summary>
    /// Promise: "Sending telemetry or contacting any server other than the ones needed for an explicit
    /// user action."
    /// </summary>
    /// <remarks>
    /// <b>The most valuable guard here, and the one that replaces a manual review step.</b> Five files may
    /// open an outbound connection, and each maps onto an action already listed in <c>SECURITY.md</c>'s
    /// "When the app uses the network": app icons (off by default), ping, the speed test, traceroute, and
    /// the update check. A sixth file acquiring one of these APIs fails the build until it is added here on
    /// purpose and that document is updated in the same change.
    /// <para>The second assertion is the point of the first: no diagnostics, report, export, crash or log
    /// path may appear in the set. That is the shape a well-meaning "upload a diagnostics bundle" feature
    /// takes, and it is the one claim the product's identity rests on.</para>
    /// <para>Scoped to APIs that actually reach out. Three further files reference
    /// <c>System.Net.Sockets</c> without opening anything — two for the <c>AddressFamily</c> enum while
    /// reading local adapter configuration, one to catch <c>SocketException</c> — so a namespace-level
    /// check would name files that contact nothing and teach the reader to widen the allowlist.</para>
    /// </remarks>
    [Fact]
    public void OnlyTheDocumentedFeatures_CanReachTheNetwork()
    {
        string[] allowed =
        [
            "AppIconService.cs",      // Bulk Installer icons, off by default
            "PingMonitorService.cs",  // ping
            "SpeedTestService.cs",    // speed test
            "TracerouteService.cs",   // traceroute
            "UpdateService.cs",       // version check + update download
        ];

        var capable = new List<string>();

        foreach (var (path, code) in AppSources())
        {
            if (Regex.IsMatch(code, @"new HttpClient\(|new Ping\(|\bDns\.Get|new Socket\(|new WebClient\(|WebRequest\."))
                capable.Add(Path.GetFileName(path));
        }

        Assert.True(capable.Count >= 4,
            $"only {capable.Count} outbound-capable files found, out of 5 measured — the pattern stopped "
            + "matching, so an unlisted network call would now pass unnoticed.");

        var unlisted = capable.Except(allowed, StringComparer.Ordinal).OrderBy(f => f).ToList();
        Assert.True(unlisted.Count == 0,
            $"{Forbidden} says the app contacts no server except for an explicit user action. These files "
            + "can now open a connection and are not in the documented set — add them here AND to "
            + $"SECURITY.md's \"When the app uses the network\", or do not make the call:\n  "
            + string.Join("\n  ", unlisted));

        // The claim the product's identity rests on: nothing that gathers or ships diagnostics is in the set.
        var diagnostic = capable
            .Where(f => Regex.IsMatch(f, "Diagnos|Report|Export|Crash|Log|Telemetr", RegexOptions.IgnoreCase))
            .ToList();
        Assert.True(diagnostic.Count == 0,
            "a diagnostics, report, export or logging path can now reach the network. That is the shape a "
            + $"telemetry feature takes, and {Forbidden} forbids it outright:\n  "
            + string.Join("\n  ", diagnostic));
    }

    // ── 7. No silent elevation ──

    /// <summary>
    /// Promise: "Elevating silently — every admin action … uses the standard <c>runas</c> UAC prompt."
    /// </summary>
    /// <remarks>
    /// Exactly one place in the app asks for elevation, which is what makes the promise auditable at all: a
    /// second one would be a second policy about when a UAC prompt appears, and nobody would know which
    /// governs. The guard is therefore on the count, not on the absence.
    /// </remarks>
    [Fact]
    public void ExactlyOnePlace_AsksForElevation()
    {
        var sites = new List<string>();

        foreach (var (path, code) in AppSources())
        {
            var count = Regex.Matches(code, @"Verb\s*=\s*""runas""").Count;
            for (var i = 0; i < count; i++) sites.Add(Path.GetFileName(path));
        }

        Assert.Single(sites);
        Assert.Equal("AdminHelper.cs", sites[0]);
    }

    // ── shared ──

    /// <summary>
    /// Every app source file, with comments removed.
    /// </summary>
    /// <remarks>
    /// String literals are deliberately KEPT: a forbidden path is a string, so stripping them would remove
    /// exactly what these guards look for. Comments are removed because this codebase documents what it
    /// refuses to touch by name, and several of these guards would otherwise fail on the sentence
    /// explaining the rule they enforce.
    /// </remarks>
    private static IEnumerable<(string Path, string Code)> AppSources()
    {
        var app = AppProjectDir();
        foreach (var path in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            yield return (path, StripComments(File.ReadAllText(path)));
        }
    }

    private static string CodeOf(params string[] parts)
    {
        var path = Path.Combine([AppProjectDir(), .. parts]);
        Assert.True(File.Exists(path), $"{string.Join('/', parts)} not found at {path}");
        return StripComments(File.ReadAllText(path));
    }

    private static string StripComments(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline),
                         @"//.*?$", "", RegexOptions.Multiline);

    // Walks up to the app project — source is not copied to the test output.
    private static string AppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Services")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else every assertion above would silently test nothing
        return Path.Combine(dir!.FullName, "SysManager");
    }
}
