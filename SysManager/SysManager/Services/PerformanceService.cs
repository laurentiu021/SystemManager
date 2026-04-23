// SysManager · PerformanceService — manages power plans and performance tweaks
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads and applies performance settings: power plans (powercfg),
/// visual effects (P/Invoke), Game Mode, Xbox Game Bar, GPU max perf,
/// processor state.
///
/// SAFETY CONTRACT:
/// • Every change is two-door: enable ↔ disable.
/// • Before the first Apply, we snapshot the original system state.
/// • Restore always reverts to that snapshot, not to hardcoded defaults.
/// • NVIDIA GPU subkey is auto-detected (not hardcoded to 0000).
/// • Visual effects use SystemParametersInfo (instant), not registry-only.
/// </summary>
public class PerformanceService
{
    private readonly PowerShellRunner _ps;

    // ── Well-known power plan GUIDs ──
    internal const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    internal const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    internal const string UltimatePerfScheme = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    // ── Registry paths ──
    internal const string GameBarKey = @"SOFTWARE\Microsoft\GameBar";
    internal const string GameDvrKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
    internal const string GameConfigStoreKey = @"System\GameConfigStore";
    internal const string GpuClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ── P/Invoke for visual effects ──
    private const uint SPI_GETUIEFFECTS = 0x103E;
    private const uint SPI_SETUIEFFECTS = 0x103F;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, bool pvParam, uint fWinIni);

    public PerformanceService(PowerShellRunner ps) => _ps = ps;

    // ═══════════════════════════════════════════════════════════════
    //  SNAPSHOT — captures original state for safe restore
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Immutable snapshot of the system state taken before any changes.
    /// Used by Restore to revert to the exact original state.
    /// </summary>
    public sealed record OriginalSnapshot(
        string PowerPlanGuid,
        string PowerPlanName,
        bool UiEffectsEnabled,
        bool GameModeEnabled,
        bool XboxGameBarEnabled,
        bool XboxGameDvrEnabled,
        bool GpuDynamicPstate,       // true = dynamic (default), false = disabled
        int ProcessorMinPercentAc,
        string? NvidiaSubKey);       // null = no NVIDIA GPU

    /// <summary>Take a snapshot of the current system state.</summary>
    public async Task<OriginalSnapshot> TakeSnapshotAsync(CancellationToken ct = default)
    {
        var (name, guid) = await GetActivePlanAsync(ct);
        var nvidiaKey = FindNvidiaSubKey();

        return new OriginalSnapshot(
            PowerPlanGuid: guid,
            PowerPlanName: name,
            UiEffectsEnabled: GetUiEffectsEnabled(),
            GameModeEnabled: ReadGameMode(),
            XboxGameBarEnabled: ReadXboxGameBarEnabled(),
            XboxGameDvrEnabled: ReadXboxGameDvrEnabled(),
            GpuDynamicPstate: nvidiaKey != null && !ReadGpuMaxPerformance(nvidiaKey),
            ProcessorMinPercentAc: await ReadProcessorMinPercentAsync(ct),
            NvidiaSubKey: nvidiaKey);
    }

    // ═══════════════════════════════════════════════════════════════
    //  READ — current system state
    // ═══════════════════════════════════════════════════════════════

    public async Task<PerformanceProfile> ReadProfileAsync(CancellationToken ct = default)
    {
        var profile = new PerformanceProfile();

        var (name, guid) = await GetActivePlanAsync(ct);
        profile.ActivePlanName = name;
        profile.ActivePlanGuid = guid;

        profile.VisualEffectsReduced = !GetUiEffectsEnabled();
        profile.GameModeEnabled = ReadGameMode();
        profile.XboxGameBarDisabled = !ReadXboxGameBarEnabled() || !ReadXboxGameDvrEnabled();

        var nvidiaKey = FindNvidiaSubKey();
        profile.HasNvidiaGpu = nvidiaKey != null;
        if (nvidiaKey != null)
        {
            profile.NvidiaGpuName = ReadNvidiaGpuName(nvidiaKey);
            profile.GpuMaxPerformance = ReadGpuMaxPerformance(nvidiaKey);
        }

        var minPct = await ReadProcessorMinPercentAsync(ct);
        profile.ProcessorMinPercent = minPct;
        profile.ProcessorMaxState = minPct >= 100;

        return profile;
    }

    // ═══════════════════════════════════════════════════════════════
    //  POWER PLAN
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Parse active plan from powercfg /getactivescheme output.</summary>
    public async Task<(string Name, string Guid)> GetActivePlanAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        void OnLine(PowerShellLine l) => lines.Add(l.Text);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", "/getactivescheme", ct); }
        finally { _ps.LineReceived -= OnLine; }

        return ParseActivePlan(lines);
    }

    /// <summary>
    /// Parses powercfg /getactivescheme output.
    /// Format: "Power Scheme GUID: 381b4222-...  (Balanced)"
    /// </summary>
    internal static (string Name, string Guid) ParseActivePlan(IList<string> lines)
    {
        foreach (var line in lines)
        {
            var guidIdx = line.IndexOf("GUID:", StringComparison.OrdinalIgnoreCase);
            if (guidIdx < 0) continue;
            var afterGuid = line[(guidIdx + 5)..].Trim();
            var spaceIdx = afterGuid.IndexOf(' ');
            if (spaceIdx < 0) continue;
            var guid = afterGuid[..spaceIdx].Trim();
            var parenStart = afterGuid.IndexOf('(');
            var parenEnd = afterGuid.IndexOf(')');
            var name = parenStart >= 0 && parenEnd > parenStart
                ? afterGuid[(parenStart + 1)..parenEnd]
                : guid;
            return (name, guid);
        }
        return ("Unknown", "");
    }

    /// <summary>Activate a power plan by GUID.</summary>
    public async Task SetActivePlanAsync(string guid, CancellationToken ct = default)
    {
        await _ps.RunProcessAsync("powercfg.exe", $"/setactive {guid}", ct);
    }

    /// <summary>
    /// Create Ultimate Performance plan if it doesn't exist, return its GUID.
    /// </summary>
    public async Task<string> EnsureUltimatePerformancePlanAsync(CancellationToken ct = default)
    {
        var existingGuid = await FindPlanGuidByNameAsync("Ultimate Performance", ct);
        if (!string.IsNullOrEmpty(existingGuid)) return existingGuid;

        var lines = new List<string>();
        void OnLine(PowerShellLine l) => lines.Add(l.Text);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", $"-duplicatescheme {UltimatePerfScheme}", ct); }
        finally { _ps.LineReceived -= OnLine; }

        // Parse GUID from output: "Power Scheme GUID: <guid>  (Ultimate Performance)"
        foreach (var line in lines)
        {
            var idx = line.IndexOf("GUID:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var after = line[(idx + 5)..].Trim();
            var sp = after.IndexOf(' ');
            return sp > 0 ? after[..sp].Trim() : after.Trim();
        }

        return await FindPlanGuidByNameAsync("Ultimate Performance", ct) ?? "";
    }

    /// <summary>Find a plan GUID by name substring.</summary>
    public async Task<string?> FindPlanGuidByNameAsync(string nameSubstring, CancellationToken ct = default)
    {
        var lines = new List<string>();
        void OnLine(PowerShellLine l) => lines.Add(l.Text);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", "/list", ct); }
        finally { _ps.LineReceived -= OnLine; }

        return ParsePlanGuidByName(lines, nameSubstring);
    }

    internal static string? ParsePlanGuidByName(IList<string> lines, string nameSubstring)
    {
        foreach (var line in lines)
        {
            if (!line.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase)) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var after = line[(idx + 1)..].Trim();
            var sp = after.IndexOf(' ');
            return sp > 0 ? after[..sp].Trim() : after.Trim();
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  VISUAL EFFECTS — via P/Invoke (instant, no logout needed)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Read whether UI effects are currently enabled.</summary>
    internal static bool GetUiEffectsEnabled()
    {
        bool enabled = true;
        SystemParametersInfo(SPI_GETUIEFFECTS, 0, ref enabled, 0);
        return enabled;
    }

    /// <summary>
    /// Enable or disable all UI effects (animations, fades, shadows).
    /// Uses SystemParametersInfo which takes effect immediately — no
    /// logout or registry-only hack needed.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public static void SetUiEffects(bool enabled)
    {
        SystemParametersInfo(SPI_SETUIEFFECTS, 0, enabled, SPIF_SENDCHANGE);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GAME MODE — HKCU registry (instant, no reboot)
    // ═══════════════════════════════════════════════════════════════

    internal static bool ReadGameMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBarKey);
            if (key == null) return true; // Windows default = ON
            var val = key.GetValue("AllowAutoGameMode");
            // If key exists but value doesn't, default is ON
            if (val == null) return true;
            return val is int i && i == 1;
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Enable or disable Game Mode.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public static void SetGameMode(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GameBarKey);
        key.SetValue("AllowAutoGameMode", enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AutoGameModeEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    // ═══════════════════════════════════════════════════════════════
    //  XBOX GAME BAR / DVR OVERLAY — HKCU registry (instant)
    // ═══════════════════════════════════════════════════════════════

    internal static bool ReadXboxGameBarEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameDvrKey);
            if (key == null) return true; // default = enabled
            var val = key.GetValue("AppCaptureEnabled");
            if (val == null) return true;
            return val is int i && i == 1;
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static bool ReadXboxGameDvrEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameConfigStoreKey);
            if (key == null) return true;
            var val = key.GetValue("GameDVR_Enabled");
            if (val == null) return true;
            return val is int i && i == 1;
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Enable or disable Xbox Game Bar overlay and Game DVR.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public static void SetXboxGameBar(bool enabled)
    {
        using var dvrKey = Registry.CurrentUser.CreateSubKey(GameDvrKey);
        dvrKey.SetValue("AppCaptureEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);

        using var configKey = Registry.CurrentUser.CreateSubKey(GameConfigStoreKey);
        configKey.SetValue("GameDVR_Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    // ═══════════════════════════════════════════════════════════════
    //  NVIDIA GPU — auto-detect subkey, registry (requires reboot)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Auto-detect the registry subkey for the NVIDIA GPU.
    /// Scans 0000, 0001, 0002, ... and checks DriverDesc/ProviderName
    /// for "NVIDIA". Returns null if no NVIDIA GPU found.
    /// </summary>
    internal static string? FindNvidiaSubKey()
    {
        try
        {
            using var classRoot = Registry.LocalMachine.OpenSubKey(GpuClassRoot);
            if (classRoot == null) return null;

            foreach (var subName in classRoot.GetSubKeyNames())
            {
                // Only check numeric subkeys (0000, 0001, etc.)
                if (!int.TryParse(subName, out _)) continue;

                try
                {
                    using var sub = classRoot.OpenSubKey(subName);
                    if (sub == null) continue;

                    var driverDesc = sub.GetValue("DriverDesc")?.ToString() ?? "";
                    var provider = sub.GetValue("ProviderName")?.ToString() ?? "";

                    if (driverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        return subName;
                    }
                }
                catch (SecurityException) { /* skip inaccessible subkeys */ }
                catch (UnauthorizedAccessException) { /* skip inaccessible subkeys */ }
            }
        }
        catch (SecurityException) { /* registry not accessible */ }
        catch (UnauthorizedAccessException) { /* registry not accessible */ }

        return null;
    }

    /// <summary>Read the NVIDIA GPU friendly name from DriverDesc.</summary>
    internal static string ReadNvidiaGpuName(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{GpuClassRoot}\{subKey}");
            return key?.GetValue("DriverDesc")?.ToString() ?? "NVIDIA GPU";
        }
        catch (SecurityException) { return "NVIDIA GPU"; }
        catch (UnauthorizedAccessException) { return "NVIDIA GPU"; }
    }

    /// <summary>Read whether DisableDynamicPstate is set to 1.</summary>
    internal static bool ReadGpuMaxPerformance(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{GpuClassRoot}\{subKey}");
            if (key == null) return false;
            var val = key.GetValue("DisableDynamicPstate");
            return val is int i && i == 1;
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Set NVIDIA GPU to max performance (DisableDynamicPstate=1) or
    /// restore dynamic P-state (=0). Requires admin + reboot.
    /// Returns false if the registry key doesn't exist.
    /// Reversible: call with false to restore dynamic.
    /// </summary>
    public static bool SetGpuMaxPerformance(string subKey, bool maxPerformance)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{GpuClassRoot}\{subKey}", writable: true);
            if (key == null) return false;

            if (maxPerformance)
                key.SetValue("DisableDynamicPstate", 1, RegistryValueKind.DWord);
            else
            {
                // Restore: set to 0 (don't delete — safer)
                key.SetValue("DisableDynamicPstate", 0, RegistryValueKind.DWord);
            }
            return true;
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  PROCESSOR STATE — powercfg (instant, no reboot)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Read the current processor minimum state percentage (AC).</summary>
    internal async Task<int> ReadProcessorMinPercentAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        void OnLine(PowerShellLine l) => lines.Add(l.Text);
        _ps.LineReceived += OnLine;
        try
        {
            await _ps.RunProcessAsync("powercfg.exe",
                "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN", ct);
        }
        finally { _ps.LineReceived -= OnLine; }

        return ParseProcessorMinPercent(lines);
    }

    internal static int ParseProcessorMinPercent(IList<string> lines)
    {
        // Look for "Current AC Power Setting Index: 0x00000064" (100 = 0x64)
        foreach (var line in lines)
        {
            if (!line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                continue;
            var hexIdx = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (hexIdx < 0) continue;
            var hex = line[(hexIdx + 2)..].Trim();
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var val))
                return val;
        }
        return 5; // Windows default
    }

    /// <summary>
    /// Set processor minimum state to a specific percentage.
    /// Reversible: call with the original percentage to restore.
    /// </summary>
    public async Task SetProcessorMinStateAsync(int percent, CancellationToken ct = default)
    {
        await _ps.RunProcessAsync("powercfg.exe",
            $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {percent}", ct);
        await _ps.RunProcessAsync("powercfg.exe",
            $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {percent}", ct);
        await _ps.RunProcessAsync("powercfg.exe", "/setactive SCHEME_CURRENT", ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  RESTORE — revert to snapshot
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Restore all settings to the exact state captured in the snapshot.
    /// This is the ONLY way to revert — we never guess defaults.
    /// </summary>
    public async Task RestoreFromSnapshotAsync(OriginalSnapshot snapshot, CancellationToken ct = default)
    {
        // Power plan — restore the exact original plan
        if (!string.IsNullOrEmpty(snapshot.PowerPlanGuid))
            await SetActivePlanAsync(snapshot.PowerPlanGuid, ct);

        // Visual effects
        SetUiEffects(snapshot.UiEffectsEnabled);

        // Game Mode
        SetGameMode(snapshot.GameModeEnabled);

        // Xbox Game Bar
        SetXboxGameBar(snapshot.XboxGameBarEnabled && snapshot.XboxGameDvrEnabled);

        // GPU
        if (snapshot.NvidiaSubKey != null)
            SetGpuMaxPerformance(snapshot.NvidiaSubKey, !snapshot.GpuDynamicPstate);

        // Processor state
        await SetProcessorMinStateAsync(snapshot.ProcessorMinPercentAc, ct);
    }
}
