// SysManager · ProcessManagerService — enumerate and manage running processes
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Enumerates running processes with CPU/memory usage. Kill is opt-in
/// and requires confirmation in the ViewModel layer.
/// </summary>
public sealed partial class ProcessManagerService
{
    private readonly Func<IReadOnlyDictionary<int, IntPtr>> _mainWindowsByPid;
    private readonly Func<IntPtr, bool> _isHungWindow;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _readDescription;
    private readonly Func<IReadOnlyList<ListedProcess>> _listProcesses;
    private readonly Action<TimeSpan> _wait;

    // The CPU time each process had at the last read, by the process: its ID and when it started. Guarded by
    // _sampleLock, which also keeps two refreshes, the tab's timer's and another caller's, from reading at once.
    private readonly Lock _sampleLock = new();
    private Dictionary<(int Pid, long Created), long> _cpuAtLast = [];
    private long _lastSampledAt;
    private bool _sampled;

    // The buffer the process list is read into, kept from one read to the next. Under _sampleLock.
    private byte[]? _listBuffer;

    public ProcessManagerService() : this(QueryMainWindowsByPid, IsHungAppWindow) { }

    /// <summary>
    /// Test seam for the two window probes, so "one enumeration per refresh" can be asserted by counting
    /// rather than measured with a stopwatch.
    /// </summary>
    /// <param name="readDescription">
    /// The description in a program file's version resource, by its path: the file's own unless a test passes a read,
    /// so one can fail for a single row.
    /// </param>
    /// <param name="listProcesses">
    /// Every process, as the system's process list gives it: <see cref="QueryProcesses"/> unless a test passes a list.
    /// </param>
    /// <param name="wait">How a read waits out <see cref="CpuWindow"/>: <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    internal ProcessManagerService(Func<IReadOnlyDictionary<int, IntPtr>> mainWindowsByPid,
                                   Func<IntPtr, bool> isHungWindow, TimeProvider? timeProvider = null,
                                   Func<string, string>? readDescription = null,
                                   Func<IReadOnlyList<ListedProcess>>? listProcesses = null,
                                   Action<TimeSpan>? wait = null)
    {
        _mainWindowsByPid = mainWindowsByPid;
        _isHungWindow = isHungWindow;
        _time = timeProvider ?? TimeProvider.System;
        _readDescription = readDescription ?? FileDescription.Read;
        _listProcesses = listProcesses ?? QueryProcesses;
        _wait = wait ?? Thread.Sleep;
    }

    /// <summary>
    /// What a listed process is: its ID, when it started, and its name. Windows gives a closed process's ID to the next
    /// one it starts, so the ID alone is not the process (#2514, #2719).
    /// </summary>
    /// <param name="Pid">The process ID.</param>
    /// <param name="StartTime">When it started, or <c>default</c> when Windows would not say.</param>
    /// <param name="Name">Its name, as <see cref="Process.ProcessName"/> gives it.</param>
    public readonly record struct ProcessIdentity(int Pid, DateTime StartTime, string Name)
    {
        /// <summary>The process <paramref name="entry"/> lists.</summary>
        public static ProcessIdentity Of(ProcessEntry entry) => new(entry.Pid, entry.StartTime, entry.Name);
    }

    // known: the processes the caller already shows. Their description and path do not change, and the view model
    // keeps them on the surviving entry (ReconcileInto), so for those the expensive MainModule/FileVersionInfo/
    // File.Exists reads are skipped and only the volatile metrics refreshed. A process, not an ID: one that took the
    // ID of a listed process that exited had its reads skipped as that one, and was listed bare for its whole
    // lifetime (#2719).
    public Task<IReadOnlyList<ProcessEntry>> SnapshotAsync(IReadOnlySet<ProcessIdentity>? known = null, CancellationToken ct = default)
        => Task.Run(() => Snapshot(known, ct), ct);

    private IReadOnlyList<ProcessEntry> Snapshot(IReadOnlySet<ProcessIdentity>? known, CancellationToken ct)
    {
        List<ProcessEntry> results = [];
        var (processes, cpuPercent) = Sample();

        // One window enumeration for the whole refresh. Both properties this replaces resolve the
        // main window by walking every top-level window in the session, and they do NOT share the
        // result: measured on 479 processes, reading Responding cost 41.6 ms, reading
        // MainWindowHandle cost 43.6 ms, and reading both cost 81.4 ms — additive, so the walk ran
        // twice per process. Responding then also sent WM_NULL through SendMessageTimeout with
        // SMTO_ABORTIFHUNG and a 5000 ms timeout, which is most expensive in exactly the case the
        // column exists for: an app that has genuinely stopped pumping messages. One pass plus
        // IsHungAppWindow costs 0.4 ms for the same answers.
        var mainWindows = processes.Count > 0 ? _mainWindowsByPid() : new Dictionary<int, IntPtr>();

        foreach (var p in processes)
        {
            if (ct.IsCancellationRequested) break;

            // A process with no top-level window is not hung, it simply has no message loop to
            // stall — which is also what Responding reported for it (it returns true when the
            // main window handle is zero).
            var hasWindow = mainWindows.TryGetValue(p.Pid, out var mainWindow);

            var entry = new ProcessEntry
            {
                Pid = p.Pid,
                Name = p.Name,
                MemoryBytes = p.WorkingSet,
                ThreadCount = p.Threads,
                HasMainWindow = hasWindow,
                Status = hasWindow && _isHungWindow(mainWindow) ? "Not responding" : "Running",
                CpuPercent = cpuPercent(p),
                // From the list, for every process. A handle's StartTime threw for most of the system's and other
                // users' processes without administrator rights, which left them without one.
                StartTime = p.Created == 0 ? default : DateTime.FromFileTime(p.Created),
            };

            // Identity (description/path) is static per process and the view model keeps it
            // for a process it already shows, so only pay the expensive MainModule /
            // FileVersionInfo / File.Exists cost for a process it doesn't.
            if (known is null || !known.Contains(new ProcessIdentity(p.Pid, entry.StartTime, entry.Name)))
            {
                entry.FilePath = MainModulePath(p.Pid);
                // Its own read, so a program whose file was renamed while it runs loses only its description,
                // not the whole list (#2718).
                entry.Description = FileDescription.Of(entry.FilePath, _readDescription);
                entry.CanOpenFileLocation = !string.IsNullOrWhiteSpace(entry.FilePath)
                                           && System.IO.File.Exists(entry.FilePath);
            }

            results.Add(entry);
        }

        return results;
    }

    /// <summary>The shortest stretch a read measures CPU over: a read sooner than this after the last waits.</summary>
    internal static readonly TimeSpan CpuWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>The oldest read a new one measures CPU from. Older, it reads anew and waits out the window.</summary>
    internal static readonly TimeSpan CpuBaselineMaxAge = TimeSpan.FromSeconds(3);

    /// <summary>
    /// One read of every process, and how much of the processor each used since the read before.
    /// </summary>
    /// <remarks>
    /// <para>Each refresh opened every process three times, its CPU time twice with 100 ms between, which it waited
    /// out on a pool thread, and its start time once, and built an object for every thread on the PC to count them
    /// (#2854). The system's process list gives the name, the threads, the memory, the start time and the CPU time of
    /// every process in one query, with no handle.</para>
    /// <para>The CPU is measured from the read before, which the tab's timer makes a second ago, so it is the use over
    /// that second rather than over a tenth of it. A read sooner than <see cref="CpuWindow"/> after the last waits out
    /// the rest. The read before is every caller's, the tab's and the Dashboard's "Why is it slow?" alike, so it is
    /// used only while it is at most <see cref="CpuBaselineMaxAge"/> old; past that, a figure would be the average
    /// since whichever caller read last, the mistake #2790 fixed for the Dashboard's vitals. Without one, as on the
    /// first refresh, a read is taken and <see cref="CpuWindow"/> waited out, as every refresh did.</para>
    /// <para>A process is matched to its CPU time at the read before by its ID and when it started, since Windows gives
    /// the ID of a process that ended to the next one. One with no earlier reading, started since, shows 0 until the
    /// next read. The idle process's CPU time is the processor's idle time, so it shows 0, as before.</para>
    /// <para>A monotonic timestamp, not the wall clock, for the stretch measured: a clock step, daylight saving or a
    /// correction, would corrupt every process's figure for that refresh (#2855).</para>
    /// </remarks>
    private (IReadOnlyList<ListedProcess> Processes, Func<ListedProcess, double> CpuPercent) Sample()
    {
        lock (_sampleLock)
        {
            var since = _sampled ? _time.GetElapsedTime(_lastSampledAt) : TimeSpan.MaxValue;
            if (since > CpuBaselineMaxAge)
            {
                Remember(_listProcesses(), _time.GetTimestamp());
                _wait(CpuWindow);
            }
            else if (since < CpuWindow)
            {
                _wait(CpuWindow - since);
            }

            var before = _cpuAtLast;
            var processes = _listProcesses();
            var sampledAt = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastSampledAt, sampledAt).Ticks;
            Remember(processes, sampledAt);

            var cores = Environment.ProcessorCount;
            return (processes, p =>
                p.Pid != 0 && elapsed > 0 && before.TryGetValue((p.Pid, p.Created), out var cpuThen)
                && p.CpuTime >= cpuThen
                    ? Math.Round((p.CpuTime - cpuThen) / (double)elapsed / cores * 100, 1)
                    : 0);
        }
    }

    // Under _sampleLock.
    private void Remember(IReadOnlyList<ListedProcess> processes, long sampledAt)
    {
        var cpu = new Dictionary<(int Pid, long Created), long>(processes.Count);
        foreach (var p in processes) cpu[(p.Pid, p.Created)] = p.CpuTime;
        _cpuAtLast = cpu;
        _lastSampledAt = sampledAt;
        _sampled = true;
    }

    /// <summary>The file process <paramref name="pid"/> runs from, or "" when Windows will not say.</summary>
    private static string MainModulePath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "";
        }
        catch (ArgumentException) { return ""; }                        // it exited
        catch (InvalidOperationException) { return ""; }                // access denied or process exited
        catch (System.ComponentModel.Win32Exception) { return ""; }    // access denied or process exited
    }

    // ── The system's process list ────────────────────────────────────────────

    /// <summary>One process as the system's process list gives it.</summary>
    /// <param name="Pid">The process ID.</param>
    /// <param name="Name">Its name, as <see cref="Process.ProcessName"/> gives it.</param>
    /// <param name="Threads">How many threads it has.</param>
    /// <param name="WorkingSet">Its working set, in bytes.</param>
    /// <param name="Created">When it started, as a FILETIME, or 0 when the list gives none.</param>
    /// <param name="CpuTime">Its user and kernel time together, in 100-nanosecond units.</param>
    internal readonly record struct ListedProcess(
        int Pid, string Name, int Threads, long WorkingSet, long Created, long CpuTime);

    // NtQuerySystemInformation's SystemProcessInformation class, and the status it gives a buffer too small.
    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Every process, from one call of <c>NtQuerySystemInformation</c>, or none when it fails.</summary>
    /// <remarks>Called under <see cref="_sampleLock"/>, which guards the buffer it reuses.</remarks>
    internal IReadOnlyList<ListedProcess> QueryProcesses()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = _listBuffer ??= new byte[1024 * 1024];
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var address = pin.AddrOfPinnedObject();
                var status = NtQuerySystemInformation(
                    SystemProcessInformationClass, address, buffer.Length, out var length);
                if (status == StatusInfoLengthMismatch)
                {
                    // More processes can start before the next call, so a little room more than it asked for.
                    _listBuffer = new byte[Math.Max(buffer.Length * 2, length + (64 * 1024))];
                    continue;
                }

                if (status < 0)
                {
                    Log.Debug("ProcessManager: NtQuerySystemInformation failed with 0x{Status:X8}", status);
                    return [];
                }

                return ReadProcesses(buffer.AsSpan(0, Math.Min(length, buffer.Length)), address);
            }
            finally { pin.Free(); }
        }

        Log.Debug("ProcessManager: the process list kept outgrowing its buffer");
        return [];
    }

    /// <summary>
    /// The processes in a buffer <c>NtQuerySystemInformation</c> filled with <c>SYSTEM_PROCESS_INFORMATION</c>
    /// entries, which was at <paramref name="address"/> when it did.
    /// </summary>
    /// <remarks>
    /// Each entry's image name points into the same buffer, by the address the buffer had when the system filled it,
    /// so a name is read from the buffer at that pointer less <paramref name="address"/>. A pointer outside the buffer
    /// is not followed. Pure, so the layout is unit-tested on bytes laid out as Windows lays them.
    /// </remarks>
    internal static List<ListedProcess> ReadProcesses(ReadOnlySpan<byte> buffer, nint address)
    {
        var processes = new List<ListedProcess>(512);
        var size = Marshal.SizeOf<SystemProcessInformation>();
        var offset = 0;
        while (offset + size <= buffer.Length)
        {
            var info = MemoryMarshal.Read<SystemProcessInformation>(buffer[offset..]);
            var pid = (int)info.UniqueProcessId;
            processes.Add(new ListedProcess(pid, NameOf(pid, info.ImageName, buffer, address),
                (int)info.NumberOfThreads, (long)(ulong)info.WorkingSetSize, info.CreateTime,
                info.UserTime + info.KernelTime));

            if (info.NextEntryOffset == 0) break;
            offset += (int)info.NextEntryOffset;
        }
        return processes;
    }

    /// <summary>
    /// A process's name as <see cref="Process.ProcessName"/> gives it: its image name without the folder or a
    /// trailing ".exe", "Idle" for the idle process, and the ID for a process the list gives no name.
    /// </summary>
    private static string NameOf(int pid, UnicodeString image, ReadOnlySpan<byte> buffer, nint address)
    {
        var start = (long)image.Buffer - address;
        if (image.Buffer == 0 || image.Length == 0 || start < 0 || start + image.Length > buffer.Length)
            return pid == 0 ? "Idle" : pid.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<char> name = MemoryMarshal.Cast<byte, char>(buffer.Slice((int)start, image.Length & ~1));
        name = name[(name.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToString();
    }

    /// <summary><c>SYSTEM_PROCESS_INFORMATION</c>, field for field as far as <c>WorkingSetSize</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr UniqueProcessKey;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
    }

    /// <summary><c>UNICODE_STRING</c>: a length in bytes and a pointer to the characters.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // No A/W variant (not a string function) → no EntryPoint suffix needed.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    /// <summary>
    /// Fills <see cref="ProcessEntry.Signature"/> and <see cref="ProcessEntry.SignatureDetail"/> for every
    /// entry whose image path was read, reusing <paramref name="cache"/> across calls.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately NOT called from <see cref="Snapshot"/>.</b> Asking Windows about a signature costs
    /// ~25 ms per file and does not get cheaper on a warm pass, so a first load over ~82 distinct images
    /// took about three and a half seconds — spent before the list appeared, on the tab someone opens
    /// BECAUSE something is wrong. <c>ProcessManagerViewModel</c> now renders the list first and calls this
    /// in small batches behind it, so the caller controls how much work happens between yields. That is why
    /// the cache is a parameter: a batched caller needs one cache across its batches, and a method that
    /// owned a local one would re-verify the same executable in every batch.
    /// <para><b>The cache is the caller's for the length of one fill pass, not the tab's lifetime.</b> A
    /// pass ends when nothing is left unverified, and only newly-started processes are ever unverified, so
    /// the cache lives exactly as long as the work it serves. Keeping one for the whole session would buy
    /// nothing — the second pass has almost nothing to do — and would owe an answer for a file replaced on
    /// disk in the meantime.</para>
    /// <para>Keyed on the path rather than the PID because a machine runs one browser as a dozen processes
    /// from one executable, and that is the common case rather than the exception.</para>
    /// <para>Uses the same <see cref="Helpers.SignatureVerdict"/> as <c>StartupService.VerifySignatures</c>,
    /// so the two tabs cannot describe one certificate in two ways.</para>
    /// </remarks>
    internal static void VerifySignatures(
        IReadOnlyList<ProcessEntry> entries,
        Dictionary<string, (SignatureTrust Trust, string Detail)> cache) =>
        ApplySignatures(ReadSignatures(entries, cache));

    /// <summary>
    /// The verdict for each entry that has a path, as <see cref="VerifySignatures"/> reaches it, with no row changed.
    /// </summary>
    /// <remarks>
    /// The slow half, for a worker thread. The rows are what the grid shows, so <see cref="ApplySignatures"/> writes the
    /// verdicts on the window's thread afterwards: the fill ran <see cref="VerifySignatures"/> on the worker, which set
    /// the bound columns there while the once-a-second refresh changed the same rows on the window's thread (#2765).
    /// </remarks>
    internal static List<(ProcessEntry Entry, SignatureTrust Trust, string Detail)> ReadSignatures(
        IReadOnlyList<ProcessEntry> entries,
        Dictionary<string, (SignatureTrust Trust, string Detail)> cache)
    {
        List<(ProcessEntry Entry, SignatureTrust Trust, string Detail)> verdicts = [];
        foreach (var entry in entries)
        {
            // No path: a system process whose module list Windows refused, which is most of them without
            // elevation. Nothing was checked, so the pill does not render — see ProcessEntry.Signature.
            if (entry.FilePath.Length == 0) continue;

            if (!cache.TryGetValue(entry.FilePath, out var verdict))
            {
                verdict = Helpers.SignatureVerdict.Describe(entry.FilePath);
                cache[entry.FilePath] = verdict;
            }

            verdicts.Add((entry, verdict.Trust, verdict.Detail));
        }
        return verdicts;
    }

    /// <summary>Writes verdicts <see cref="ReadSignatures"/> read onto their rows, on the thread that owns them.</summary>
    internal static void ApplySignatures(IEnumerable<(ProcessEntry Entry, SignatureTrust Trust, string Detail)> verdicts)
    {
        foreach (var (entry, trust, detail) in verdicts)
        {
            entry.Signature = trust;
            entry.SignatureDetail = detail;
        }
    }

    /// <summary>A fresh verdict cache for one fill pass, keyed the way <see cref="VerifySignatures"/> keys.</summary>
    /// <remarks>
    /// Exposed so a caller does not have to know the comparer. Getting it wrong would be silent: an ordinal
    /// comparer makes <c>C:\Windows\explorer.exe</c> and <c>C:\WINDOWS\EXPLORER.EXE</c> two separate
    /// entries, which costs a second verification rather than producing a wrong answer — the kind of miss
    /// no test would notice.
    /// </remarks>
    internal static Dictionary<string, (SignatureTrust Trust, string Detail)> NewSignatureCache() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps each process id to its main window, in one pass over the session's top-level windows.
    /// </summary>
    /// <remarks>
    /// The enumeration collects each visible top-level window, and <see cref="MainWindowsOf"/> decides which
    /// is a process's main window.
    /// <para>Processes with no window are simply absent from the result rather than present with a zero
    /// handle: on this machine 15 of 479 processes had one, so a map of only the windows that exist is
    /// both smaller and the honest shape.</para>
    /// </remarks>
    private static IReadOnlyDictionary<int, IntPtr> QueryMainWindowsByPid()
    {
        List<TopLevelWindow> windows = [];
        var className = new char[64];

        // EnumWindows walks in Z-order, so the first window accepted for a process is its topmost one —
        // the same one Process.MainWindowHandle settles on.
        var enumerated = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            if (GetWindowThreadProcessId(window, out var pid) == 0) return true;

            var length = GetClassName(window, className, className.Length);
            var isFrame = className.AsSpan(0, Math.Max(length, 0)).SequenceEqual(FrameClass);
            var hosted = isFrame ? FindWindowEx(window, IntPtr.Zero, CoreWindowClass, null) : IntPtr.Zero;
            uint hostedPid = 0;
            if (hosted != IntPtr.Zero) GetWindowThreadProcessId(hosted, out hostedPid);

            windows.Add(new TopLevelWindow(window, (int)pid, GetWindow(window, GwOwner) != IntPtr.Zero,
                CloakOf(window), isFrame, hosted, (int)hostedPid));
            return true;
        }, IntPtr.Zero);

        // The callback never returns false, so false here is a real failure rather than an early stop. It
        // leaves an empty map, which reads as "nothing has a window" and reports every process as
        // responding — worth a line in the log rather than silence. Read immediately: the callback's own
        // GetWindowThreadProcessId also sets the last error, and EnumWindows returns after every callback.
        if (!enumerated)
            Log.Debug("ProcessManager: EnumWindows failed with {Error}; window state unavailable this refresh",
                Marshal.GetLastWin32Error());

        return MainWindowsOf(windows);
    }

    /// <summary>A visible top-level window, with what deciding a process's main window needs from it.</summary>
    /// <param name="Handle">The window.</param>
    /// <param name="ProcessId">The process that owns it.</param>
    /// <param name="HasOwner">True for an owned window: a dialog or tooltip, not a main window.</param>
    /// <param name="Cloaked">
    /// DWM's cloak flags for it: 0, or <c>DWM_CLOAKED_APP</c>, <c>_SHELL</c> and <c>_INHERITED</c>.
    /// </param>
    /// <param name="IsFrame">True for an <c>ApplicationFrameWindow</c>, the frame a Store app is drawn in.</param>
    /// <param name="Hosted">The <c>Windows.UI.Core.CoreWindow</c> inside the frame, or zero.</param>
    /// <param name="HostedProcessId">The process that owns <paramref name="Hosted"/>, or zero.</param>
    internal readonly record struct TopLevelWindow(
        IntPtr Handle, int ProcessId, bool HasOwner, uint Cloaked, bool IsFrame, IntPtr Hosted, int HostedProcessId);

    /// <summary>
    /// The main window of each process, from its visible top-level windows in Z-order: the first one that is
    /// not owned and that Windows does not keep hidden, as <c>Process.MainWindowHandle</c> picks it.
    /// </summary>
    /// <remarks>
    /// A dialog or tooltip is owned by its parent and is skipped, which is what stops a modal prompt from being
    /// mistaken for an app's main window. Two kinds of window are not what they look like (#2869):
    /// <list type="bullet">
    /// <item>A Store app such as Settings or Calculator is drawn in an <c>ApplicationFrameWindow</c> owned by
    /// ApplicationFrameHost, with the app's own <c>CoreWindow</c> inside it. The frame is credited to the app
    /// that owns that window, which is also the window the hung check reads, since the frame's process keeps
    /// answering when the app stops. A frame with no app inside credits nothing, so ApplicationFrameHost, a
    /// system process, is not an app; a Store app that is not on screen has a top-level <c>CoreWindow</c> of its
    /// own.</item>
    /// <item><c>IsWindowVisible</c> is also true for a window DWM cloaks. One cloaked by its own app, or inside a
    /// cloaked one, is hidden and makes nothing an app. One the shell cloaks still counts: that is a window on
    /// another virtual desktop, or a Store app's <c>CoreWindow</c> while it is not on screen.</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyDictionary<int, IntPtr> MainWindowsOf(IEnumerable<TopLevelWindow> windows)
    {
        var byPid = new Dictionary<int, IntPtr>();
        foreach (var window in windows)
        {
            if (window.HasOwner) continue;
            if ((window.Cloaked & (DwmCloakedApp | DwmCloakedInherited)) != 0) continue;
            if (window.IsFrame)
            {
                if (window.Hosted != IntPtr.Zero && window.HostedProcessId != 0
                    && window.HostedProcessId != window.ProcessId)
                {
                    byPid.TryAdd(window.HostedProcessId, window.Hosted);
                }
                continue;
            }
            byPid.TryAdd(window.ProcessId, window.Handle);
        }
        return byPid;
    }

    /// <summary>DWM's cloak flags for <paramref name="window"/>, or 0 when DWM cannot say.</summary>
    private static uint CloakOf(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(uint)) == 0 ? cloaked : 0;

    /// <summary>The class of the frame a Store app is drawn in.</summary>
    private const string FrameClass = "ApplicationFrameWindow";

    /// <summary>The class of a Store app's own window.</summary>
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>DWMWA_CLOAKED.</summary>
    private const uint DwmwaCloaked = 14;

    /// <summary>DWM_CLOAKED_APP: the window's own app cloaked it.</summary>
    internal const uint DwmCloakedApp = 1;

    /// <summary>DWM_CLOAKED_SHELL: the shell cloaked it, as it does a window on another virtual desktop.</summary>
    internal const uint DwmCloakedShell = 2;

    /// <summary>DWM_CLOAKED_INHERITED: the window is inside a cloaked one.</summary>
    internal const uint DwmCloakedInherited = 4;

    /// <summary>Callback for <see cref="EnumWindows"/>; returning false stops the enumeration.</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    /// <summary>GW_OWNER — the owner of a window, or zero for an unowned top-level window.</summary>
    private const uint GwOwner = 4;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr window, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? title);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, int size);

    /// <summary>
    /// True when a window has stopped pumping messages — the non-blocking answer to the question
    /// <c>Process.Responding</c> answered by sending it one and waiting up to five seconds.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsHungAppWindow(IntPtr window);

    /// <summary>What happened when <see cref="KillProcess(int, DateTime, string)"/> tried to end a process.</summary>
    public enum KillOutcome
    {
        /// <summary>Windows ended the process.</summary>
        Ended,

        /// <summary>
        /// The process that was meant is not running: nothing has that ID, or the ID now belongs to another process,
        /// one that did not start as listed or has another name. Nothing was ended.
        /// </summary>
        NotRunning,

        /// <summary>Windows refused and the process is still running. Ending it usually needs administrator rights.</summary>
        Refused,
    }

    /// <summary>
    /// Ends the process with <paramref name="pid"/>, and only that process. <see cref="KillProcess(int, DateTime, string)"/>
    /// with no name to compare, for a list that has none.
    /// </summary>
    /// <param name="pid">The process ID.</param>
    /// <param name="startTime">When the process started, as listed, or <c>default</c> when Windows would not say.</param>
    public static KillOutcome KillProcess(int pid, DateTime startTime) => KillProcess(pid, startTime, name: null);

    /// <summary>
    /// Ends the process with <paramref name="pid"/>, and only if it is still the process that was listed.
    /// </summary>
    /// <param name="pid">The process ID.</param>
    /// <param name="startTime">
    /// When the process started, as listed, or <c>default</c> when Windows would not say. Windows reuses a process ID
    /// once its process exits, and the confirmation can stay open for as long as the user likes, so by the time the
    /// ID is acted on it can name a different program. A process that did not start as listed, by
    /// <see cref="StartedAsListed(Process, DateTime)"/>, is left alone and reported as <see cref="KillOutcome.NotRunning"/>.
    /// </param>
    /// <param name="name">
    /// The listed process's name, or null when the list has none to compare. A process of another name is left alone in
    /// the same way: where Windows gives no start time, the name is what still tells two processes apart.
    /// </param>
    /// <remarks>
    /// Not <c>Kill(entireProcessTree: true)</c>, which this was until #2498. That also ends every process the target
    /// started, one generation at a time, behind a prompt that names one process. Every program opened from the
    /// taskbar or the Start menu is a child of <c>explorer.exe</c>. After SysManager's own administrator relaunch it
    /// sits outside that tree, so ending Explorer ended all of those programs. Started from Explorer, it sits inside
    /// the tree, and .NET refuses to end a tree that contains its caller. Nothing was ended, and the tab blamed
    /// administrator rights. Task Manager's End task ends the process alone, and so does File Lock Detector.
    /// <para><see cref="Process.Kill()"/> returns quietly for a process that has already exited, so a
    /// <see cref="System.ComponentModel.Win32Exception"/> means the process is still running and Windows refused.</para>
    /// </remarks>
    public static KillOutcome KillProcess(int pid, DateTime startTime, string? name)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (name is not null && !string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                return KillOutcome.NotRunning;
            if (!StartedAsListed(p, startTime)) return KillOutcome.NotRunning;
            p.Kill();
            return KillOutcome.Ended;
        }
        catch (ArgumentException) { return KillOutcome.NotRunning; }
        catch (InvalidOperationException) { return KillOutcome.NotRunning; }
        catch (System.ComponentModel.Win32Exception) { return KillOutcome.Refused; }
    }

    /// <summary>
    /// Whether the process that holds a listed ID now, <paramref name="p"/>, started as the listed one did.
    /// </summary>
    /// <param name="p">The process that holds the ID now.</param>
    /// <param name="listedStart">When the listed process started, or <c>default</c> when Windows would not say.</param>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The list has a start time, and Windows will not give this process's: each caller decides what that means, as
    /// before.
    /// </exception>
    /// <exception cref="InvalidOperationException">The process has exited.</exception>
    internal static bool StartedAsListed(Process p, DateTime listedStart) => StartedAsListed(listedStart, () => p.StartTime);

    /// <summary>
    /// <see cref="StartedAsListed(Process, DateTime)"/>, with the read of the start time of the process that holds the ID
    /// now passed in.
    /// </summary>
    /// <remarks>
    /// A start time the list has decides it. One the list does not have is one Windows would not give with the rights
    /// SysManager runs with, as for most of the system's and other users' processes without administrator rights, so a
    /// process whose start time Windows gives now is another one. Without one, every check skipped the comparison, so
    /// a confirmation left open while such a process exited acted on whatever Windows gave its ID to next (#2719).
    /// </remarks>
    internal static bool StartedAsListed(DateTime listedStart, Func<DateTime> startNow)
    {
        if (listedStart != default) return startNow() == listedStart;
        try
        {
            startNow();
            return false;
        }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    /// <summary>When <paramref name="p"/> started, or null when Windows will not say.</summary>
    /// <exception cref="InvalidOperationException">The process has exited.</exception>
    internal static DateTime? StartTimeOf(Process p)
    {
        try { return p.StartTime; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>
    /// Open the file location of a process in Explorer.
    /// </summary>
    public static void OpenFileLocation(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath)) return;
        try
        {
            // Dispose the returned Process handle — we don't track the launched explorer.
            Process.Start(new ProcessStartInfo
            {
                FileName = SystemPaths.ResolveSystemTool("explorer.exe"),
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            })?.Dispose();
        }
        catch (InvalidOperationException ex) { Log.Warning(ex, "Failed to open file location: {Path}", filePath); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warning(ex, "Failed to open file location: {Path}", filePath); }
    }

    /// <summary>Renders a process list as CSV with a header row.</summary>
    /// <remarks>
    /// Both the raw byte count and the formatted size are exported, and the raw start time alongside its
    /// display string, because a spreadsheet sorts text: "9.8 GB" lands below "10 MB" and an em dash sorts
    /// against real dates. The display columns are what the user recognises from the tab; the raw ones are
    /// what makes the file answer questions the screen already answered.
    /// </remarks>
    public static string ToCsv(IEnumerable<ProcessEntry> entries, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(culture);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, culture, "PID", "Name", "Description", "Memory", "Memory (bytes)", "CPU %", "Threads",
            "Status", "Started", "Started (raw)", "Category", "Safety", "Signature", "Path");
        foreach (var e in entries)
        {
            Csv.AppendRow(sb, culture,
                e.Pid.ToString(culture),
                e.Name,
                e.PlainDescription.Length > 0 ? e.PlainDescription : e.Description,
                e.MemoryDisplay,
                e.MemoryBytes.ToString(culture),
                e.CpuPercent.ToString("F1", culture),
                e.ThreadCount.ToString(culture),
                e.Status,
                e.StartTimeDisplay,
                e.StartTime == default
                    ? null
                    : e.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.Category,
                e.SafetyLevel,
                e.SignatureDetail.Length > 0 ? e.Signature.ToString() : "",
                e.FilePath);
        }
        return sb.ToString();
    }
}
