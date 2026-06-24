// SysManager · DebloaterService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

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
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._~-]{0,255}$")]
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
        "Microsoft.Windows.SecHealthUI",       // Windows Security UI
        "Microsoft.SecHealthUI",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.LockApp",
        "Microsoft.CredDialogHost",
        "Microsoft.Win32WebViewHost",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.Photos.Settings",
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
    private static readonly Dictionary<string, (string Display, string Description)> Catalog =
        new(StringComparer.OrdinalIgnoreCase)
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
            ["Microsoft.windowscommunicationsapps"] = ("Mail & Calendar", "Legacy Mail and Calendar apps."),
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
            ["Microsoft.SkypeApp"] = ("Skype", "Bundled Skype app."),
            ["Microsoft.WindowsMaps"] = ("Maps", "Offline maps app."),
            ["Microsoft.WindowsFeedbackHub"] = ("Feedback Hub", "Sends feedback to Microsoft."),
        };

    /// <summary>
    /// Lists installed Store apps for the current user, newest catalog matches first.
    /// Protected packages are included but flagged <see cref="StoreApp.IsProtected"/>.
    /// Returns an empty list if the query fails (logged at Debug).
    /// </summary>
    public async Task<IReadOnlyList<StoreApp>> ListAsync(CancellationToken ct = default)
    {
        // Non-framework, non-resource packages for the current user.
        const string script =
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage } | " +
            "Select-Object Name, PackageFullName, PackageFamilyName, Publisher, Version";
        try
        {
            Collection<PSObject> results = await _ps.RunAsync(script, cancellationToken: ct).ConfigureAwait(false);
            return ParsePackages(results);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Debloater: list failed: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Parses <c>Get-AppxPackage</c> output into <see cref="StoreApp"/> records, applying the
    /// denylist and curated catalog. Pure and runner-agnostic for unit testing.
    /// </summary>
    public static IReadOnlyList<StoreApp> ParsePackages(IEnumerable<PSObject> objects)
    {
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
                IsCommonBloat = inCatalog && !isProtected
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
