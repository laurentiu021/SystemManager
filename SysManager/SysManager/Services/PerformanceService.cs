// SysManager · PerformanceService — manages power plans and performance tweaks
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
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
public sealed partial class PerformanceService : IDisposable
{
    private readonly IPowerShellRunner _ps;
    private readonly RestorePointService _restorePoints;
    private readonly string _snapshotPath;
    private readonly string _ultimateCopyPath;
    private readonly Func<System.Diagnostics.Process[]> _processes;
    private readonly Func<System.Diagnostics.Process, bool> _trimWorkingSet;
    private readonly Func<string, RecordedAdapter> _findAdapter;
    private readonly Func<IReadOnlySet<string>?> _presentAdapters;
    private readonly Func<IReadOnlySet<Guid>?> _plansOnThePc;
    private readonly Func<bool, (bool Found, bool Ok)> _writeGpu;
    private readonly Action<bool> _setUiEffects;
    private readonly Action<bool> _setGameMode;
    private readonly Action<bool, bool> _setXboxGameBar;
    private readonly Func<string, bool, bool> _restoreGpu;
    private readonly Func<string, bool?> _readGpu;
    private readonly Func<bool> _isElevated;
    private readonly SemaphoreSlim _psGate = new(1, 1);
    private bool _disposed;

    // ── Well-known power plan GUIDs ──
    // Defined once in Models/PowerPlans so the model can identify a plan too; re-exported here
    // under the names this service and its tests already use.
    internal const string BalancedGuid = PowerPlans.Balanced;
    internal const string HighPerfGuid = PowerPlans.HighPerformance;
    internal const string UltimatePerfScheme = PowerPlans.UltimatePerformance;

    /// <summary>The file the copy of the Ultimate Performance scheme SysManager made is kept in, beside the record.</summary>
    internal const string UltimateCopyFileName = "ultimate-performance-copy.json";

    internal const int MaxSnapshotBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 8,
        AllowDuplicateProperties = false
    };

    // ── Registry paths ──
    internal const string GameBarKey = @"SOFTWARE\Microsoft\GameBar";
    internal const string GameDvrKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
    internal const string GameConfigStoreKey = @"System\GameConfigStore";
    internal const string GpuClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // The display-adapter device class, the last part of GpuClassRoot, which a present adapter's Driver value names
    // before its subkey.
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    // ── P/Invoke for visual effects ──
    private const uint SPI_GETUIEFFECTS = 0x103E;
    private const uint SPI_SETUIEFFECTS = 0x103F;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>How the visual-effects write is made: into the user's profile, and announced to the open windows.</summary>
    /// <remarks>
    /// With <c>SPIF_SENDCHANGE</c> alone Windows changes the session and writes nothing to the profile, so after a
    /// sign-out the animations were back and the tab read them as normal, and a restore lasted no longer (#2716).
    /// </remarks>
    internal const uint UiEffectsWrite = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoGet(
        uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoSet(
        uint uiAction, uint uiParam, int pvParam, uint fWinIni);

    /// <summary>The service over the machine, with its record in <paramref name="configDir"/>.</summary>
    /// <param name="configDir">
    /// Where the record and the Ultimate Performance copy's GUID live; the real profile unless the shell's no-container
    /// graph passes its own.
    /// </param>
    public PerformanceService(IPowerShellRunner ps, RestorePointService restorePoints, string? configDir = null)
        : this(
            ps,
            restorePoints,
            configDir ?? AppDataFolders.Local,
            System.Diagnostics.Process.GetProcesses,
            writeGpu: WriteGpuToRegistry,
            setUiEffects: SetUiEffects,
            setGameMode: SetGameMode,
            setXboxGameBar: SetXboxGameBar,
            restoreGpu: SetGpuMaxPerformance,
            presentAdapters: PresentAdapterKeys,
            plansOnThePc: PlansOnThePc,
            readGpu: ReadGpuSetting,
            isElevated: AdminHelper.IsElevated)
    {
    }

    /// <summary>
    /// Test seam for snapshot persistence without touching the real app profile, and for the RAM trim without touching
    /// the machine's processes.
    /// </summary>
    /// <param name="processes">
    /// The processes the RAM trim works through. Unlike the folder, which every test passes, it defaults to none: the
    /// public constructor passes every process on the machine, so a test that leaves it out trims nothing instead of
    /// paging out every program on the computer running the suite (#2557).
    /// </param>
    /// <param name="trimWorkingSet">Empties one process's working set; the real call unless a test passes its own.</param>
    /// <param name="findAdapter">
    /// What has become of the NVIDIA adapter a record names; the registry's answer unless a test passes its own, so
    /// that what a test sees does not depend on the graphics card of the PC running it.
    /// </param>
    /// <param name="writeGpu">
    /// The NVIDIA setting's write for <see cref="WriteGpuMaxPerformance"/>. Like <paramref name="processes"/> it
    /// defaults to nothing: the public constructor passes the registry write, and a test that leaves it out finds no
    /// card rather than changing the graphics setting of the PC running the suite.
    /// </param>
    /// <param name="setUiEffects">
    /// The visual-effects write a restore makes. It defaults to nothing for the same reason: the public constructor
    /// passes <see cref="SetUiEffects"/>, and a test that leaves it out restores nothing on the PC running the suite.
    /// </param>
    /// <param name="setGameMode">The Game Mode write a restore makes; nothing unless given, as above.</param>
    /// <param name="setXboxGameBar">The Game Bar and Game DVR write a restore makes; nothing unless given.</param>
    /// <param name="presentAdapters">
    /// The display-class subkeys of the adapters in the PC, null when not known. Unless given it is not known, so a
    /// test reads no WMI and the NVIDIA key is the first one the registry names, as it was.
    /// </param>
    /// <param name="restoreGpu">
    /// The write that puts the recorded NVIDIA adapter's setting back, by its subkey. Unless given it writes nothing
    /// and says so, so a restore that reaches a card a test did not provide for fails rather than writing this PC's.
    /// </param>
    /// <param name="readGpu">
    /// The NVIDIA adapter's setting as it is now, by its subkey: true for maximum performance, null when it could not
    /// be read. Unless given it is not known, so a restore writes the card's setting through its stand-in, as it did.
    /// </param>
    /// <param name="isElevated">
    /// Whether the session can write the card's setting. Unless given it can, so a restore a test drives is not
    /// refused because of how the PC running the suite was started.
    /// </param>
    /// <param name="plansOnThePc">
    /// The power plans on the PC, null when not known. Unless given they are not known, so a test reads no plans of the
    /// PC running it, and a restore switches the recorded plan on as it did.
    /// </param>
    internal PerformanceService(
        IPowerShellRunner ps,
        RestorePointService restorePoints,
        string configDir,
        Func<System.Diagnostics.Process[]>? processes = null,
        Func<System.Diagnostics.Process, bool>? trimWorkingSet = null,
        Func<string, RecordedAdapter>? findAdapter = null,
        Func<bool, (bool Found, bool Ok)>? writeGpu = null,
        Action<bool>? setUiEffects = null,
        Action<bool>? setGameMode = null,
        Action<bool, bool>? setXboxGameBar = null,
        Func<string, bool, bool>? restoreGpu = null,
        Func<IReadOnlySet<string>?>? presentAdapters = null,
        Func<IReadOnlySet<Guid>?>? plansOnThePc = null,
        Func<string, bool?>? readGpu = null,
        Func<bool>? isElevated = null)
    {
        _ps = ps;
        _restorePoints = restorePoints;
        _snapshotPath = Path.Combine(configDir, "performance-snapshot.json");
        _ultimateCopyPath = Path.Combine(configDir, UltimateCopyFileName);
        _processes = processes ?? (() => []);
        _trimWorkingSet = trimWorkingSet ?? TrimWorkingSet;
        _presentAdapters = presentAdapters ?? (() => null);
        _plansOnThePc = plansOnThePc ?? (() => null);
        _readGpu = readGpu ?? (_ => null);
        _isElevated = isElevated ?? (() => true);
        _findAdapter = findAdapter ?? (subKey => FindRecordedAdapter(subKey, ReadAdapterNames, _presentAdapters));
        _writeGpu = writeGpu ?? (_ => (Found: false, Ok: false));
        _setUiEffects = setUiEffects ?? (_ => { });
        _setGameMode = setGameMode ?? (_ => { });
        _setXboxGameBar = setXboxGameBar ?? ((_, _) => { });
        _restoreGpu = restoreGpu ?? ((_, _) => false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SNAPSHOT — captures original state for safe restore
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Immutable snapshot of the system state taken before any changes.
    /// Used by Restore to revert to the exact original state.
    /// </summary>
    /// <remarks>
    /// <c>ProcessorMinPercentDc</c>, the minimum on battery, is not required, so a record from before it was kept still
    /// loads, and its restore gives the battery the plugged-in value, as every record's did. Only that one was kept, so
    /// Restore All, Undo Changes and the processor switch set the battery minimum to it on any plan where the two
    /// differ (#2716).
    /// </remarks>
    public sealed record OriginalSnapshot(
        [property: JsonRequired] string PowerPlanGuid,
        [property: JsonRequired] string PowerPlanName,
        [property: JsonRequired] bool UiEffectsEnabled,
        [property: JsonRequired] bool GameModeEnabled,
        [property: JsonRequired] bool XboxGameBarEnabled,
        [property: JsonRequired] bool XboxGameDvrEnabled,
        [property: JsonRequired] bool GpuDynamicPstate,       // true = dynamic (default), false = disabled
        [property: JsonRequired] int? ProcessorMinPercentAc,  // null = couldn't read (don't restore rather than guess)
        [property: JsonRequired] string? NvidiaSubKey,        // null = no NVIDIA GPU
        DateTimeOffset? CapturedAtUtc = null, // null = legacy snapshot without timestamp
        int? ProcessorMinPercentDc = null);   // on battery; null = legacy snapshot or couldn't read (the AC value goes on both)

    /// <summary>
    /// Take a snapshot of the current system state.
    /// IMPORTANT: callers MUST invoke this BEFORE applying any change, otherwise
    /// the snapshot will capture already-modified state and Restore will not be
    /// able to revert to the original baseline. The recommended pattern is to
    /// guard every Apply through a wrapper that lazy-initializes the snapshot
    /// (see PerformanceViewModel.EnsureSnapshotAsync).
    /// </summary>
    public async Task<OriginalSnapshot> TakeSnapshotAsync(CancellationToken ct = default)
    {
        var (name, guid) = await GetActivePlanAsync(ct).ConfigureAwait(false);
        var nvidiaKey = FindNvidiaSubKey(_presentAdapters());
        var minimum = await ReadProcessorMinPercentsAsync(ct).ConfigureAwait(false);

        return new OriginalSnapshot(
            PowerPlanGuid: guid,
            PowerPlanName: name,
            UiEffectsEnabled: GetUiEffectsEnabled(),
            GameModeEnabled: ReadGameMode(),
            XboxGameBarEnabled: ReadXboxGameBarEnabled(),
            XboxGameDvrEnabled: ReadXboxGameDvrEnabled(),
            GpuDynamicPstate: nvidiaKey is not null && !ReadGpuMaxPerformance(nvidiaKey),
            ProcessorMinPercentAc: minimum.Ac,
            NvidiaSubKey: nvidiaKey,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            ProcessorMinPercentDc: minimum.Dc);
    }

    /// <summary>Persist a snapshot to disk so it survives app restarts.</summary>
    public bool SaveSnapshot(OriginalSnapshot snapshot)
    {
        if (!TryValidateSnapshot(snapshot, out var reason))
        {
            Log.Warning("Refused to save invalid performance snapshot: {Reason}", reason);
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(_snapshotPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);
            if (json.Length > MaxSnapshotBytes)
                throw new InvalidDataException("The performance snapshot exceeds the supported size.");
            AtomicFile.WriteAllBytes(_snapshotPath, json);
            Log.Information("Performance snapshot saved to {Path}", _snapshotPath);
            return true;
        }
        catch (InvalidDataException ex) { Log.Warning(ex, "Failed to save performance snapshot"); return false; }
        catch (IOException ex) { Log.Warning(ex, "Failed to save performance snapshot"); return false; }
        catch (SecurityException ex) { Log.Warning(ex, "Failed to save performance snapshot"); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Failed to save performance snapshot"); return false; }
    }

    /// <summary>
    /// Delete the persisted snapshot from disk. Called after "Restore All" so the next
    /// Apply captures a fresh baseline instead of reloading the now-reverted pre-restore
    /// state via <see cref="LoadSnapshot()"/>.
    /// </summary>
    public bool DeleteSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath);
            Log.Information("Performance snapshot deleted from {Path}", _snapshotPath);
            return true;
        }
        catch (IOException ex) { Log.Warning(ex, "Failed to delete performance snapshot"); return false; }
        catch (SecurityException ex) { Log.Warning(ex, "Failed to delete performance snapshot"); return false; }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Failed to delete performance snapshot"); return false; }
    }

    /// <summary>
    /// Loads and validates a previously saved snapshot. Invalid, incomplete, or oversized
    /// files are rejected before any value can reach a restore operation.
    /// </summary>
    public OriginalSnapshot? LoadSnapshot() => LoadSnapshot(out _);

    /// <summary>Why <see cref="LoadSnapshot(out SnapshotProblem)"/> returned no snapshot.</summary>
    internal enum SnapshotProblem
    {
        /// <summary>There is no snapshot file: nothing has been changed since the last Restore All, if ever.</summary>
        None,

        /// <summary>The file is there and could not be read.</summary>
        Unreadable,

        /// <summary>The file was read and is not a snapshot that could be restored.</summary>
        Invalid,
    }

    /// <summary>
    /// <see cref="LoadSnapshot()"/>, and when it finds no usable snapshot, whether there was none or it could not be used.
    /// </summary>
    /// <remarks>
    /// The difference decides whether a baseline may be captured. With no snapshot, the settings now are the user's
    /// own. With one that is there but unusable, they may be the tweaks of an earlier Apply, and capturing them would
    /// record the tweaks as the original: Restore All would then put them back (#2521).
    /// </remarks>
    internal OriginalSnapshot? LoadSnapshot(out SnapshotProblem problem)
    {
        problem = SnapshotProblem.None;
        try
        {
            if (!File.Exists(_snapshotPath)) return null;
            var snapshot = JsonSerializer.Deserialize<OriginalSnapshot>(
                ReadBounded(_snapshotPath),
                SnapshotJsonOptions);
            if (!TryValidateSnapshot(snapshot, out var reason))
            {
                Log.Warning("Rejected invalid performance snapshot: {Reason}", reason);
                problem = SnapshotProblem.Invalid;
                return null;
            }
            return snapshot;
        }
        catch (InvalidDataException ex) { Log.Warning(ex, "Rejected invalid performance snapshot"); problem = SnapshotProblem.Invalid; }
        catch (IOException ex) { Log.Warning(ex, "Failed to load performance snapshot"); problem = SnapshotProblem.Unreadable; }
        catch (SecurityException ex) { Log.Warning(ex, "Failed to load performance snapshot"); problem = SnapshotProblem.Unreadable; }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Failed to load performance snapshot"); problem = SnapshotProblem.Unreadable; }
        catch (JsonException ex) { Log.Warning(ex, "Failed to parse performance snapshot"); problem = SnapshotProblem.Invalid; }
        return null;
    }

    /// <summary>
    /// Keeps a snapshot that is not usable as "performance-snapshot.json.unreadable", so a new baseline can be
    /// recorded without destroying it. False when it could not be moved, and then nothing may be recorded.
    /// </summary>
    internal bool SetSnapshotAside() => StoreFile.SetAside(_snapshotPath);

    // One of this service's two files, whole. Neither is ever larger than MaxSnapshotBytes, so a file that is larger, or
    // empty, or changes while it is read, is refused.
    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (stream.Length is <= 0 or > MaxSnapshotBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} has an invalid size.");

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException($"{Path.GetFileName(path)} changed while it was being read.");
        return bytes;
    }

    internal static bool TryValidateSnapshot(OriginalSnapshot? snapshot, out string reason)
    {
        if (snapshot is null)
            return RejectSnapshot("the document is empty", out reason);

        if (snapshot.PowerPlanGuid is null)
            return RejectSnapshot("the power-plan GUID is missing", out reason);
        if (snapshot.PowerPlanGuid.Length > 0 && CanonicalPlan(snapshot.PowerPlanGuid) is null)
        {
            return RejectSnapshot("the power-plan GUID is not canonical", out reason);
        }

        if (string.IsNullOrWhiteSpace(snapshot.PowerPlanName) ||
            snapshot.PowerPlanName.Length > 256 ||
            snapshot.PowerPlanName.Any(character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return RejectSnapshot("the power-plan name is invalid", out reason);
        }

        if (snapshot.ProcessorMinPercentAc is < 0 or > 100)
            return RejectSnapshot("the processor minimum is outside 0-100", out reason);
        if (snapshot.ProcessorMinPercentDc is < 0 or > 100)
            return RejectSnapshot("the processor minimum on battery is outside 0-100", out reason);

        if (snapshot.NvidiaSubKey is not null &&
            !NvidiaSubKeyRegex().IsMatch(snapshot.NvidiaSubKey))
        {
            return RejectSnapshot("the NVIDIA registry subkey is invalid", out reason);
        }

        reason = "";
        return true;
    }

    private static bool RejectSnapshot(string rejectionReason, out string reason)
    {
        reason = rejectionReason;
        return false;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[0-9]{1,8}\z")]
    private static partial System.Text.RegularExpressions.Regex NvidiaSubKeyRegex();

    // ═══════════════════════════════════════════════════════════════
    //  READ — current system state
    // ═══════════════════════════════════════════════════════════════

    public async Task<PerformanceProfile> ReadProfileAsync(CancellationToken ct = default)
    {
        var profile = new PerformanceProfile();

        var (name, guid) = await GetActivePlanAsync(ct).ConfigureAwait(false);
        profile.ActivePlanName = name;
        profile.ActivePlanGuid = guid;
        profile.IsUltimateCopy = guid.Length > 0 && string.Equals(ReadUltimateCopy(), guid, StringComparison.OrdinalIgnoreCase);

        profile.VisualEffectsReduced = !GetUiEffectsEnabled();
        profile.GameModeEnabled = ReadGameMode();
        profile.XboxGameBarOn = ReadXboxGameBarEnabled();
        profile.XboxGameDvrOn = ReadXboxGameDvrEnabled();

        var nvidiaKey = FindNvidiaSubKey(_presentAdapters());
        profile.HasNvidiaGpu = nvidiaKey is not null;
        if (nvidiaKey is not null)
        {
            profile.NvidiaGpuName = ReadNvidiaGpuName(nvidiaKey);
            profile.GpuMaxPerformance = ReadGpuMaxPerformance(nvidiaKey);
        }

        // Display-only profile: if the value can't be read, fall back to the Windows default
        // (5) for the UI — unchanged from before. The RESTORE path (snapshot) keeps the true
        // null so it never writes back a guessed value.
        var minPct = await ReadProcessorMinPercentAsync(ct).ConfigureAwait(false) ?? 5;
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
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        // ConcurrentQueue: LineReceived fires from BOTH the stdout and stderr reader threads,
        // so a plain List<string>.Add could interleave and corrupt/throw. Matches WingetService.
        ConcurrentQueue<string> lines = new();
        void OnLine(PowerShellLine l) => lines.Enqueue(l.Text);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", "/getactivescheme", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false); }
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }

        return ParseActivePlan([.. lines]);
    }

    // Canonical GUID token (8-4-4-4-12 hex). powercfg prints the active plan's GUID in this
    // exact form in EVERY display language, so anchoring on it makes the parse locale-agnostic.
    [System.Text.RegularExpressions.GeneratedRegex(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial System.Text.RegularExpressions.Regex PowerPlanGuidRegex();

    /// <summary>
    /// Parses powercfg /getactivescheme output. Example (en-US):
    /// "Power Scheme GUID: 381b4222-...  (Balanced)".
    /// Locale-agnostic: the English label ("Power Scheme GUID:") is translated on non-English
    /// Windows, so we anchor on the canonical GUID token itself (stable in every language)
    /// rather than the label. The friendly name is read from the trailing parentheses when
    /// present (localized, display-only); otherwise the GUID stands in.
    /// </summary>
    internal static (string Name, string Guid) ParseActivePlan(IList<string> lines)
    {
        foreach (var line in lines)
        {
            var m = PowerPlanGuidRegex().Match(line);
            if (!m.Success) continue;
            var guid = m.Value;
            var parenStart = line.IndexOf('(');
            var parenEnd = line.IndexOf(')');
            var name = parenStart >= 0 && parenEnd > parenStart
                ? line[(parenStart + 1)..parenEnd].Trim()
                : guid;
            return (name, guid);
        }
        return ("Unknown", "");
    }

    /// <summary>
    /// <paramref name="value"/> as powercfg prints a plan, the 36 characters of the D format, or null when it is not
    /// one.
    /// </summary>
    /// <remarks>
    /// Guid parsing allows white space around a GUID, so the parse alone let a plan with a line break after it through,
    /// and the plan went on to powercfg's command line as it was written. Each plan checked here was read back from a
    /// file in the user's profile: the snapshot Restore puts back, game mode's record, and the Ultimate Performance
    /// copy's (#2741).
    /// </remarks>
    internal static string? CanonicalPlan(string? value) =>
        value is { Length: 36 } && Guid.TryParseExact(value, "D", out var plan) ? plan.ToString("D") : null;

    /// <summary>Activate a power plan by GUID.</summary>
    public async Task SetActivePlanAsync(string guid, CancellationToken ct = default)
    {
        if (CanonicalPlan(guid) is not { } plan)
            throw new ArgumentException("Power-plan GUID must use canonical D format.", nameof(guid));

        // Serialize on the same gate the readers use: they own the shared
        // _ps.LineReceived while parsing, and a concurrent powercfg call on the
        // same runner would interleave their output stream.
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exitCode = await _ps.RunProcessAsync(
                "powercfg.exe",
                $"/setactive {plan}",
                ct,
                PowerShellRunner.OemEncoding).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"powercfg failed to activate the power plan (exit code {exitCode}).");
        }
        finally { ReleaseGate(); }
    }

    /// <summary>
    /// Create Ultimate Performance plan if it doesn't exist, return its GUID — or "" when Windows yields none.
    /// </summary>
    /// <remarks>
    /// Written for English Windows until #2438: the existing copy was found only by its English name, and the new
    /// copy's GUID was read after the English "GUID:" label — the same label <see cref="ParseActivePlan"/> had
    /// already stopped trusting because powercfg translates it. On other display languages the lookup missed,
    /// each click duplicated the plan again, no GUID was parsed, and the caller was handed "". The new copy's GUID
    /// is now read as the canonical token, the same in every language, and the built-in scheme is used as it is
    /// whenever Windows lists it.
    /// <para>An earlier copy was still found only by its English name, so where the built-in scheme is hidden, as on
    /// Home and Pro, and the display language is not English, every Ultimate step, from this tab or from a game mode
    /// start, added another copy to Power Options (#2717). The GUID of the copy made here is now kept, and the copy is
    /// used again while Windows lists it. A copy made before that is still found only by its English name.</para>
    /// </remarks>
    public async Task<string> EnsureUltimatePerformancePlanAsync(CancellationToken ct = default)
    {
        var existingGuid = await FindUltimatePlanGuidAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existingGuid)) return existingGuid;

        // ConcurrentQueue: LineReceived fires from BOTH the stdout and stderr reader threads,
        // so a plain List<string>.Add could interleave and corrupt/throw. Matches WingetService.
        ConcurrentQueue<string> lines = new();
        void OnLine(PowerShellLine l) => lines.Enqueue(l.Text);
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", $"-duplicatescheme {UltimatePerfScheme}", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false); }
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }

        // "Power Scheme GUID: <new guid>  (Ultimate Performance)" in English; the label and the name are translated
        // elsewhere, the GUID never is.
        var created = ParseFirstPlanGuid([.. lines]);
        if (created is not null)
        {
            RecordUltimateCopy(created);
            return created;
        }

        return await FindUltimatePlanGuidAsync(ct).ConfigureAwait(false) ?? "";
    }

    /// <summary>
    /// The Ultimate Performance plan already on this PC: the built-in scheme itself when <c>powercfg /list</c> shows
    /// it (its GUID is the same in every language), then the copy SysManager made while the list shows it, by its
    /// GUID, so in any language, then an earlier copy found by its English name.
    /// </summary>
    private async Task<string?> FindUltimatePlanGuidAsync(CancellationToken ct)
    {
        var listed = await ListPlansAsync(ct).ConfigureAwait(false);
        if (listed.Any(l => l.Contains(UltimatePerfScheme, StringComparison.OrdinalIgnoreCase))) return UltimatePerfScheme;
        if (ReadUltimateCopy() is { } copy && listed.Any(l => l.Contains(copy, StringComparison.OrdinalIgnoreCase)))
            return copy;
        return ParsePlanGuidByName(listed, "Ultimate Performance");
    }

    // The copy as its file holds it.
    private sealed record UltimateCopy([property: JsonRequired] string PlanGuid);

    /// <summary>
    /// The GUID of the copy of the Ultimate Performance scheme SysManager made, or null when it made none or the
    /// record of it cannot be used.
    /// </summary>
    /// <remarks>
    /// Only a GUID in its canonical form is taken, as powercfg prints one and <see cref="SetActivePlanAsync"/> passes
    /// one. A record that cannot be used names no copy, and the next Ultimate step makes one and keeps that.
    /// </remarks>
    internal string? ReadUltimateCopy()
    {
        try
        {
            if (!File.Exists(_ultimateCopyPath)) return null;
            var guid = JsonSerializer.Deserialize<UltimateCopy>(ReadBounded(_ultimateCopyPath), SnapshotJsonOptions)?.PlanGuid;
            return CanonicalPlan(guid);
        }
        catch (InvalidDataException ex) { Log.Debug("The Ultimate Performance copy's record cannot be used: {Error}", ex.Message); }
        catch (JsonException ex) { Log.Debug("The Ultimate Performance copy's record cannot be used: {Error}", ex.Message); }
        catch (IOException ex) { Log.Debug("The Ultimate Performance copy's record could not be read: {Error}", ex.Message); }
        catch (SecurityException ex) { Log.Debug("The Ultimate Performance copy's record could not be read: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("The Ultimate Performance copy's record could not be read: {Error}", ex.Message); }
        return null;
    }

    // Keeps the GUID of the copy just made. A record that could not be used is written over: it named no copy that
    // could be found, and keeping it would mean another copy at every Ultimate step. Both callers hold the
    // system-modification lock, so two copies are never made at once.
    private void RecordUltimateCopy(string guid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_ultimateCopyPath)!);
            AtomicFile.WriteAllBytes(_ultimateCopyPath, JsonSerializer.SerializeToUtf8Bytes(new UltimateCopy(guid), SnapshotJsonOptions));
        }
        catch (IOException ex) { Log.Warning(ex, "The Ultimate Performance copy could not be recorded"); }
        catch (SecurityException ex) { Log.Warning(ex, "The Ultimate Performance copy could not be recorded"); }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "The Ultimate Performance copy could not be recorded"); }
    }

    /// <summary>What has become of the power plan a record names.</summary>
    internal enum RecordedPlan
    {
        /// <summary>Windows still lists it, so it can be switched back on.</summary>
        Present,

        /// <summary>Windows lists its plans and this one is not among them: deleted, or the plans were reset.</summary>
        Gone,

        /// <summary>The list could not be read, so whether it is still there is not known.</summary>
        Unreadable,
    }

    /// <summary>What has become of the power plan <paramref name="snapshot"/> names, or null when it names none.</summary>
    /// <remarks>
    /// What asks before a restore goes through here, and hands the answer to the restore, as it does the NVIDIA card's,
    /// so the restore does what the question said. The plans are read through the power API rather than powercfg: a
    /// powercfg read waits behind any other, and Restore All's question has to come up while the tab's own read is
    /// stuck.
    /// </remarks>
    internal RecordedPlan? PlanOf(OriginalSnapshot snapshot) =>
        string.IsNullOrEmpty(snapshot.PowerPlanGuid) ? null : FindRecordedPlan(snapshot.PowerPlanGuid, _plansOnThePc());

    /// <summary>
    /// What has become of the plan <paramref name="guid"/> names, among <paramref name="plans"/>, the plans on the PC,
    /// which are not known when null.
    /// </summary>
    internal static RecordedPlan FindRecordedPlan(string guid, IReadOnlySet<Guid>? plans) =>
        plans is null || !Guid.TryParse(guid, out var plan) ? RecordedPlan.Unreadable
        : plans.Contains(plan) ? RecordedPlan.Present
        : RecordedPlan.Gone;

    // The power API's enumeration of the power plans: ACCESS_SCHEME lists the schemes, and ERROR_NO_MORE_ITEMS ends
    // the list.
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(
        IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index,
        ref Guid buffer, ref uint bufferSize);

    /// <summary>The GUIDs of the power plans on the PC, the ones <c>powercfg /list</c> shows, or null when Windows
    /// could not list them.</summary>
    /// <remarks>
    /// An empty list is a read that did not work, since Windows always has an active plan, so it is not known rather
    /// than none: a plan found gone is left out of the restore, and lets the record be deleted.
    /// </remarks>
    internal static IReadOnlySet<Guid>? PlansOnThePc()
    {
        HashSet<Guid> plans = [];
        for (uint index = 0; ; index++)
        {
            var plan = Guid.Empty;
            var size = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<Guid>();
            var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, ref plan, ref size);
            if (result == ErrorNoMoreItems) break;
            if (result != 0)
            {
                Log.Debug("The power plans could not be listed (error {Error})", result);
                return null;
            }
            plans.Add(plan);
        }
        return plans.Count == 0 ? null : plans;
    }

    /// <summary>The first canonical GUID in powercfg output, whatever language its labels are in.</summary>
    internal static string? ParseFirstPlanGuid(IList<string> lines)
    {
        foreach (var line in lines)
        {
            var m = PowerPlanGuidRegex().Match(line);
            if (m.Success) return m.Value;
        }
        return null;
    }

    /// <summary>The lines of <c>powercfg /list</c>.</summary>
    private async Task<IList<string>> ListPlansAsync(CancellationToken ct)
    {
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        // ConcurrentQueue: LineReceived fires from BOTH the stdout and stderr reader threads,
        // so a plain List<string>.Add could interleave and corrupt/throw. Matches WingetService.
        ConcurrentQueue<string> lines = new();
        void OnLine(PowerShellLine l) => lines.Enqueue(l.Text);
        _ps.LineReceived += OnLine;
        try { await _ps.RunProcessAsync("powercfg.exe", "/list", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false); }
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }

        return [.. lines];
    }

    internal static string? ParsePlanGuidByName(IList<string> lines, string nameSubstring)
    {
        foreach (var line in lines.Where(l => l.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase)))
        {
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
        int enabled = 1;
        SystemParametersInfoGet(SPI_GETUIEFFECTS, 0, ref enabled, 0);
        return enabled != 0;
    }

    /// <summary>
    /// Enable or disable all UI effects (animations, fades, shadows).
    /// Uses SystemParametersInfo which takes effect immediately — no
    /// logout or registry-only hack needed — and keeps the change in the profile, so it lasts past a sign-out.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public static void SetUiEffects(bool enabled)
    {
        SystemParametersInfoSet(SPI_SETUIEFFECTS, 0, enabled ? 1 : 0, UiEffectsWrite);
        Log.Information("System: Visual effects {Action}", enabled ? "enabled" : "reduced");
    }

    // ═══════════════════════════════════════════════════════════════
    //  GAME MODE — HKCU registry (instant, no reboot)
    // ═══════════════════════════════════════════════════════════════

    internal static bool ReadGameMode()
    {
        try
        {
            // The signed-in account's own hive, not necessarily Registry.CurrentUser: elevated as a
            // different administrator, the two diverge and this switch used to read and write the
            // administrator's profile while the person who actually plays sees no change (#2826).
            // Disposed: PerUserRegistryRoot's rare elevated-as-another-user branch opens a real
            // HKEY_USERS handle rather than the well-known Registry.CurrentUser, whose own Dispose is a
            // no-op — so this closes that handle without touching the shared static in the common case.
            using var hkcuRoot = AdminHelper.PerUserRegistryRoot();
            using var key = hkcuRoot.OpenSubKey(GameBarKey);
            return GameModeOn(key?.GetValue("AutoGameModeEnabled"), key?.GetValue("AllowAutoGameMode"));
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Whether Game Mode is on, by <c>AutoGameModeEnabled</c>, the value the Settings switch writes, then by
    /// <c>AllowAutoGameMode</c>, Windows 10 1703's, and when neither is there by Windows' default, on.
    /// </summary>
    /// <remarks>
    /// Only <c>AllowAutoGameMode</c> was read. With Game Mode off in Settings and that value absent, the tab showed it
    /// on, refused to turn it on, and recorded "on" as the original for Restore All to put back (#2716).
    /// </remarks>
    internal static bool GameModeOn(object? autoGameModeEnabled, object? allowAutoGameMode) =>
        (autoGameModeEnabled ?? allowAutoGameMode) switch
        {
            null => true,
            int value => value == 1,
            _ => false,
        };

    /// <summary>
    /// Enable or disable Game Mode.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public static void SetGameMode(bool enabled)
    {
        using var hkcuRoot = AdminHelper.PerUserRegistryRoot();
        using var key = hkcuRoot.CreateSubKey(GameBarKey);
        key.SetValue("AllowAutoGameMode", enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("AutoGameModeEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        Log.Information("Registry: Game Mode {Action} (AllowAutoGameMode={Value})",
            enabled ? "enabled" : "disabled", enabled ? 1 : 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  XBOX GAME BAR / DVR OVERLAY — HKCU registry (instant)
    // ═══════════════════════════════════════════════════════════════

    internal static bool ReadXboxGameBarEnabled()
    {
        try
        {
            using var hkcuRoot = AdminHelper.PerUserRegistryRoot();
            using var key = hkcuRoot.OpenSubKey(GameDvrKey);
            if (key is null) return true; // default = enabled
            var val = key.GetValue("AppCaptureEnabled");
            if (val is null) return true;
            return val is int i && i == 1;
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static bool ReadXboxGameDvrEnabled()
    {
        try
        {
            using var hkcuRoot = AdminHelper.PerUserRegistryRoot();
            using var key = hkcuRoot.OpenSubKey(GameConfigStoreKey);
            if (key is null) return true;
            var val = key.GetValue("GameDVR_Enabled");
            if (val is null) return true;
            return val is int i && i == 1;
        }
        catch (SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Enable or disable Xbox Game Bar overlay and Game DVR together — the user-facing
    /// toggle sets both keys to the same state. Reversible: call with true to re-enable.
    /// </summary>
    public static void SetXboxGameBar(bool enabled) => SetXboxGameBar(enabled, enabled);

    /// <summary>
    /// Sets the two independent Xbox registry values separately. Used by snapshot restore,
    /// where <c>AppCaptureEnabled</c> (Game Bar) and <c>GameDVR_Enabled</c> (per-game DVR)
    /// can legitimately differ — a user may have one on and the other off. Collapsing them
    /// into a single value (e.g. <c>barEnabled &amp;&amp; dvrEnabled</c>) would leave one key in
    /// the wrong state on restore, breaking the "revert to the exact snapshot" contract.
    /// </summary>
    public static void SetXboxGameBar(bool appCaptureEnabled, bool gameDvrEnabled)
    {
        // Resolved once: both keys belong to the same account, and PerUserRegistryRoot's rare
        // elevated-as-another-user branch opens a real HKEY_USERS handle that is cheaper to share
        // between the two writes below than to re-resolve and re-open twice.
        using var hkcuRoot = AdminHelper.PerUserRegistryRoot();

        using var dvrKey = hkcuRoot.CreateSubKey(GameDvrKey);
        dvrKey.SetValue("AppCaptureEnabled", appCaptureEnabled ? 1 : 0, RegistryValueKind.DWord);

        using var configKey = hkcuRoot.CreateSubKey(GameConfigStoreKey);
        configKey.SetValue("GameDVR_Enabled", gameDvrEnabled ? 1 : 0, RegistryValueKind.DWord);
        Log.Information("Registry: Xbox Game Bar set (AppCaptureEnabled={Bar}, GameDVR_Enabled={Dvr})",
            appCaptureEnabled ? 1 : 0, gameDvrEnabled ? 1 : 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  NVIDIA GPU — auto-detect subkey, registry (requires reboot)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Auto-detect the registry subkey for the NVIDIA GPU.
    /// Scans 0000, 0001, 0002, ... and checks DriverDesc/ProviderName
    /// for "NVIDIA". Returns null if no NVIDIA GPU found.
    /// </summary>
    internal static string? FindNvidiaSubKey() => FindNvidiaSubKey(PresentAdapterKeys());

    /// <summary><see cref="FindNvidiaSubKey()"/> among the adapters <paramref name="present"/> names.</summary>
    internal static string? FindNvidiaSubKey(IReadOnlySet<string>? present)
    {
        try
        {
            using var classRoot = Registry.LocalMachine.OpenSubKey(GpuClassRoot);
            return classRoot is null ? null : FindNvidiaSubKey(classRoot.GetSubKeyNames(), ReadAdapterNames, present);
        }
        catch (SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The first of <paramref name="subKeys"/> whose names mention NVIDIA, among the adapters in the PC when
    /// <paramref name="present"/> says which those are, and among every key when it is null.
    /// </summary>
    /// <remarks>
    /// Windows keeps a removed card's key until the device is uninstalled, and it sorts before its replacement's, so
    /// after a GPU upgrade, or a move from NVIDIA to another maker, the first key naming NVIDIA was the card taken out.
    /// The tab showed it as detected, and wrote its setting, which needs a restart, where no running driver reads it
    /// (#2836).
    /// </remarks>
    internal static string? FindNvidiaSubKey(
        IEnumerable<string> subKeys, Func<string, (string DriverDesc, string Provider)?> readNames,
        IReadOnlySet<string>? present)
    {
        foreach (var subName in subKeys.Where(s => int.TryParse(s, out _)))
        {
            if (present is not null && !present.Contains(subName)) continue;
            try
            {
                if (readNames(subName) is { } names
                    && (names.DriverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || names.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
                {
                    return subName;
                }
            }
            catch (SecurityException) { /* skip inaccessible subkeys */ }
            catch (UnauthorizedAccessException) { /* skip inaccessible subkeys */ }
        }
        return null;
    }

    /// <summary>How long the read of the adapters in the PC may take: it bounds a WMI service that hangs.</summary>
    private static readonly TimeSpan AdapterReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The display-class subkeys of the adapters in the PC now, or null when they could not be read.
    /// </summary>
    /// <remarks>
    /// <c>Win32_VideoController</c> lists only the adapters that are present, and each one's device key names its class
    /// subkey in its <c>Driver</c> value.
    /// </remarks>
    internal static IReadOnlySet<string>? PresentAdapterKeys()
    {
        List<string> devices = [];
        try
        {
            using var searcher = Wmi.Searcher(
                "SELECT PNPDeviceID FROM Win32_VideoController", AdapterReadTimeout);
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    if (mo["PNPDeviceID"] is string device && device.Length > 0) devices.Add(device);
                }
            }
        }
        catch (ManagementException ex) { Log.Debug("Display adapters unreadable: {Error}", ex.Message); return null; }
        catch (COMException ex) { Log.Debug("Display adapters WMI COM error: {Error}", ex.Message); return null; }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug("Display adapters access denied: {Error}", ex.Message);
            return null;
        }

        return PresentAdapterKeys(devices, ReadDeviceDriver);
    }

    /// <summary>
    /// The class subkeys the drivers of <paramref name="devices"/> name, or null when none names one: a PC always has a
    /// display adapter, so an empty answer is a read that did not work, not a PC without a graphics card.
    /// </summary>
    internal static IReadOnlySet<string>? PresentAdapterKeys(
        IEnumerable<string> devices, Func<string, string?> readDriver)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            try
            {
                if (ClassSubKeyOf(readDriver(device)) is { } subKey) keys.Add(subKey);
            }
            catch (SecurityException) { /* skip a device key that cannot be read */ }
            catch (UnauthorizedAccessException) { /* skip a device key that cannot be read */ }
            catch (IOException) { /* skip a device key that cannot be read */ }
        }
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>The display-class subkey a device's <c>Driver</c> value names, or null for another class.</summary>
    internal static string? ClassSubKeyOf(string? driver)
    {
        if (driver is null || !driver.StartsWith(DisplayClass + @"\", StringComparison.OrdinalIgnoreCase)) return null;
        var subKey = driver[(DisplayClass.Length + 1)..];
        return NvidiaSubKeyRegex().IsMatch(subKey) ? subKey : null;
    }

    // The Driver value of a device's key, which names its class and subkey.
    private static string? ReadDeviceDriver(string device)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{device}");
        return key?.GetValue("Driver") as string;
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
    internal static bool ReadGpuMaxPerformance(string subKey) => ReadGpuSetting(subKey) ?? false;

    /// <summary>Whether DisableDynamicPstate is set to 1 on the adapter's key, or null when the key could not be read.</summary>
    internal static bool? ReadGpuSetting(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{GpuClassRoot}\{subKey}");
            if (key is null) return null;
            var val = key.GetValue("DisableDynamicPstate");
            return val is int i && i == 1;
        }
        catch (SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Set NVIDIA GPU to max performance (DisableDynamicPstate=1) or
    /// restore dynamic P-state (=0). Requires admin + reboot.
    /// Returns false if the registry key doesn't exist.
    /// Reversible: call with false to restore dynamic.
    /// </summary>
    public static bool SetGpuMaxPerformance(string subKey, bool maxPerformance)
    {
        if (!IsNvidiaSubKey(subKey)) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{GpuClassRoot}\{subKey}", writable: true);
            if (key is null) return false;

            if (maxPerformance)
                key.SetValue("DisableDynamicPstate", 1, RegistryValueKind.DWord);
            else
            {
                // Restore: set to 0 (don't delete — safer)
                key.SetValue("DisableDynamicPstate", 0, RegistryValueKind.DWord);
            }
            Log.Information("Registry: GPU DisableDynamicPstate set to {Value} on subkey {SubKey}",
                maxPerformance ? 1 : 0, subKey);
            return true;
        }
        catch (SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool IsNvidiaSubKey(string subKey) => FindRecordedAdapter(subKey) == RecordedAdapter.Present;

    /// <summary>What has become of the NVIDIA adapter a record names.</summary>
    internal enum RecordedAdapter
    {
        /// <summary>It is still an NVIDIA adapter, so its setting can be written back.</summary>
        Present,

        /// <summary>Its key is gone, or now belongs to another card: there is nothing to write the setting back to.</summary>
        Gone,

        /// <summary>Its key could not be read, so whether it is still there is not known.</summary>
        Unreadable,
    }

    /// <summary>What has become of the adapter <paramref name="subKey"/> names: still an NVIDIA one, gone, or not known.</summary>
    /// <remarks>
    /// Three answers rather than <see cref="IsNvidiaSubKey"/>'s two, because they lead to different things (#1525). An
    /// adapter that is gone — taken out, or its driver removed so that the key now belongs to another card — has nothing
    /// to write its setting back to, and a restore that stopped there could never finish. One whose key could not be
    /// read may still be there, and leaving it out would lose its setting for good.
    /// </remarks>
    internal static RecordedAdapter FindRecordedAdapter(string subKey) =>
        FindRecordedAdapter(subKey, ReadAdapterNames, PresentAdapterKeys);

    /// <summary>
    /// <see cref="FindRecordedAdapter(string)"/>, with the read of the adapter's key a parameter, so that a test can
    /// drive every answer — a read that fails among them — without the registry of the PC running it.
    /// </summary>
    internal static RecordedAdapter FindRecordedAdapter(string subKey, Func<string, (string DriverDesc, string Provider)?> readNames)
        => FindRecordedAdapter(subKey, readNames, present: () => null);

    /// <summary>
    /// What has become of the adapter <paramref name="subKey"/> names, as above, and gone too when
    /// <paramref name="present"/>, read only for a key that could be one, says which adapters are in the PC and this
    /// one is not. A removed card's key keeps its NVIDIA names until the device is uninstalled, so a restore wrote the
    /// setting back where no driver reads it (#2836).
    /// </summary>
    internal static RecordedAdapter FindRecordedAdapter(
        string subKey, Func<string, (string DriverDesc, string Provider)?> readNames,
        Func<IReadOnlySet<string>?> present)
    {
        if (!NvidiaSubKeyRegex().IsMatch(subKey)) return RecordedAdapter.Gone;
        if (present() is { } inThePC && !inThePC.Contains(subKey)) return RecordedAdapter.Gone;
        try
        {
            return readNames(subKey) is { } names
                   && (names.DriverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                       || names.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                ? RecordedAdapter.Present
                : RecordedAdapter.Gone;
        }
        catch (SecurityException) { return RecordedAdapter.Unreadable; }
        catch (UnauthorizedAccessException) { return RecordedAdapter.Unreadable; }
        // Not read, so not known: "gone" would leave the setting out and let the record be deleted.
        catch (IOException) { return RecordedAdapter.Unreadable; }
    }

    // The names the adapter's key gives its driver and its maker, or null when the key is not there.
    private static (string DriverDesc, string Provider)? ReadAdapterNames(string subKey)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{GpuClassRoot}\{subKey}");
        return key is null
            ? null
            : (key.GetValue("DriverDesc")?.ToString() ?? "", key.GetValue("ProviderName")?.ToString() ?? "");
    }

    /// <summary>
    /// What has become of the adapter <paramref name="subKey"/> names, as the restore finds it: whatever asks before a
    /// restore goes through here, so that it says what the restore will do.
    /// </summary>
    internal RecordedAdapter FindAdapter(string subKey) => _findAdapter(subKey);

    /// <summary>Performance Mode's Apply of the visual effects, through the write a restore makes.</summary>
    /// <remarks>
    /// The Apply commands called the static writes themselves, so no test could reach their success path without
    /// changing the settings of the PC running it. Through the constructor's writes, a test's are its own.
    /// </remarks>
    internal void ApplyUiEffects(bool enabled) => _setUiEffects(enabled);

    /// <summary>Performance Mode's Apply of Game Mode, through the write a restore makes.</summary>
    internal void ApplyGameMode(bool enabled) => _setGameMode(enabled);

    /// <summary>Performance Mode's Apply of the Xbox Game Bar: the overlay and per-game recording together.</summary>
    internal void ApplyXboxGameBar(bool enabled) => _setXboxGameBar(enabled, enabled);

    /// <summary>
    /// Finds the NVIDIA card and writes its max-performance setting: <c>Found</c> is false when there is no card, and
    /// <c>Ok</c> false when the write was refused. Performance Mode's own Apply; Restore All writes the recorded card
    /// through <see cref="RestoreOriginalAsync"/> instead.
    /// </summary>
    internal (bool Found, bool Ok) WriteGpuMaxPerformance(bool maxPerformance) => _writeGpu(maxPerformance);

    private static (bool Found, bool Ok) WriteGpuToRegistry(bool maxPerformance)
    {
        var nvidiaKey = FindNvidiaSubKey();
        return nvidiaKey is null
            ? (Found: false, Ok: false)
            : (Found: true, Ok: SetGpuMaxPerformance(nvidiaKey, maxPerformance));
    }

    /// <summary>What has become of the NVIDIA adapter <paramref name="snapshot"/> names, or null when it names none.</summary>
    internal RecordedAdapter? AdapterOf(OriginalSnapshot snapshot) =>
        snapshot.NvidiaSubKey is { } subKey ? FindAdapter(subKey) : null;

    // ═══════════════════════════════════════════════════════════════
    //  PROCESSOR STATE — powercfg (instant, no reboot)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the current processor minimum state percentage (AC), or null if it can't be
    /// parsed. Callers that persist it for restore MUST keep the null (never substitute a
    /// default), so a snapshot on a machine we couldn't read doesn't write back a wrong value.
    /// </summary>
    internal async Task<int?> ReadProcessorMinPercentAsync(CancellationToken ct = default) =>
        (await ReadProcessorMinPercentsAsync(ct).ConfigureAwait(false)).Ac;

    /// <summary>The processor minimum plugged in and on battery, each null when it could not be parsed.</summary>
    internal async Task<(int? Ac, int? Dc)> ReadProcessorMinPercentsAsync(CancellationToken ct = default)
    {
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        // ConcurrentQueue: LineReceived fires from BOTH the stdout and stderr reader threads,
        // so a plain List<string>.Add could interleave and corrupt/throw. Matches WingetService.
        ConcurrentQueue<string> lines = new();
        void OnLine(PowerShellLine l) => lines.Enqueue(l.Text);
        _ps.LineReceived += OnLine;
        try
        {
            await _ps.RunProcessAsync("powercfg.exe",
                "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false);
        }
        finally { _ps.LineReceived -= OnLine; ReleaseGate(); }

        return ParseProcessorMinPercents([.. lines]);
    }

    internal static int? ParseProcessorMinPercent(IList<string> lines) => ParseProcessorMinPercents(lines).Ac;

    /// <summary>
    /// The plugged-in and the battery processor minimum in <c>powercfg /query</c>'s output: by their English labels,
    /// and in another display language as the last two hex values, which come AC first. Each is null when it is not
    /// there.
    /// </summary>
    internal static (int? Ac, int? Dc) ParseProcessorMinPercents(IList<string> lines)
    {
        // powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN prints, in an order that
        // is identical in every display language, up to five hex values:
        //   Minimum Possible Setting, Maximum Possible Setting, Possible Settings increment,
        //   Current AC Power Setting Index, Current DC Power Setting Index.
        // Only the LABELS are translated; the AC-before-DC ordering and the fact that the two
        // "current index" lines come LAST never change — so the AC index we want is always the
        // second-to-last hex token in the output, and DC the last.
        //
        // Fast path (en-US): match the English AC label exactly — cheap and unambiguous — and the DC one beside it.
        if (HexAfter(lines, "Current AC Power Setting Index") is int ac)
            return (ac, HexAfter(lines, "Current DC Power Setting Index"));
        // Locale-agnostic fallback: on non-English Windows the label above is translated, so the
        // fast path misses. Collect every hex token in order and take the second-to-last (AC).
        // The previous fallback returned the FIRST hex token — "Minimum Possible Setting" (0x0) —
        // so on non-English Windows it read 0, and a later "Restore" wrote 0% back as the minimum.
        List<int> hexValues = [];
        foreach (var line in lines)
        {
            if (TryParseHexToken(line, out var val)) hexValues.Add(val);
        }
        // Need both current-index tokens (AC + DC) to trust the ordering; AC is second-to-last.
        // Fewer than two = ambiguous, so return null rather than guess — never fabricate a value.
        return hexValues.Count >= 2 ? (hexValues[^2], hexValues[^1]) : (null, null);
    }

    // The hex value on the first line that carries the label and one.
    private static int? HexAfter(IList<string> lines, string label)
    {
        foreach (var line in lines.Where(l => l.Contains(label, StringComparison.OrdinalIgnoreCase)))
        {
            if (TryParseHexToken(line, out var value)) return value;
        }
        return null;
    }

    private static bool TryParseHexToken(string line, out int value)
    {
        value = 0;
        var hexIdx = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (hexIdx < 0) return false;
        var hex = line[(hexIdx + 2)..].Trim();
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    /// <summary>
    /// Set processor minimum state to a specific percentage.
    /// Reversible: call with the original percentage to restore.
    /// </summary>
    public Task SetProcessorMinStateAsync(int percent, CancellationToken ct = default) =>
        SetProcessorMinStateAsync(percent, percent, ct);

    /// <summary>Set the processor minimum plugged in to <paramref name="ac"/> and on battery to <paramref name="dc"/>.</summary>
    public async Task SetProcessorMinStateAsync(int ac, int dc, CancellationToken ct = default)
    {
        if (ac is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(ac), ac, "Processor minimum must be between 0 and 100.");
        if (dc is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(dc), dc, "Processor minimum must be between 0 and 100.");

        // Serialize on the shared gate — see SetActivePlanAsync.
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var acExitCode = await _ps.RunProcessAsync("powercfg.exe",
                $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {ac}", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false);
            if (acExitCode != 0)
                throw new InvalidOperationException($"powercfg failed to set the AC processor minimum (exit code {acExitCode}).");

            var dcExitCode = await _ps.RunProcessAsync("powercfg.exe",
                $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN {dc}", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false);
            if (dcExitCode != 0)
                throw new InvalidOperationException($"powercfg failed to set the DC processor minimum (exit code {dcExitCode}).");

            var applyExitCode = await _ps.RunProcessAsync(
                "powercfg.exe", "/setactive SCHEME_CURRENT", ct, PowerShellRunner.OemEncoding).ConfigureAwait(false);
            if (applyExitCode != 0)
                throw new InvalidOperationException($"powercfg failed to apply the processor minimum (exit code {applyExitCode}).");
        }
        finally { ReleaseGate(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RESTORE POINT — via WMI (requires admin)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a Windows System Restore point. Requires admin elevation.
    /// Returns true if the restore point was created successfully.
    /// Delegates to <see cref="RestorePointService.CreateAsync"/> so restore-point creation
    /// has a single source of truth (it also enables System Restore on the system drive
    /// first) — this method exists only as the Performance tab's convenience entry point.
    /// </summary>
    public Task<bool> CreateRestorePointAsync(string description, CancellationToken ct = default)
        => _restorePoints.CreateAsync(description, ct);

    // ═══════════════════════════════════════════════════════════════
    //  RAM WORKING SET TRIM — via EmptyWorkingSet (instant)
    // ═══════════════════════════════════════════════════════════════

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Trim the working set of all accessible processes, freeing physical
    /// RAM pages back to the standby list. Pages are soft-faulted back in
    /// on demand — no data is lost, but apps may feel briefly slower on
    /// next access. This is the same operation as "Empty Working Set" in
    /// RAMMap. Does not require a reboot.
    /// </summary>
    /// <returns>Number of processes successfully trimmed.</returns>
    /// <remarks>
    /// An instance method, not static, so a test can hand the service its own process list: as a static it could only
    /// be tested by trimming every process on the machine running the suite, which two unit tests did (#2557).
    /// </remarks>
    public int TrimWorkingSets() => TrimWorkingSets(_processes(), _trimWorkingSet);

    /// <summary>
    /// The trim loop: counts the processes whose trim succeeded, skips one it cannot open or that has exited, and
    /// disposes every process it is given.
    /// </summary>
    internal static int TrimWorkingSets(
        IEnumerable<System.Diagnostics.Process> processes, Func<System.Diagnostics.Process, bool> trim)
    {
        int trimmed = 0;
        foreach (var proc in processes)
        {
            using (proc)
            {
                try
                {
                    if (trim(proc))
                        trimmed++;
                }
                catch (System.ComponentModel.Win32Exception) { /* access denied — skip */ }
                catch (InvalidOperationException) { /* process exited — skip */ }
            }
        }
        return trimmed;
    }

    /// <summary>
    /// Empties one process's working set. Internal so the integration suite can make the real call on its own process
    /// alone.
    /// </summary>
    internal static bool TrimWorkingSet(System.Diagnostics.Process process) => EmptyWorkingSet(process.Handle);

    // ═══════════════════════════════════════════════════════════════
    //  HIBERNATION TOGGLE — powercfg (requires admin)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Read whether hibernation is currently enabled by checking for
    /// the hiberfil.sys file on the system drive.
    /// </summary>
    public static bool ReadHibernationEnabled()
    {
        var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var root = System.IO.Path.GetPathRoot(systemDrive) ?? @"C:\";
        var hiberFile = System.IO.Path.Join(root, "hiberfil.sys");
        return System.IO.File.Exists(hiberFile);
    }

    /// <summary>
    /// Enable or disable hibernation. Requires admin.
    /// When disabled, deletes hiberfil.sys and frees disk space.
    /// Reversible: call with true to re-enable.
    /// </summary>
    public async Task SetHibernationAsync(bool enabled, CancellationToken ct = default)
    {
        var arg = enabled ? "/hibernate on" : "/hibernate off";
        // Serialize on the shared gate — see SetActivePlanAsync.
        await _psGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The exit code decides, as it does for SetActivePlanAsync. It used to be discarded, so a PC without
            // hibernation support — many virtual machines, some firmware — was told the change succeeded (#2438).
            var exitCode = await _ps.RunProcessAsync("powercfg.exe", arg, ct, PowerShellRunner.OemEncoding).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"powercfg could not turn hibernation {(enabled ? "on" : "off")} (exit code {exitCode}).");
        }
        finally { ReleaseGate(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  RESTORE — revert to snapshot
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Restore all settings to the exact state captured in the snapshot.
    /// This is the ONLY way to revert — we never guess defaults.
    /// </summary>
    /// <remarks>
    /// The NVIDIA card the snapshot names is taken as <paramref name="card"/> found it — what the question asked before
    /// the restore said — or looked for now when it is null. Its power plan is left alone when <paramref name="plan"/>,
    /// the question's, found it gone, and when it is gone now.
    /// </remarks>
    internal async Task RestoreFromSnapshotAsync(
        OriginalSnapshot snapshot, RecordedAdapter? card, RecordedPlan? plan = null, CancellationToken ct = default)
    {
        if (!TryValidateSnapshot(snapshot, out var reason))
            throw new InvalidOperationException($"The saved performance snapshot is invalid: {reason}.");

        // GPU — as the question found the card, so the restore does what it said. Only an administrator can write its
        // setting, so a restore that has to is refused before it changes anything: it used to put the plan, the effects,
        // Game Mode and the Xbox settings back and stop at the card, before the processor minimum, on every retry, and
        // it wrote the card even when it was set as recorded already (#2716).
        var graphics = GraphicsStepOf(snapshot, card);
        if (RestoreNeedsAdministrator(graphics)) throw new UnauthorizedAccessException(GraphicsNeedsAdministrator);

        // Power plan — restore the exact original plan, unless it is gone. Deleted, or the plans reset by
        // powercfg -restoredefaultschemes or an OEM tool, it could not be switched on, and the restore stopped here,
        // before every other setting, with every retry failing the same way (#2716). There is nothing to switch back
        // to then, so it is logged and the rest goes on, as for a graphics card that is gone.
        if (!string.IsNullOrEmpty(snapshot.PowerPlanGuid))
        {
            if (plan == RecordedPlan.Gone || PlanOf(snapshot) == RecordedPlan.Gone)
                Log.Warning("Performance restore: the power plan it recorded ({Guid}) is no longer on this PC, so it is "
                            + "left out", snapshot.PowerPlanGuid);
            else
                await SetActivePlanAsync(snapshot.PowerPlanGuid, ct).ConfigureAwait(false);
        }

        // Visual effects
        _setUiEffects(snapshot.UiEffectsEnabled);

        // Game Mode
        _setGameMode(snapshot.GameModeEnabled);

        // Xbox Game Bar — restore each key from its own snapshot value; the two are
        // independent (Game Bar overlay vs per-game DVR) and must not be collapsed.
        _setXboxGameBar(snapshot.XboxGameBarEnabled, snapshot.XboxGameDvrEnabled);

        // Processor state — only restore if we captured a real value. A null means the
        // snapshot couldn't read the original minimum (e.g. an unparseable powercfg output),
        // so writing anything would fabricate state; leaving it untouched is the safe choice.
        // Each from its own field: the battery minimum got the plugged-in value (#2716).
        if (snapshot.ProcessorMinPercentAc is int minPct)
            await SetProcessorMinStateAsync(minPct, snapshot.ProcessorMinPercentDc ?? minPct, ct).ConfigureAwait(false);

        // GPU last, so a write that fails leaves nothing else undone; the write checks the card again.
        RestoreGraphics(snapshot, graphics, _restoreGpu);
    }

    /// <summary>What a restore does with the graphics setting a record names.</summary>
    internal enum GraphicsStep
    {
        /// <summary>The record names no NVIDIA card.</summary>
        None,

        /// <summary>The card is gone, so there is nothing to write its setting to.</summary>
        LeftOut,

        /// <summary>The card is set as recorded already, so nothing is written and no restart is needed.</summary>
        AlreadySet,

        /// <summary>The setting is written back, which needs administrator rights and a restart.</summary>
        Write,
    }

    /// <summary>The message a restore that has to write the card's setting is refused with, without administrator rights.</summary>
    internal const string GraphicsNeedsAdministrator =
        "Putting back the NVIDIA graphics setting needs administrator rights, so nothing was changed. Use Run as "
        + "administrator at the top of the page.";

    /// <summary>
    /// What a restore does with <paramref name="snapshot"/>'s graphics setting, with its card as <paramref name="card"/>
    /// found it, or looked for now when it is null.
    /// </summary>
    internal GraphicsStep GraphicsStepOf(OriginalSnapshot snapshot, RecordedAdapter? card) =>
        GraphicsStepOf(snapshot, card is { } found ? _ => found : _findAdapter, _readGpu);

    /// <summary>
    /// What a restore does with <paramref name="snapshot"/>'s graphics setting: nothing for a card that is gone or set as
    /// recorded already, and a write for one that differs or may still be there.
    /// </summary>
    internal static GraphicsStep GraphicsStepOf(
        OriginalSnapshot snapshot, Func<string, RecordedAdapter> findAdapter, Func<string, bool?> readMaxPerformance)
    {
        if (snapshot.NvidiaSubKey is not { } subKey) return GraphicsStep.None;
        return findAdapter(subKey) switch
        {
            RecordedAdapter.Gone => GraphicsStep.LeftOut,
            RecordedAdapter.Present when readMaxPerformance(subKey) == !snapshot.GpuDynamicPstate => GraphicsStep.AlreadySet,
            _ => GraphicsStep.Write,
        };
    }

    /// <summary>Whether a restore taking <paramref name="graphics"/> needs administrator rights this session lacks.</summary>
    internal bool RestoreNeedsAdministrator(GraphicsStep graphics) => graphics == GraphicsStep.Write && !_isElevated();

    /// <summary>
    /// Writes the record's graphics setting back when <paramref name="step"/> says to, and logs a card that is gone.
    /// Throws when the setting could not be written.
    /// </summary>
    /// <remarks>
    /// An adapter that is gone used to stop the restore here, before the processor minimum and before the record was
    /// deleted, so Restore All could never finish once the card it recorded had been taken out or its driver removed —
    /// and Undo Changes kept a row that could never be put back (#1525). There is nothing to write the setting to then,
    /// so it is logged and the rest goes on. The two lookups are parameters so that a test can drive each answer
    /// without the registry of the PC running it.
    /// </remarks>
    internal static void RestoreGraphics(OriginalSnapshot snapshot, GraphicsStep step, Func<string, bool, bool> write)
    {
        if (snapshot.NvidiaSubKey is not { } subKey) return;

        if (step == GraphicsStep.LeftOut)
        {
            Log.Warning("Performance restore: the NVIDIA adapter it recorded ({SubKey}) is no longer found, so its "
                        + "graphics setting is left out", subKey);
            return;
        }

        if (step == GraphicsStep.Write && !write(subKey, !snapshot.GpuDynamicPstate))
            throw new InvalidOperationException("The NVIDIA graphics setting could not be restored.");
    }

    /// <summary>
    /// Puts every setting back to <paramref name="snapshot"/>, then deletes the persisted record, so the next change
    /// records a fresh original instead of reloading this one. False when the settings are back and the record could
    /// not be deleted.
    /// </summary>
    /// <remarks>
    /// Restore All on Performance Mode and Undo Changes both put the original back through here (#1525), so the order
    /// — the settings first, the record only once they are back — is stated once. A restore that throws leaves the
    /// record where it is, which is what lets it be tried again.
    /// <para>Both pass the NVIDIA card the snapshot names as <paramref name="card"/>, the way their question found it,
    /// so the restore does what that question said: a card it left out is not written to after all, and one it promised
    /// is. Null looks for the card now. <paramref name="plan"/> is the power plan, the same way.</para>
    /// </remarks>
    internal async Task<bool> RestoreOriginalAsync(
        OriginalSnapshot snapshot, RecordedAdapter? card, RecordedPlan? plan = null, CancellationToken ct = default)
    {
        await RestoreFromSnapshotAsync(snapshot, card, plan, ct).ConfigureAwait(false);
        return DeleteSnapshot();
    }

    /// <summary>
    /// Releases the gate unless <see cref="Dispose"/> has already claimed it. Releasing a disposed
    /// <see cref="SemaphoreSlim"/> throws, and every call site is a <c>finally</c> block, where that
    /// would replace a clean shutdown — or a real error — with an unhandled exception. A powercfg call still
    /// running when SysManager closed ended that way, as <c>DnsService</c>'s already did not (#2742).
    /// </summary>
    private void ReleaseGate()
    {
        if (_disposed) return;
        try { _psGate.Release(); }
        catch (ObjectDisposedException) { /* disposed mid-request at shutdown */ }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _psGate.Dispose();
    }
}
