// SysManager · PrivacyService — reads/writes privacy-related registry toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
/// <para>What a switch's values held before its first write is kept in <see cref="PrivacyLedgerService"/>, and
/// turning the switch off puts that back (#2778).</para>
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    private readonly RegistryKey _hkcuRoot;
    private readonly RegistryKey _hklmRoot;
    private readonly PrivacyLedgerService _ledger;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates the service. <paramref name="hkcuRoot"/> defaults to the signed-in account's own hive via
    /// <see cref="AdminHelper.PerUserRegistryRoot"/> — <see cref="Registry.CurrentUser"/> itself when
    /// SysManager runs as that account, which is the overwhelming majority of the time, and the account
    /// signed in to the console when elevated as a different administrator (#2826). <paramref
    /// name="hklmRoot"/> defaults to <see cref="Registry.LocalMachine"/>, the other hive the toggles' paths
    /// name. Tests pass redirected roots (an HKCU subkey they own), as <see cref="EdgeOneDriveService"/>'s
    /// tests do, so a test that applies a change writes there rather than into this machine's privacy
    /// policies. <paramref name="ledger"/> keeps what each value held before its first write, in the config
    /// folder unless a test gives one of its own; <paramref name="clock"/> dates its records.
    /// </summary>
    public PrivacyService(RegistryKey? hkcuRoot = null, RegistryKey? hklmRoot = null,
                          PrivacyLedgerService? ledger = null, TimeProvider? clock = null)
    {
        _hkcuRoot = hkcuRoot ?? AdminHelper.PerUserRegistryRoot();
        _hklmRoot = hklmRoot ?? Registry.LocalMachine;
        _ledger = ledger ?? new PrivacyLedgerService();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates the full toggle list with current state read from the registry. A switch the Windows on this PC no
    /// longer reads (<see cref="PrivacyToggle.IsRetired"/>) is in it only while it is on, so it can be turned off.
    /// </summary>
    public List<PrivacyToggle> LoadToggles()
    {
        var toggles = CreateToggleDefinitions(InstalledWindows.Read(_hklmRoot));

        foreach (var toggle in toggles)
        {
            toggle.IsEnabled = ReadCurrentState(toggle);
        }

        // Listed while off, it would be a switch that changes nothing when it is turned on.
        toggles.RemoveAll(t => t.IsRetired && !t.IsEnabled);
        return toggles;
    }

    /// <summary>
    /// Writes a single toggle's current <see cref="PrivacyToggle.IsEnabled"/> state to the registry, and says what
    /// became of it, so the caller can avoid reporting an unwritten change as applied: refused (an HKLM-backed
    /// toggle without elevation, or a hive it does not recognise), or not written because what was there could not
    /// be recorded or the record could not be read.
    /// </summary>
    public PrivacyWriteOutcome ApplyToggle(PrivacyToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);

        try
        {
            using var key = RegistryPaths.OpenOrCreateKey(_hkcuRoot, _hklmRoot, toggle.RegistryPath, writable: true);
            if (key is null)
            {
                Log.Warning("Privacy toggle {Name} not applied — could not open/create {Path}",
                    toggle.Name, toggle.RegistryPath);
                return PrivacyWriteOutcome.Refused;
            }

            // Every value the switch writes, the ones Windows' own switch sets with it included (#2778).
            return toggle.IsEnabled ? Protect(toggle, key) : PutBack(toggle, key);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Access denied writing privacy toggle {Name} at {Path} — elevation required",
                toggle.Name, toggle.RegistryPath);
            return PrivacyWriteOutcome.Refused;
        }
        catch (SecurityException ex)
        {
            Log.Warning(ex, "Security exception writing privacy toggle {Name} at {Path} — elevation required",
                toggle.Name, toggle.RegistryPath);
            return PrivacyWriteOutcome.Refused;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Registry I/O error writing privacy toggle {Name} at {Path}",
                toggle.Name, toggle.RegistryPath);
            return PrivacyWriteOutcome.Refused;
        }
        catch (ArgumentException ex)
        {
            Log.Warning(ex, "Invalid registry key or value writing privacy toggle {Name} at {Path}",
                toggle.Name, toggle.RegistryPath);
            return PrivacyWriteOutcome.Refused;
        }
    }

    /// <summary>
    /// Privacy ON: records what each value holds now, then writes the protection value. Nothing is written when the
    /// record cannot be kept, because turning the switch off again could then not put the values back (#2778).
    /// </summary>
    private PrivacyWriteOutcome Protect(PrivacyToggle toggle, RegistryKey key)
    {
        List<PrivacyPriorValue> priors = [];
        foreach (var valueName in toggle.ValueNames)
        {
            if (PrivacyLedgerService.Capture(key, toggle.RegistryPath, valueName, _clock.GetUtcNow()) is not { } prior)
            {
                Log.Warning("Privacy toggle {Name} not applied — {Path}\\{ValueName} holds a kind of value SysManager "
                    + "could not put back", toggle.Name, toggle.RegistryPath, valueName);
                return PrivacyWriteOutcome.NotRecorded;
            }
            priors.Add(prior);
        }

        if (!_ledger.Remember(priors))
        {
            Log.Warning("Privacy toggle {Name} not applied — what its values held could not be recorded", toggle.Name);
            return PrivacyWriteOutcome.NotRecorded;
        }

        foreach (var valueName in toggle.ValueNames)
        {
            key.SetValue(valueName, toggle.EnabledValue, RegistryValueKind.DWord);
            Log.Information("Privacy toggle applied: {Name} = {Value} at {Path}\\{ValueName}",
                toggle.Name, toggle.EnabledValue, toggle.RegistryPath, valueName);
        }
        return PrivacyWriteOutcome.Written;
    }

    /// <summary>
    /// Privacy OFF (revert): puts each value back as the ledger recorded it before SysManager's first write, and
    /// removes it only when there was none (#2778). A value with no record, one SysManager wrote before it kept them,
    /// is removed, so Windows falls back to its own default, never set to <see cref="PrivacyToggle.DisabledValue"/>:
    /// for a policy that would enforce a level the machine may never have had (AllowTelemetry=3, Full telemetry).
    /// Nothing is changed when the ledger cannot be read, because putting the values back would then be a guess.
    /// <para>The ledger is a file in the user's own profile, so for a switch that needs administrator only a DWORD the
    /// setting is known to take is written back; any other record is removed like a value with none, so a change to
    /// the file cannot have SysManager write what it chooses into a key the user cannot write (#2747).</para>
    /// </summary>
    private PrivacyWriteOutcome PutBack(PrivacyToggle toggle, RegistryKey key)
    {
        if (_ledger.Read() is not ({ } records, false))
        {
            Log.Warning("Privacy toggle {Name} not reverted — the record of what its values held could not be read",
                toggle.Name);
            return PrivacyWriteOutcome.RecordUnreadable;
        }

        List<PrivacyPriorValue> used = [];
        foreach (var valueName in toggle.ValueNames)
        {
            if (records.TryGetValue(PrivacyPriorValue.KeyOf(toggle.RegistryPath, valueName), out var prior))
            {
                used.Add(prior);
                if (MayPutBack(toggle, prior))
                {
                    PrivacyLedgerService.PutBack(key, prior);
                    Log.Information("Privacy toggle reverted (value put back as it was): {Name} at "
                        + "{Path}\\{ValueName}", toggle.Name, toggle.RegistryPath, valueName);
                    continue;
                }
                Log.Warning("Privacy toggle {Name}: the record for {Path}\\{ValueName} holds a value SysManager never "
                    + "sets there, so the value is removed rather than written back",
                    toggle.Name, toggle.RegistryPath, valueName);
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
            Log.Information("Privacy toggle reverted (value removed): {Name} at {Path}\\{ValueName}",
                toggle.Name, toggle.RegistryPath, valueName);
        }
        _ledger.Forget(used);
        return PrivacyWriteOutcome.Written;
    }

    /// <summary>
    /// True when <paramref name="prior"/> may be written back for <paramref name="toggle"/>: always for a switch that
    /// changes only the account's own settings, which the account can write itself, and for one that needs
    /// administrator only an absent value or a DWORD the setting is known to take.
    /// </summary>
    internal static bool MayPutBack(PrivacyToggle toggle, PrivacyPriorValue prior) =>
        !toggle.NeedsAdministrator
        || prior.Kind is null
        || (prior.Kind == nameof(RegistryValueKind.DWord)
            && int.TryParse(prior.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && toggle.TakesValue(value));

    /// <summary>
    /// Applies all toggles in sequence. Errors on individual toggles are logged but do not stop the batch. Returns the
    /// toggles that were not written, each with why (empty when all were), so the caller can report them and avoid
    /// rebasing their baseline.
    /// </summary>
    public IReadOnlyList<PrivacyWriteFailure> ApplyAll(IEnumerable<PrivacyToggle> toggles)
    {
        ArgumentNullException.ThrowIfNull(toggles);

        List<PrivacyWriteFailure> failed = [];
        foreach (var toggle in toggles)
        {
            if (ApplyToggle(toggle) is var outcome and not PrivacyWriteOutcome.Written)
                failed.Add(new PrivacyWriteFailure(toggle, outcome));
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

    private const string WindowsCopilotPane =
        "Turns off the Windows Copilot pane and its taskbar button on versions of Windows that have them, and keeps an "
        + "upgrade from adding the Windows Copilot app. An app already installed is not affected: Preinstalled Apps "
        + "can remove it.";

    private const string WindowsCopilotIgnored =
        "Windows 11 version 24H2 and later no longer read this setting, so it changes nothing on this PC. Turn it off "
        + "and press Apply to clear it. To remove the Windows Copilot app, use Preinstalled Apps.";

    /// <summary>
    /// Builds the full set of toggle definitions with their registry mappings, for the edition and build of
    /// <paramref name="windows"/>: the diagnostic data switch is worded for the edition, the widgets switch writes
    /// Windows 10's news and interests policy on Windows 10 (#2778), and the Windows Copilot switch is retired from
    /// Windows 11 version 24H2 on.
    /// </summary>
    private static List<PrivacyToggle> CreateToggleDefinitions(InstalledWindows windows)
    {
        var diagnosticData = DiagnosticDataWording(windows.EditionId);
        var windowsCopilotIgnored = windows.IsWindows11Version24H2OrLater;
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
                DisabledValue = 3,
                KnownValues = [1, 2]   // Required, and Enhanced on Windows 10 1809 and earlier
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
            // The policy turns off the Windows Copilot pane and its taskbar button, and keeps an upgrade from adding
            // the Windows Copilot app. Windows 11 24H2 has the app and no pane, the app ignores the policy, and
            // Microsoft lists the policy as deprecated, so there the switch read back as applied and changed nothing.
            new PrivacyToggle
            {
                Key = "ai-assistant",
                Name = windowsCopilotIgnored
                    ? "Disable Windows Copilot (no effect on this PC)"
                    : "Disable Windows Copilot",
                Description = windowsCopilotIgnored ? WindowsCopilotIgnored : WindowsCopilotPane,
                Category = "Features",
                RegistryPath = @"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot",
                ValueName = "TurnOffWindowsCopilot",
                EnabledValue = 1,
                DisabledValue = 0,
                IsRetired = windowsCopilotIgnored
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
