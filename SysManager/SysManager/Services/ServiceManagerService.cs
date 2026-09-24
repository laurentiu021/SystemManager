// SysManager · ServiceManagerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates Windows services, provides gaming recommendations, and
/// allows starting/stopping/changing startup type. All mutations require admin.
/// </summary>
public sealed partial class ServiceManagerService
{
    /// <summary>
    /// Gaming-oriented recommendations for common Windows services.
    /// Key = service name (case-insensitive), Value = (recommendation, reason).
    /// </summary>
    internal static readonly FrozenDictionary<string, (string Rec, string Reason)> GamingGuide = new Dictionary<string, (string Rec, string Reason)>(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] = ("safe-to-disable", "Superfetch — preloads apps into RAM. Disabling frees RAM for games and reduces disk I/O."),
        ["DiagTrack"] = ("safe-to-disable", "Connected User Experiences and Telemetry — sends usage data to Microsoft. No impact on functionality."),
        ["WSearch"] = ("safe-to-disable", "Windows Search indexer — uses CPU and disk in the background. Disable if you don't use Windows Search."),
        ["MapsBroker"] = ("safe-to-disable", "Downloaded Maps Manager — manages offline maps. Safe to disable if you don't use the Maps app."),
        ["Fax"] = ("safe-to-disable", "Windows Fax and Scan — unused on most gaming PCs."),
        ["RetailDemo"] = ("safe-to-disable", "Retail Demo Service — only used in store display mode."),
        ["WMPNetworkSvc"] = ("safe-to-disable", "Windows Media Player Network Sharing — shares media over the network. Rarely needed."),
        ["XblAuthManager"] = ("advanced", "Xbox Live Auth Manager — needed for Xbox Game Pass and Xbox Live features. Disable only if you don't use Xbox services."),
        ["XblGameSave"] = ("advanced", "Xbox Live Game Save — syncs game saves to Xbox Live. Disable only if you don't use Xbox cloud saves."),
        ["XboxGipSvc"] = ("advanced", "Xbox Accessory Management — manages Xbox controllers. Keep if you use Xbox controllers."),
        ["XboxNetApiSvc"] = ("advanced", "Xbox Live Networking — needed for Xbox multiplayer. Keep if you play Xbox games."),
        ["TabletInputService"] = ("safe-to-disable", "Touch Keyboard and Handwriting — safe to disable on desktops without touchscreens."),
        ["WbioSrvc"] = ("safe-to-disable", "Windows Biometric Service — fingerprint/face login. Disable if you don't use biometrics."),
        ["Spooler"] = ("safe-to-disable", "Print Spooler — manages print jobs. Disable if you don't have a printer."),
        ["RemoteRegistry"] = ("safe-to-disable", "Remote Registry — allows remote registry editing. Security risk, safe to disable."),
        ["lmhosts"] = ("safe-to-disable", "TCP/IP NetBIOS Helper — legacy name resolution. Safe to disable on modern networks."),
        ["Themes"] = ("keep-enabled", "Desktop themes and visual styles — disabling breaks the UI appearance."),
        ["AudioSrv"] = ("keep-enabled", "Windows Audio — required for all sound output."),
        ["Dhcp"] = ("keep-enabled", "DHCP Client — required for automatic IP address assignment."),
        ["Dnscache"] = ("keep-enabled", "DNS Client — caches DNS lookups for faster browsing."),
        ["EventLog"] = ("keep-enabled", "Windows Event Log — required for system diagnostics."),
        ["LanmanWorkstation"] = ("keep-enabled", "Workstation — required for network file sharing and SMB."),
        ["nsi"] = ("keep-enabled", "Network Store Interface — required for network connectivity."),
        ["Winmgmt"] = ("keep-enabled", "Windows Management Instrumentation — required by many apps and system tools."),
        ["wuauserv"] = ("keep-enabled", "Windows Update — keeps your system secure and up to date."),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enumerate all Windows services with their current state and gaming recommendations.
    /// </summary>
    public static List<ServiceEntry> GetAllServices()
    {
        var services = ServiceController.GetServices();
        var result = new List<ServiceEntry>(services.Length);

        foreach (var sc in services)
        {
            using (sc)
            {
                try
                {
                    var (rec, reason) = GamingGuide.TryGetValue(sc.ServiceName, out var guide)
                        ? guide
                        : ("", "");

                    var (safety, safetyDesc) = SafetyDatabase.GetServiceSafety(sc.ServiceName);
                    var startMode = sc.StartType;

                    result.Add(new ServiceEntry
                    {
                        Name = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Description = GetServiceDescription(sc),
                        Status = sc.Status.ToString(),
                        StartType = startMode.ToString(),
                        IsDelayedAutoStart = IsDelayedAutomatic(startMode, sc.ServiceName),
                        Recommendation = rec,
                        RecommendationReason = reason,
                        SafetyLevel = safety,
                        SafetyDescription = safetyDesc,
                        DependentServices = ReadDependentServices(sc),
                    });
                }
                catch (InvalidOperationException) { /* service disappeared — skip */ }
            }
        }

        return result.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The display names of the services Windows would stop along with <paramref name="sc"/>.
    /// </summary>
    /// <remarks>
    /// Each returned <see cref="ServiceController"/> is a live SCM handle, so they are disposed here —
    /// only the names are kept. Without that, one scan leaks a handle per dependency relationship (183 of
    /// 320 services depend on something, so the totals are not small).
    /// <para>Returns an empty list rather than throwing on the failures this enumeration really has:
    /// <see cref="InvalidOperationException"/> when the service is deleted between the enumeration and
    /// this read, and <see cref="System.ComponentModel.Win32Exception"/> when the SCM refuses the query.
    /// A row with no dependency information is worth far more than a scan that stops at the first
    /// service that vanished — 12 of 320 threw on a normal machine.</para>
    /// </remarks>
    private static IReadOnlyList<string> ReadDependentServices(ServiceController sc)
    {
        ServiceController[] dependents;
        try
        {
            dependents = sc.DependentServices;
        }
        catch (InvalidOperationException) { return []; }
        catch (System.ComponentModel.Win32Exception) { return []; }

        try
        {
            var names = new List<string>(dependents.Length);
            foreach (var dependent in dependents)
            {
                try { names.Add(dependent.DisplayName); }
                catch (InvalidOperationException) { /* this one vanished — the others still count */ }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return names;
        }
        finally
        {
            foreach (var dependent in dependents)
                dependent.Dispose();
        }
    }

    /// <summary>Start a service. Requires admin.</summary>
    public static async Task StartServiceAsync(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status == ServiceControllerStatus.Running ||
            sc.Status == ServiceControllerStatus.StartPending)
            return;

        try
        {
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new InvalidOperationException(
                $"Service '{serviceName}' did not start within 30 seconds. It may still be starting — check Services again in a moment.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // sc.Start() surfaces the underlying Win32 error (e.g. access denied,
            // dependency failure) as a Win32Exception. Normalize to the type the
            // ViewModel layer already handles.
            throw new InvalidOperationException(
                $"Could not start service '{serviceName}': {ex.Message}", ex);
        }
    }

    /// <summary>Stop a service. Requires admin.</summary>
    public static async Task StopServiceAsync(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.CanStop && sc.Status != ServiceControllerStatus.Stopped)
        {
            try
            {
                sc.Stop();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Service '{serviceName}' did not stop within 30 seconds. It may still be stopping — check Services again in a moment.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // The service state can change between the CanStop check and Stop(),
                // or the caller may lack rights — sc.Stop() then throws Win32Exception.
                throw new InvalidOperationException(
                    $"Could not stop service '{serviceName}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>Change the startup type of a service via sc.exe. Requires admin.</summary>
    public static async Task SetStartupTypeAsync(string serviceName, string startType, IPowerShellRunner ps, CancellationToken ct = default)
    {
        // SEC-006: Strict allowlist for service names — alphanumeric, spaces,
        // hyphens, underscores, dots, and dollar signs only (covers all valid
        // Windows service names including instance names like MSSQL$INSTANCE).
        if (string.IsNullOrWhiteSpace(serviceName) ||
            !ServiceNamePattern().IsMatch(serviceName))
            throw new ArgumentException("Invalid service name.", nameof(serviceName));

        var allowedTypes = new[] { "auto", "delayed-auto", "demand", "disabled" };
        if (!allowedTypes.Contains(startType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid start type: {startType}", nameof(startType));

        var exit = await ps.RunProcessAsync("sc.exe", $"config \"{serviceName}\" start= {startType}", ct, PowerShellRunner.OemEncoding)
            .ConfigureAwait(false);

        // sc.exe config can fail even when elevated — e.g. a TrustedInstaller-owned
        // service returns exit 5 (Access denied). Fail loud so the caller reports the
        // real outcome instead of a false "set to Disabled" success.
        if (exit != 0)
            throw new InvalidOperationException(
                $"Could not change startup type of '{serviceName}' to '{startType}' (sc.exe exit code {exit}).");
    }

    /// <summary>
    /// services.msc's name for an Automatic service whose start Windows delays until shortly after the other
    /// automatic services. <see cref="ServiceStartMode"/> has no member for it, so this is the name Disable
    /// snapshots for such a service and the name the ledger stores (#2428).
    /// </summary>
    internal const string AutomaticDelayedStart = "Automatic (Delayed Start)";

    /// <summary>
    /// Every startup type Enable can put back, with the sc.exe <c>start=</c> token that does it. The one list
    /// the restore path reads: <see cref="ServiceStartupLedgerService"/> stores only these names and
    /// <see cref="StartTypeToScToken"/> maps only these, so a type cannot be storable without being restorable.
    /// </summary>
    /// <remarks>
    /// The ledger and the mapping each kept their own list, and both had drifted from the allowlist in
    /// <see cref="SetStartupTypeAsync"/>. The ledger accepted Boot and System, and the mapping turned them
    /// into "boot" and "system", which that allowlist refuses — so a recorded Boot could only end in an
    /// <see cref="ArgumentException"/>. Neither occurs on this tab: they are driver start types, and
    /// <see cref="ServiceController.GetServices()"/> returns no drivers. The delayed start was missing the
    /// other way round: the allowlist accepts "delayed-auto", but nothing produced it.
    /// <para>The allowlist stays a separate literal, because it guards what reaches the sc.exe command line
    /// and must also admit "disabled"; <c>EveryEntryOfTheRestorableList_PassesTheAllowlistInFrontOfScExe</c>
    /// walks this list through it.</para>
    /// <para>Case-insensitive, as the ledger always was — its file is plain JSON a user can edit — so the
    /// mapping can no longer turn a record the ledger kept, such as "automatic", into Manual.</para>
    /// </remarks>
    internal static readonly FrozenDictionary<string, string> RestorableStartTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(ServiceStartMode.Automatic)] = "auto",
            [AutomaticDelayedStart] = "delayed-auto",
            [nameof(ServiceStartMode.Manual)] = "demand",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a startup type as the ledger stores it — a <see cref="RestorableStartTypes"/> name — to the
    /// sc.exe <c>start=</c> token that restores it, so a disabled service can be re-enabled to its exact
    /// previous startup type instead of always to Manual.
    /// Falls back to "demand" (Manual) for "Disabled" or any unrecognized value, since
    /// re-enabling to Disabled would be a no-op.
    /// </summary>
    internal static string StartTypeToScToken(string? startType) =>
        startType is not null && RestorableStartTypes.TryGetValue(startType, out var token) ? token : "demand";

    /// <summary>
    /// The startup type as services.msc names it: <see cref="ServiceEntry.StartType"/>, except that an
    /// Automatic service whose start Windows delays reads <see cref="AutomaticDelayedStart"/>.
    /// </summary>
    /// <remarks>
    /// What Disable snapshots, so Enable puts the delay back too, and what Enable reports afterwards — its
    /// prompt names the delayed type, so the line that confirms the result must not quietly drop it.
    /// </remarks>
    internal static string StartTypeWithDelay(ServiceEntry entry) =>
        entry.IsDelayedAutoStart && entry.StartType == nameof(ServiceStartMode.Automatic)
            ? AutomaticDelayedStart
            : entry.StartType;

    /// <summary>Refresh the status of a single service entry.</summary>
    public static void RefreshStatus(ServiceEntry entry)
    {
        try
        {
            using var sc = new ServiceController(entry.Name);
            entry.Status = sc.Status.ToString();
            var startMode = sc.StartType;
            entry.StartType = startMode.ToString();
            entry.IsDelayedAutoStart = IsDelayedAutomatic(startMode, entry.Name);
        }
        catch (InvalidOperationException) { entry.Status = "Unknown"; }
        // The status/start-type getters call into the SCM and can throw
        // Win32Exception (access denied, or the handle became invalid right after a
        // stop/disable). Mirror GetAllServices/RefreshAsync which already catch it,
        // so refreshing one entry after a mutation can't crash the command.
        catch (System.ComponentModel.Win32Exception) { entry.Status = "Unknown"; }
    }

    /// <summary>
    /// True only for an Automatic service whose start Windows delays. Windows keeps the delay setting for
    /// other start types too and ignores it there — the service control manager reported it on 6 of 201
    /// Manual services on the machine this was written on — so for them it is not asked about at all.
    /// </summary>
    private static bool IsDelayedAutomatic(ServiceStartMode mode, string serviceName) =>
        mode == ServiceStartMode.Automatic && ReadDelayedAutoStart(serviceName) is true;

    /// <summary>
    /// Whether Windows delays <paramref name="serviceName"/>'s automatic start, as the service control
    /// manager reports it, or null when it could not be asked.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceController"/> cannot say: <see cref="ServiceStartMode"/> has no delayed member, so a
    /// delayed service reads as plain Automatic (#2428). The flag is asked of the service control manager
    /// through <c>QueryServiceConfig2</c>, which is what services.msc shows, rather than read from the
    /// service's <c>DelayedAutostart</c> registry value, because the registry is not where every service
    /// keeps it. A per-user service instance has no value of its own and takes its template's: on the
    /// machine this was written on, the registry read disagreed with Windows for 1 of 322 services, the
    /// per-user clipboard service.
    /// <para>Needs no elevation. <c>SERVICE_QUERY_CONFIG</c> is the access
    /// <see cref="ServiceController.StartType"/> itself opens a service with, so every service the scan lists
    /// already grants it. The flag is reported whatever the start type; <see cref="IsDelayedAutomatic"/> is
    /// what decides it only counts for an Automatic one.</para>
    /// <para>Measured over the 108 Automatic services of a 322-service scan: 9 ms in all, about 5% of the
    /// scan. Opening the manager on each call is a third of that, too little to be worth threading one
    /// handle through every caller.</para>
    /// </remarks>
    internal static bool? ReadDelayedAutoStart(string serviceName)
    {
        using var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (manager.IsInvalid) return Unavailable(serviceName);

        using var service = NativeMethods.OpenService(manager, serviceName, NativeMethods.SERVICE_QUERY_CONFIG);
        if (service.IsInvalid) return Unavailable(serviceName);

        return NativeMethods.QueryServiceConfig2(
                   service, NativeMethods.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, out var info,
                   (uint)Marshal.SizeOf<NativeMethods.SERVICE_DELAYED_AUTO_START_INFO>(), out _)
            ? info.fDelayedAutostart != 0
            : Unavailable(serviceName);
    }

    /// <summary>
    /// Logs why the delayed-start flag could not be read, and reports it as unknown. A service removed
    /// between the scan and this read is the expected cause; the Win32 error is kept so that any other one
    /// shows up in the log instead of silently reading as "not delayed".
    /// </summary>
    private static bool? Unavailable(string serviceName)
    {
        Log.Debug("Delayed-start flag unavailable for {Service}: Win32 error {Error}",
            serviceName, Marshal.GetLastPInvokeError());
        return null;
    }

    private static string GetServiceDescription(ServiceController sc)
    {
        try
        {
            // SEC-M6: Validate service name before interpolating into registry path.
            // Although ServiceController.GetServices() returns names from the SCM
            // (trusted source), we defensively reject names with path separators or
            // registry metacharacters to prevent registry path traversal.
            var name = sc.ServiceName;
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains('\\') || name.Contains('/') ||
                name.Contains('\0'))
                return "";

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{name}");
            return ResolveDescription(
                key?.GetValue("Description")?.ToString(),
                NativeMethods.LoadIndirectString);
        }
        catch (System.Security.SecurityException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>
    /// Turns a raw registry <c>Description</c> value into readable text.
    /// </summary>
    /// <remarks>
    /// Windows rarely stores a service description as text. For most in-box services the value is an
    /// indirect resource reference — <c>@%SystemRoot%\system32\spoolsv.exe,-2</c> — and the sentence
    /// lives in that binary's resource table, which is how one binary serves every display language.
    /// Reading the value verbatim put the DLL path in the Description column and, worse, made it what
    /// the tab's free-text filter searched: typing "print" could not match the Print Spooler because
    /// its description was a path. This is not a non-English-only problem; English Windows stores the
    /// reference too, and <c>services.msc</c> resolves it at display time exactly like this.
    /// <para>The native call is a parameter so the decision table above it is unit-testable without
    /// depending on which binaries a given machine has. A failed resolve yields empty rather than the
    /// raw reference: the reference is noise to a reader and pollutes the filter, and an empty cell is
    /// already the normal case for the 365 services that ship no description at all.</para>
    /// </remarks>
    internal static string ResolveDescription(string? raw, Func<string, string?> resolveIndirect)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // '@' is the only marker of an indirect reference. Anything else is already a sentence and is
        // passed through untouched — 57 services on a stock install store real text here.
        if (raw[0] != '@') return raw;

        var resolved = resolveIndirect(raw);
        return string.IsNullOrWhiteSpace(resolved) ? "" : resolved;
    }

    private static partial class NativeMethods
    {
        /// <summary>Longest description observed is ~700 chars; 2048 leaves generous headroom.</summary>
        private const int IndirectStringBuffer = 2048;

        /// <summary>
        /// Resolves an indirect string of the form <c>@[path\]file,-resourceId</c>, expanding
        /// environment variables on the way, or returns null when it cannot.
        /// </summary>
        internal static string? LoadIndirectString(string source)
        {
            var buffer = new char[IndirectStringBuffer];

            // A missing binary, a missing resource id and a malformed reference all return E_FAIL with
            // the buffer untouched, so a non-zero HRESULT is the whole error story — there is no
            // partial-write case to defend against.
            if (SHLoadIndirectString(source, buffer, IndirectStringBuffer, IntPtr.Zero) != 0)
                return null;

            // The API null-terminates rather than returning a length.
            var end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end < 0 ? buffer.Length : end);
        }

        // Unicode-only: shlwapi exports no A/W pair for this one, so it correctly carries no EntryPoint
        // suffix. Verified against the live export table — the W-suffixed name does not resolve.
        [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int SHLoadIndirectString(
            string pszSource,
            [Out] char[] pszOutBuf,
            int cchOutBuf,
            IntPtr ppvReserved);

        // ── Service configuration: the delayed-start flag (#2428) ──

        internal const uint SC_MANAGER_CONNECT = 0x0001;
        internal const uint SERVICE_QUERY_CONFIG = 0x0001;
        internal const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_DELAYED_AUTO_START_INFO
        {
            /// <summary>A Win32 BOOL: non-zero when the automatic start is delayed.</summary>
            public int fDelayedAutostart;
        }

        /// <summary>A service control manager or service handle, released with CloseServiceHandle.</summary>
        internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeServiceHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }

        // OpenSCManager and OpenService have A/W pairs → pin the W entry points.
        [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeServiceHandle OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeServiceHandle OpenService(SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

        // QueryServiceConfig2 has an A/W pair too. This info level carries no text, so either export would
        // fill the same four bytes; the W one matches the two above.
        [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryServiceConfig2(
            SafeServiceHandle hService,
            uint dwInfoLevel,
            out SERVICE_DELAYED_AUTO_START_INFO lpBuffer,
            uint cbBufSize,
            out uint pcbBytesNeeded);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseServiceHandle(IntPtr hSCObject);
    }

    // \A…\z (absolute anchors): ^…$ would accept a trailing newline in the service
    // name, which is then interpolated into the sc.exe command line.
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[\w \-.$]+\z")]
    private static partial System.Text.RegularExpressions.Regex ServiceNamePattern();
}
