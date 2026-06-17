// SysManager · PrivacyService — reads/writes privacy-related registry toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Provides a curated list of Windows privacy toggles and persists their
/// state to the registry. HKCU writes always succeed; HKLM writes require
/// elevation and failures are handled gracefully.
/// </summary>
public sealed class PrivacyService
{
    /// <summary>
    /// Creates the full toggle list with current state read from the registry.
    /// </summary>
    public List<PrivacyToggle> LoadToggles()
    {
        var toggles = CreateToggleDefinitions();

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

        var valueToWrite = toggle.IsEnabled ? toggle.EnabledValue : toggle.DisabledValue;

        try
        {
            using var key = OpenOrCreateKey(toggle.RegistryPath, writable: true);
            if (key is null)
            {
                Log.Warning("Privacy toggle {Name} not applied — could not open/create {Path}",
                    toggle.Name, toggle.RegistryPath);
                return false;
            }

            key.SetValue(toggle.ValueName, valueToWrite, RegistryValueKind.DWord);
            Log.Information("Privacy toggle applied: {Name} = {Value} at {Path}\\{ValueName}",
                toggle.Name, valueToWrite, toggle.RegistryPath, toggle.ValueName);
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
    /// Returns true (privacy ON) if the current value matches <see cref="PrivacyToggle.EnabledValue"/>.
    /// If the key/value does not exist, returns false (default Windows state = privacy off).
    /// </summary>
    private static bool ReadCurrentState(PrivacyToggle toggle)
    {
        try
        {
            using var key = OpenOrCreateKey(toggle.RegistryPath, writable: false);
            if (key is null) return false;

            var value = key.GetValue(toggle.ValueName);
            if (value is null) return false;

            if (value is int intVal)
                return intVal == toggle.EnabledValue;

            if (int.TryParse(value.ToString(), out var parsed))
                return parsed == toggle.EnabledValue;

            return false;
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
    /// Opens or creates a registry key from a full path string (e.g. "HKLM\SOFTWARE\...").
    /// Returns null if the root hive is unrecognized or access is denied.
    /// </summary>
    private static RegistryKey? OpenOrCreateKey(string fullPath, bool writable)
    {
        var separatorIndex = fullPath.IndexOf('\\');
        if (separatorIndex < 0) return null;

        var hiveName = fullPath[..separatorIndex].ToUpperInvariant();
        var subPath = fullPath[(separatorIndex + 1)..];

        var hive = hiveName switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => null
        };

        if (hive is null) return null;

        if (writable)
        {
            // CreateSubKey creates intermediate keys if missing
            return hive.CreateSubKey(subPath, writable: true);
        }

        return hive.OpenSubKey(subPath, writable: false);
    }

    /// <summary>
    /// Builds the full set of toggle definitions with their registry mappings.
    /// </summary>
    private static List<PrivacyToggle> CreateToggleDefinitions()
    {
        return
        [
            // ── Telemetry ─────────────────────────────────────────────────
            new PrivacyToggle
            {
                Name = "Disable diagnostic data",
                Description = "Prevents Windows from sending diagnostic and usage data to Microsoft.",
                Category = "Telemetry",
                RegistryPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                ValueName = "AllowTelemetry",
                EnabledValue = 0,
                DisabledValue = 3
            },
            new PrivacyToggle
            {
                Name = "Disable activity history",
                Description = "Stops Windows from collecting activity history and sending it to Microsoft.",
                Category = "Telemetry",
                RegistryPath = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "EnableActivityFeed",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Name = "Disable advertising ID",
                Description = "Prevents apps from using your advertising ID for targeted ads.",
                Category = "Telemetry",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                ValueName = "Enabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
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
                Name = "Disable Start suggestions",
                Description = "Removes suggested apps and content from the Start menu.",
                Category = "UI Declutter",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-338388Enabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Name = "Disable tips",
                Description = "Turns off Windows tips and suggestions notifications.",
                Category = "UI Declutter",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SoftLandingEnabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Name = "Disable lock screen tips",
                Description = "Removes tips and tricks from the lock screen.",
                Category = "UI Declutter",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "RotatingLockScreenOverlayEnabled",
                EnabledValue = 0,
                DisabledValue = 1
            },
            new PrivacyToggle
            {
                Name = "Disable Spotlight ads",
                Description = "Removes promotional content from Windows Spotlight on the lock screen.",
                Category = "UI Declutter",
                RegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-353696Enabled",
                EnabledValue = 0,
                DisabledValue = 1
            },

            // ── Features ──────────────────────────────────────────────────
            new PrivacyToggle
            {
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
                Name = "Disable web search",
                Description = "Removes Bing web results from Start menu and taskbar search.",
                Category = "Features",
                RegistryPath = @"HKCU\Software\Policies\Microsoft\Windows\Explorer",
                ValueName = "DisableSearchBoxSuggestions",
                EnabledValue = 1,
                DisabledValue = 0
            },
            new PrivacyToggle
            {
                Name = "Disable widgets",
                Description = "Turns off the Widgets board (news and interests) on the taskbar.",
                Category = "Features",
                RegistryPath = @"HKLM\SOFTWARE\Policies\Microsoft\Dsh",
                ValueName = "AllowNewsAndInterests",
                EnabledValue = 0,
                DisabledValue = 1
            },
        ];
    }
}
