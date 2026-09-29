// SysManager · DebloaterService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Lists and removes Windows Store (Appx) apps for the current user. All PowerShell
/// runs through the <see cref="IPowerShellRunner"/> seam so listing/parsing can be
/// unit-tested with a substituted runner (Gate-ARCH).
///
/// SAFETY: a hard-coded denylist of system-critical package families (Store, frameworks,
/// security/shell components) is enforced in <see cref="IsProtected"/> — those packages
/// are never offered for removal, even if the catalog or the user selects them. Removal
/// uses the per-user <c>Remove-AppxPackage</c> (no provisioning/-AllUsers), so it is
/// reversible: the user can reinstall any removed app from the Microsoft Store.
/// </summary>
public sealed partial class DebloaterService
{
    private readonly IPowerShellRunner _ps;

    public DebloaterService(IPowerShellRunner ps) => _ps = ps;

    // PackageFullName shape: letters/digits/dot/dash/underscore/tilde, plus we accept the
    // version and publisher-hash segments. Validated before being embedded in a script.
    // \A…\z (absolute anchors): ^…$ would accept a trailing newline before the package
    // name is embedded into the Remove-AppxPackage script.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._~-]{0,255}\z")]
    private static partial Regex PackageFullNameRegex();

    /// <summary>
    /// System-critical package families that must never be removed. Matched by a
    /// case-insensitive prefix on the family/name so version-specific suffixes still match.
    /// </summary>
    private static readonly string[] ProtectedPrefixes =
    [
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",       // winget / App Installer
        "Microsoft.VCLibs",                    // C++ runtime frameworks
        "Microsoft.NET.Native",                // .NET native frameworks
        "Microsoft.UI.Xaml",                   // WinUI framework
        "Microsoft.Services.Store.Engagement",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search",            // search host
        "Microsoft.SecHealthUI",               // Windows Security UI (real package family name)
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.LockApp",
        "Microsoft.CredDialogHost",
        "Microsoft.Win32WebViewHost",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.Photos",            // Photos app — real .Name (not the ...Photos.Settings sub-package)
        "Microsoft.WindowsAppRuntime",
        "MicrosoftWindows.Client",             // client framework family
        "Windows.CBSPreview",
        "Microsoft.Windows.NarratorQuickStart",
        "Microsoft.XboxGameCallableUI",
    ];

    /// <summary>
    /// Curated "commonly removed bloat" families — pre-checked safe items the preset selects.
    /// Each entry maps a name prefix to a friendly label + one-line description.
    /// </summary>
    private static readonly FrozenDictionary<string, (string Display, string Description)> Catalog =
        new Dictionary<string, (string Display, string Description)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.BingNews"] = ("Microsoft News", "Bing-powered news app."),
            ["Microsoft.BingWeather"] = ("Weather", "Bing-powered weather app."),
            ["Microsoft.BingSearch"] = ("Bing Search", "Web search integration."),
            ["Clipchamp.Clipchamp"] = ("Clipchamp", "Video editor bundled with Windows 11."),
            ["Microsoft.GamingApp"] = ("Xbox", "Xbox app and Game Pass storefront."),
            ["Microsoft.XboxGamingOverlay"] = ("Xbox Game Bar", "In-game overlay (Win+G)."),
            ["Microsoft.XboxIdentityProvider"] = ("Xbox Identity Provider", "Xbox sign-in helper."),
            ["Microsoft.XboxSpeechToTextOverlay"] = ("Xbox Speech-to-Text", "Game chat captions overlay."),
            ["Microsoft.ZuneMusic"] = ("Media Player / Groove", "Music & media player."),
            ["Microsoft.ZuneVideo"] = ("Movies & TV", "Video store and player."),
            ["Microsoft.MicrosoftSolitaireCollection"] = ("Solitaire Collection", "Bundled solitaire games (with ads)."),
            ["Microsoft.People"] = ("People", "Contacts aggregator app."),
            ["Microsoft.windowscommunicationsapps"] = ("Mail & Calendar", "Legacy Mail and Calendar apps, retired by Microsoft at the end of 2024."),
            ["Microsoft.YourPhone"] = ("Phone Link", "Android/iPhone companion."),
            ["Microsoft.Todos"] = ("Microsoft To Do", "Task list app."),
            ["Microsoft.PowerAutomateDesktop"] = ("Power Automate", "Desktop automation tool."),
            ["MicrosoftCorporationII.MicrosoftFamily"] = ("Family", "Family safety app."),
            ["MicrosoftTeams"] = ("Teams (personal)", "Consumer Teams chat."),
            ["MicrosoftCorporationII.QuickAssist"] = ("Quick Assist", "Remote assistance tool."),
            ["Microsoft.Getstarted"] = ("Tips", "Windows tips and getting-started app."),
            ["Microsoft.MicrosoftOfficeHub"] = ("Office Hub", "Office app launcher/upsell."),
            ["Microsoft.3DBuilder"] = ("3D Builder", "Legacy 3D model viewer."),
            ["Microsoft.MixedReality.Portal"] = ("Mixed Reality Portal", "Windows Mixed Reality."),
            ["Microsoft.SkypeApp"] = ("Skype", "Bundled Skype app, retired by Microsoft in May 2025."),
            ["Microsoft.WindowsMaps"] = ("Maps", "Offline maps app."),
            ["Microsoft.WindowsFeedbackHub"] = ("Feedback Hub", "Sends feedback to Microsoft."),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Package families Microsoft has retired. The Store no longer offers them, so a removal cannot be undone by
    /// reinstalling, and the confirmation says so instead of promising the Store (#2505).
    /// </summary>
    /// <remarks>
    /// Only retirements Microsoft has announced: Skype was shut down on 5 May 2025, and Mail and Calendar reached
    /// the end of support on 31 December 2024. Matched like <see cref="ProtectedPrefixes"/>.
    /// </remarks>
    private static readonly string[] RetiredPrefixes =
    [
        "Microsoft.SkypeApp",
        "Microsoft.windowscommunicationsapps",
    ];

    /// <summary>
    /// The script <see cref="ListAsync"/> runs: the non-framework, non-resource packages for the current user.
    /// </summary>
    /// <remarks>
    /// A read that listed nothing and reported an error is a failure, and is thrown so that the runner raises it.
    /// <c>Get-AppxPackage</c> reports its failures as non-terminating errors, so it used to write the error and
    /// return nothing, which parsed as a PC with no Store apps. A read that listed some packages and failed on
    /// others keeps what it listed. <c>-ErrorAction Stop</c> would have lost the whole tab to one damaged package.
    /// <para>Internal so the integration suite can run this exact text in Windows PowerShell with
    /// <c>Get-AppxPackage</c> shadowed by a function.</para>
    /// </remarks>
    internal const string ListScript = """
        $packages = @(Get-AppxPackage -ErrorVariable listErrors -ErrorAction SilentlyContinue)
        if ($packages.Count -eq 0 -and $listErrors.Count -gt 0) { throw $listErrors[0] }
        $packages | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } |
            Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version
        """;

    /// <summary>
    /// Lists installed Store apps for the current user, newest catalog matches first.
    /// Protected packages are included but flagged <see cref="StoreApp.IsProtected"/>.
    /// Returns null when the query failed, and an empty list only when Windows answered with none.
    /// </summary>
    /// <remarks>
    /// A failure used to come back as an empty list, so the tab said "No Store apps found." about a PC whose
    /// apps it had not read (#2487). <see cref="ListScript"/> says how a failed read is told from an empty one.
    /// </remarks>
    public async Task<IReadOnlyList<StoreApp>?> ListAsync(CancellationToken ct = default)
    {
        try
        {
            Collection<PSObject> results = await _ps.RunAsync(ListScript, cancellationToken: ct).ConfigureAwait(false);
            return ParsePackages(results);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Debloater: list failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses <c>Get-AppxPackage</c> output into <see cref="StoreApp"/> records, applying the
    /// denylist and curated catalog. Pure and runner-agnostic for unit testing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="objects"/> is null.</exception>
    public static IReadOnlyList<StoreApp> ParsePackages(IEnumerable<PSObject> objects)
    {
        // The individual nulls below are handled and the collection itself was not (#2259). Found the hard
        // way: an unconfigured test substitute returned a null collection, this threw NullReferenceException
        // inside a view-model's constructor init, and the init swallowed it — so a real fault was invisible
        // until something awaited the task. Failing here says which argument was wrong.
        ArgumentNullException.ThrowIfNull(objects);

        List<StoreApp> apps = [];
        foreach (var obj in objects)
        {
            if (obj is null) continue;
            var name = obj.Properties["Name"]?.Value?.ToString()?.Trim();
            var fullName = obj.Properties["PackageFullName"]?.Value?.ToString()?.Trim();
            var familyName = obj.Properties["PackageFamilyName"]?.Value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(familyName))
                continue;

            var publisher = obj.Properties["Publisher"]?.Value?.ToString()?.Trim() ?? "";
            var version = obj.Properties["Version"]?.Value?.ToString()?.Trim() ?? "";

            var isProtected = IsProtected(name);
            var inCatalog = TryGetCatalog(name, out var display, out var description);

            apps.Add(new StoreApp
            {
                Name = name,
                PackageFullName = fullName,
                PackageFamilyName = familyName,
                DisplayName = inCatalog ? display : PrettyName(name),
                Publisher = publisher,
                Version = version,
                Description = description,
                IsProtected = isProtected,
                IsCommonBloat = inCatalog && !isProtected,
                IsRetired = IsRetired(name)
            });
        }
        // Curated bloat first, then the rest; alphabetical within each band.
        return [.. apps
            .OrderByDescending(a => a.IsCommonBloat)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True if the package name matches a system-critical denylist prefix.</summary>
    public static bool IsProtected(string packageName) =>
        ProtectedPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the package name matches a family Microsoft has retired, which cannot be reinstalled.</summary>
    public static bool IsRetired(string packageName) =>
        RetiredPrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetCatalog(string name, out string display, out string description)
    {
        foreach (var (prefix, entry) in Catalog)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                display = entry.Display;
                description = entry.Description;
                return true;
            }
        }
        display = "";
        description = "";
        return false;
    }

    /// <summary>Turns "Microsoft.WindowsCalculator" into "Windows Calculator" for unknown apps.</summary>
    private static string PrettyName(string name)
    {
        var tail = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return SpaceCamelCase().Replace(tail, "$1 $2").Trim();
    }

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex SpaceCamelCase();

    /// <summary>
    /// Removes a Store app for the current user. Refuses protected packages and validates
    /// the package full name before use. Returns true on success.
    /// </summary>
    public async Task<bool> RemoveAsync(StoreApp app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.IsProtected || IsProtected(app.Name))
        {
            Log.Warning("Debloater: refusing to remove protected package {Name}", app.Name);
            return false;
        }
        if (!PackageFullNameRegex().IsMatch(app.PackageFullName))
        {
            Log.Warning("Debloater: rejected invalid package full name {Full}", app.PackageFullName);
            return false;
        }

        var safeFull = app.PackageFullName.Replace("'", "''");
        var script =
            "try { " +
            $"Remove-AppxPackage -Package '{safeFull}' -ErrorAction Stop; '__SM_RM_OK__' " +
            "} catch { Write-Error $_; exit 1 }";
        try
        {
            var results = await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            var ok = results.Any(o => string.Equals(o?.BaseObject?.ToString(), "__SM_RM_OK__", StringComparison.Ordinal));
            if (!ok) Log.Warning("Debloater: removal of {Name} did not confirm success", app.Name);
            return ok;
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Warning("Debloater: removal of {Name} failed: {Error}", app.Name, ex.Message);
            return false;
        }
    }
}
