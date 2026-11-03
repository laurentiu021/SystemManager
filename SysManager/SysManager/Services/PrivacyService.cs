// SysManager · PrivacyService — reads/writes privacy-related registry toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Provides a curated list of Windows privacy toggles and persists their state to the registry. A write to HKLM, or
/// to a policy Windows keeps for the account under <c>HKCU\Software\Policies</c>, needs elevation; the summary said
/// every HKCU write succeeds, and those policies refuse the account (#2778). A refused write is logged and reported to
/// the caller.
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    private readonly RegistryKey _hkcuRoot;
    private readonly RegistryKey _hklmRoot;

    /// <summary>
    /// Creates the service. <paramref name="hkcuRoot"/> defaults to the signed-in account's own hive via
    /// <see cref="AdminHelper.PerUserRegistryRoot"/> — <see cref="Registry.CurrentUser"/> itself when
    /// SysManager runs as that account, which is the overwhelming majority of the time, and the account
    /// signed in to the console when elevated as a different administrator (#2826). <paramref
    /// name="hklmRoot"/> defaults to <see cref="Registry.LocalMachine"/>, the other hive the toggles' paths
    /// name. Tests pass redirected roots (an HKCU subkey they own), as <see cref="EdgeOneDriveService"/>'s
    /// tests do, so a test that applies a change writes there rather than into this machine's privacy
    /// policies.
    /// </summary>
    public PrivacyService(RegistryKey? hkcuRoot = null, RegistryKey? hklmRoot = null)
    {
        _hkcuRoot = hkcuRoot ?? AdminHelper.PerUserRegistryRoot();
        _hklmRoot = hklmRoot ?? Registry.LocalMachine;
    }

    /// <summary>
    /// Creates the full toggle list with current state read from the registry.
    /// </summary>
    public List<PrivacyToggle> LoadToggles()
    {
        var toggles = CreateToggleDefinitions(InstalledWindows.Read(_hklmRoot));

        foreach (var toggle in toggles)
        {
            toggle.IsEnabled = ReadCurrentState(toggle);
        }

        return toggles;
    }

    /// <summary>
    /// Writes a single toggle's current <see cref="PrivacyToggle.IsEnabled"/> state
    /// to the registry. Returns <c>true</c> if the write succeeded, <c>false</c> if it
    /// was rejected (e.g. an HKLM-backed toggle without elevation) or the hive was
    /// unrecognized — so the caller can avoid reporting an unwritten change as applied.
    /// </summary>
    public bool ApplyToggle(PrivacyToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);

        try
        {
            using var key = RegistryPaths.OpenOrCreateKey(_hkcuRoot, _hklmRoot, toggle.RegistryPath, writable: true);
            if (key is null)
            {
                Log.Warning("Privacy toggle {Name} not applied — could not open/create {Path}",
                    toggle.Name, toggle.RegistryPath);
                return false;
            }

            // Every value the switch writes, the ones Windows' own switch sets with it included (#2778).
            foreach (var valueName in toggle.ValueNames)
            {
                if (toggle.IsEnabled)
                {
                    // Privacy ON → enforce our protection value.
                    key.SetValue(valueName, toggle.EnabledValue, RegistryValueKind.DWord);
                    Log.Information("Privacy toggle applied: {Name} = {Value} at {Path}\\{ValueName}",
                        toggle.Name, toggle.EnabledValue, toggle.RegistryPath, valueName);
                }
                else
                {
                    // Privacy OFF (revert) → REMOVE our value so Windows falls back to its own
                    // default, rather than WRITING DisabledValue. Writing DisabledValue was wrong:
                    // for policy-backed toggles it materialises an enforced GPO the machine may
                    // never have had (e.g. AllowTelemetry=3 = Full telemetry ENFORCED), which is
                    // strictly worse than the value being absent. Deleting is the correct undo of
                    // an enforcement. DeleteValue(name, throwOnMissingValue: false) is a no-op when
                    // the value is already absent, so revert is idempotent.
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    Log.Information("Privacy toggle reverted (value removed): {Name} at {Path}\\{ValueName}",
                        toggle.Name, toggle.RegistryPath, valueName);
                }
            }
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied writing privacy toggle {Name} at {Path} — elevation required",
                toggle.Name, toggle.RegistryPath);
            return false;
        }
        catch (SecurityException ex)
        {
            Log.Warning(ex, "Security exception writing privacy toggle {Name} at {Path} — elevation required",
                toggle.Name, toggle.RegistryPath);
            return false;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Registry I/O error writing privacy toggle {Name} at {Path}",
                toggle.Name, toggle.RegistryPath);
            return false;
        }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Invalid registry key or value writing privacy toggle {Name} at {Path}",
                toggle.Name, toggle.RegistryPath);
            return false;
        }
    }

    /// <summary>
    /// Applies all toggles in sequence. Errors on individual toggles are logged but do
    /// not stop the batch. Returns the toggles that failed to apply (empty when all
    /// succeeded) so the caller can report failures and avoid rebasing their baseline.
    /// </summary>
    public IReadOnlyList<PrivacyToggle> ApplyAll(IEnumerable<PrivacyToggle> toggles)
    {
        ArgumentNullException.ThrowIfNull(toggles);

        List<PrivacyToggle> failed = [];
        foreach (var toggle in toggles)
        {
            if (!ApplyToggle(toggle))
                failed.Add(toggle);
        }
        return failed;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the current registry state for a toggle.
    /// Returns true (privacy ON) if every value it writes matches <see cref="PrivacyToggle.EnabledValue"/>.
    /// If the key or any of the values does not exist, returns false (default Windows state = privacy off).
    /// </summary>
    private bool ReadCurrentState(PrivacyToggle toggle)
    {
        try
        {
            using var key = RegistryPaths.OpenOrCreateKey(_hkcuRoot, _hklmRoot, toggle.RegistryPath, writable: false);
            if (key is null) return false;

            return toggle.ValueNames.All(name => key.GetValue(name) switch
            {
                int intVal => intVal == toggle.EnabledValue,
                { } other => int.TryParse(other.ToString(), out var parsed) && parsed == toggle.EnabledValue,
                null => false,
            });
        }
        catch (UnauthorizedAccessException)
        {
            Log.Debug("Cannot read {Path}\\{Value} — access denied", toggle.RegistryPath, toggle.ValueName);
            return false;
        }
        catch (SecurityException)
        {
            Log.Debug("Cannot read {Path}\\{Value} — security exception", toggle.RegistryPath, toggle.ValueName);
            return false;
        }
    }

    /// <summary>
    /// The name and description of the diagnostic data switch on the edition <paramref name="editionId"/> names. The
    /// switch sets the AllowTelemetry policy to 0, which turns diagnostic data off only on Enterprise, Education and
    /// Server. Home and Pro read 0 as 1 and still send the required diagnostic data, and the switch promised them none
    /// (#2778). An edition this cannot place, or none, gets the description that holds on every edition. Pure.
    /// </summary>
    /// <remarks>
    /// Pro Education is left to that description rather than placed: Microsoft lists the editions that can turn
    /// diagnostic data off as Enterprise, Education and Server, and whether Pro Education counts as one of them is
    /// not something its documentation says.
    /// </remarks>
    internal static (string Name, string Description) DiagnosticDataWording(string? editionId)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        return editionId switch
        {
            { } e when e.StartsWith("Enterprise", ic) || e.StartsWith("Education", ic) || e.StartsWith("Server", ic)
                => ("Disable diagnostic data", "Prevents Windows from sending diagnostic and usage data to Microsoft."),
            { } e when e.StartsWith("Core", ic) => LeastOn("Home"),
            { } e when e.StartsWith("ProfessionalEducation", ic) => (LimitDiagnosticData, OnAnyEdition),
            { } e when e.StartsWith("ProfessionalWorkstation", ic) => LeastOn("Pro for Workstations"),
            { } e when e.StartsWith("Professional", ic) => LeastOn("Pro"),
            _ => (LimitDiagnosticData, OnAnyEdition),
        };

        static (string, string) LeastOn(string edition) => (LimitDiagnosticData,
            "Lowers the diagnostic data Windows sends to Microsoft to the required level, "
            + $"the least Windows {edition} allows.");
    }

    private const string LimitDiagnosticData = "Limit diagnostic data";

    private const string OnAnyEdition =
        "Lowers the diagnostic data Windows sends to Microsoft as far as your edition allows: off on Enterprise and "
        + "Education, the required level on Home and Pro.";

    /// <summary>
    /// Builds the full set of toggle definitions with their registry mappings, for the edition and build of
    /// <paramref name="windows"/>: the diagnostic data switch is worded for the edition, and the widgets switch writes
    /// Windows 10's news and interests policy on Windows 10 (#2778).
    /// </summary>
    private static List<PrivacyToggle> CreateToggleDefinitions(InstalledWindows windows)
    {
        var diagnosticData = DiagnosticDataWording(windows.EditionId);
        return
        [
            // ── Telemetry ─────────────────────────────────────────────────
            new PrivacyToggle
            {
                Key = "diagnostic-data",
                Name = diagnosticData.Name,
                Description = diagnosticData.Description,
                Category = "Telemetry",
                RegistryPath = RegistryPaths.DataCollectionPolicy,
                ValueName = "AllowTelemetry",
                EnabledValue = 0,
                DisabledValue = 3
            },
            new PrivacyToggle
            {
                Key = "activity-history",
                Name = "Disable activity history",
                Description = "Stops Windows from collecting activity history and sending it to Microsoft.",
                Category = "Telemetry",
                RegistryPath = RegistryPaths.WindowsSystemPolicy,
                ValueName = "EnableActivityFeed",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Key = "advertising-id",
                Name = "Disable advertising ID",
                Description = "Prevents apps from using your advertising ID for targeted ads.",
                Category = "Telemetry",
                RegistryPath = RegistryPaths.AdvertisingInfo,
                ValueName = "Enabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Key = "feedback-prompts",
                Name = "Disable feedback",
                Description = "Stops Windows from prompting for feedback surveys.",
                Category = "Telemetry",
                RegistryPath = @"HKCU\Software\Microsoft\Siuf\Rules",
                ValueName = "NumberOfSIUFInPeriod",
                EnabledValue = 0,
                DisabledValue = 1
            },

            // ── UI Declutter ──────────────────────────────────────────────
            new PrivacyToggle
            {
                Key = "start-suggestions",
                Name = "Disable Start suggestions",
                Description = "Removes suggested apps and content from the Start menu.",
                Category = "UI Declutter",
                RegistryPath = RegistryPaths.ContentDeliveryManager,
                ValueName = "SubscribedContent-338388Enabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Key = "tips",
                Name = "Disable tips",
                Description = "Turns off Windows tips and suggestions notifications.",
                Category = "UI Declutter",
                RegistryPath = RegistryPaths.ContentDeliveryManager,
                ValueName = "SoftLandingEnabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Key = "lock-screen-tips",
                Name = "Disable lock screen tips",
                Description = "Removes the tips, fun facts and promotions Windows shows on the lock screen.",
                Category = "UI Declutter",
                RegistryPath = RegistryPaths.ContentDeliveryManager,
                ValueName = "RotatingLockScreenOverlayEnabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            // The key is the one profiles already name it by. The switch was "Disable Spotlight ads" and promised the
            // lock screen's promotions, and its one value was one of the three behind Windows' "Show me suggested
            // content in the Settings app"; the lock screen's are lock-screen-tips' (#2778).
            new PrivacyToggle
            {
                Key = "spotlight-ads",
                Name = "Disable suggestions in Settings",
                Description = "Stops the Settings app from showing suggested content.",
                Category = "UI Declutter",
                RegistryPath = RegistryPaths.ContentDeliveryManager,
                ValueName = "SubscribedContent-338393Enabled",
                AlsoWrites = ["SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"],
                EnabledValue = 0,
                DisabledValue = 1
            },

            // ── Features ──────────────────────────────────────────────────
            new PrivacyToggle
            {
                Key = "ai-assistant",
                Name = "Disable Copilot",
                Description = "Turns off Windows Copilot AI assistant integration.",
                Category = "Features",
                RegistryPath = @"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot",
                ValueName = "TurnOffWindowsCopilot",
                EnabledValue = 1,
                DisabledValue = 0
            },
            new PrivacyToggle
            {
                Key = "cortana",
                Name = "Disable Cortana",
                Description = "Prevents Cortana from running and collecting voice/search data.",
                Category = "Features",
                RegistryPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCortana",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Key = "web-search",
                Name = "Disable web search",
                Description = "Removes Bing web results from Start menu and taskbar search.",
                Category = "Features",
                RegistryPath = RegistryPaths.ExplorerPolicy,
                ValueName = "DisableSearchBoxSuggestions",
                EnabledValue = 1,
                DisabledValue = 0
            },
            // Windows 11's widgets policy does nothing on Windows 10, whose news and interests has a policy of its own,
            // so the switch read back as on there and the weather button stayed (#2778).
            windows.IsWindows10
                ? new PrivacyToggle
                {
                    Key = "widgets",
                    Name = "Disable news and interests",
                    Description = "Turns off news and interests, the weather and news button on the taskbar.",
                    Category = "Features",
                    RegistryPath = RegistryPaths.WindowsFeedsPolicy,
                    ValueName = "EnableFeeds",
                    EnabledValue = 0,
                    DisabledValue = 1
                }
                : new PrivacyToggle
                {
                    Key = "widgets",
                    Name = "Disable widgets",
                    Description = "Turns off the Widgets board (news and interests) on the taskbar.",
                    Category = "Features",
                    RegistryPath = RegistryPaths.Dsh,
                    ValueName = "AllowNewsAndInterests",
                    EnabledValue = 0,
                    DisabledValue = 1
                },
        ];
    }
}
