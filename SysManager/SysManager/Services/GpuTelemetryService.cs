// SysManager · GpuTelemetryService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;

namespace SysManager.Services;

/// <summary>
/// Reads every GPU's load and video memory from Windows' own vendor-neutral counters, named by DXGI.
/// </summary>
/// <remarks>
/// <para>Two native surfaces, both documented and public, and neither of them a vendor library:</para>
/// <list type="bullet">
///   <item><b>PDH</b> (<c>pdh.dll</c>) for <c>\GPU Engine(*)\Utilization Percentage</c> and
///   <c>\GPU Adapter Memory(*)\Dedicated Usage</c>. NOT <see cref="System.Diagnostics.PerformanceCounter"/>:
///   that API reads the legacy registry-based counter system and is BLIND to the GPU counters —
///   <c>PerformanceCounterCategory.Exists("GPU Engine")</c> returns false and <c>GetInstanceNames()</c> throws
///   "Category does not exist" on a machine where <c>typeperf</c> samples them happily. An implementation built
///   on it would read nothing and report 0%, which is the bug this service exists to fix (#2857).</item>
///   <item><b>DXGI</b> (<c>CreateDXGIFactory1</c>) to turn a counter's LUID into the card's name and its total
///   dedicated memory. The counters carry the LUID and nothing else; <c>Win32_VideoController</c> carries the
///   name and no LUID, so neither alone can say which card a reading belongs to on a machine with two.</item>
/// </list>
/// <para>Both are behind internal seams so the aggregation — the part with the actual rules in it — is
/// unit-testable without a GPU.</para>
/// </remarks>
public sealed partial class GpuTelemetryService : IGpuTelemetry, IDisposable
{
    /// <summary>The engine utilization and adapter memory readings, by counter instance name.</summary>
    internal interface IGpuCounters : IDisposable
    {
        /// <summary>One entry per <c>\GPU Engine</c> instance, or empty when the query is unusable.</summary>
        IReadOnlyList<(string Instance, double Value)> EngineUtilization();

        /// <summary>One entry per <c>\GPU Adapter Memory</c> instance, in BYTES.</summary>
        IReadOnlyList<(string Instance, double Bytes)> AdapterDedicatedUsage();
    }

    /// <summary>The display adapters DXGI reports.</summary>
    internal interface IGpuAdapters
    {
        IReadOnlyList<GpuAdapter> Adapters();
    }

    /// <summary>One adapter as DXGI describes it.</summary>
    /// <param name="Name">The adapter description.</param>
    /// <param name="LuidKey">
    /// The adapter's LUID in the exact text the counter instance names use, e.g.
    /// <c>luid_0x00000000_0x0002B42E</c> — verified against both counters and DXGI on a two-GPU machine.
    /// </param>
    /// <param name="TotalVramGB">Dedicated video memory on the card.</param>
    /// <param name="IsSoftware">True for a software adapter (Microsoft Basic Render Driver), which is not a GPU.</param>
    internal sealed record GpuAdapter(string Name, string LuidKey, double TotalVramGB, bool IsSoftware);

    private readonly IGpuCounters _counters;
    private readonly IGpuAdapters _adapters;
    private readonly Lock _gate = new();
    private bool _disposed;

    public GpuTelemetryService() : this(new PdhGpuCounters(), new DxgiGpuAdapters()) { }

    /// <summary>Test seam: builds the service over supplied readings instead of the real native ones.</summary>
    internal GpuTelemetryService(IGpuCounters counters, IGpuAdapters adapters)
    {
        _counters = counters;
        _adapters = adapters;
    }

    /// <inheritdoc/>
    public IReadOnlyList<GpuUsage> ReadUsage()
    {
        lock (_gate)
        {
            if (_disposed) return [];
            return Aggregate(_counters.EngineUtilization(), _counters.AdapterDedicatedUsage(), _adapters.Adapters());
        }
    }

    /// <summary>
    /// Turns raw counter instances into one reading per hardware adapter, busiest first.
    /// </summary>
    /// <remarks>
    /// Task Manager's own arithmetic, which is not a plain average or a plain sum:
    /// <list type="number">
    ///   <item>every <c>\GPU Engine</c> instance belongs to one process AND one engine on one adapter
    ///   (<c>pid_45000_luid_0x00000000_0x0002B42E_phys_0_eng_0_engtype_3D</c>), so the instances sharing an
    ///   (adapter, engine type) are SUMMED — several processes can each use part of the same engine;</item>
    ///   <item>the adapter's figure is then the BUSIEST engine type, not the sum of all of them: 3D and video
    ///   decode run in parallel, so adding them could report 150% on a card that is not saturated.</item>
    /// </list>
    /// Pure and static so the rules above are assertable against fed instance names, with no GPU involved.
    /// </remarks>
    internal static IReadOnlyList<GpuUsage> Aggregate(
        IReadOnlyList<(string Instance, double Value)> engine,
        IReadOnlyList<(string Instance, double Bytes)> memory,
        IReadOnlyList<GpuAdapter> adapters)
    {
        var hardware = adapters.Where(a => !a.IsSoftware).ToList();
        if (hardware.Count == 0) return [];

        // (luid, engtype) -> summed utilization.
        var perEngineType = new Dictionary<(string Luid, string EngineType), double>();
        foreach (var (instance, value) in engine)
        {
            var luid = LuidOf(instance);
            var engineType = EngineTypeOf(instance);
            if (luid is null || engineType is null) continue;
            var key = (luid, engineType);
            perEngineType[key] = perEngineType.TryGetValue(key, out var sum) ? sum + value : value;
        }

        var usedBytes = new Dictionary<string, double>();
        foreach (var (instance, bytes) in memory)
        {
            var luid = LuidOf(instance);
            if (luid is null) continue;
            // One instance per adapter, but take the largest rather than assuming: a repeated LUID would
            // otherwise depend on enumeration order.
            usedBytes[luid] = usedBytes.TryGetValue(luid, out var seen) ? Math.Max(seen, bytes) : bytes;
        }

        var readings = new List<GpuUsage>(hardware.Count);
        foreach (var adapter in hardware)
        {
            var busiest = perEngineType
                .Where(e => string.Equals(e.Key.Luid, adapter.LuidKey, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value)
                .DefaultIfEmpty(0)
                .Max();

            var used = usedBytes.TryGetValue(adapter.LuidKey, out var bytes) ? bytes : 0;
            readings.Add(new GpuUsage(
                adapter.Name,
                Math.Clamp(busiest, 0, 100),
                used / 1024d / 1024d / 1024d,
                adapter.TotalVramGB));
        }

        // Busiest first, so a caller that shows one GPU shows the one doing the work — on a laptop with
        // switchable graphics that is whichever card the foreground app actually landed on.
        return readings.OrderByDescending(r => r.UsagePercent).ToList();
    }

    /// <summary>The <c>luid_0x..._0x...</c> part of a counter instance name, or null if it carries none.</summary>
    internal static string? LuidOf(string instance)
    {
        const string marker = "luid_";
        var at = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        // luid_0xHHHHHHHH_0xLLLLLLLL — take the marker plus the two hex words, and nothing after them.
        var rest = instance[at..];
        var parts = rest.Split('_');
        if (parts.Length < 3) return null;
        return string.Concat(parts[0], "_", parts[1], "_", parts[2]);
    }

    /// <summary>The engine type suffix of a <c>\GPU Engine</c> instance name, or null if it carries none.</summary>
    internal static string? EngineTypeOf(string instance)
    {
        const string marker = "engtype_";
        var at = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : instance[(at + marker.Length)..];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _counters.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PDH — the counters themselves
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The two GPU counters, read through PDH with the query kept open between reads.
    /// </summary>
    /// <remarks>
    /// <c>Utilization Percentage</c> is a RATE: PDH needs two collections spaced in time to produce a value, so
    /// the query is opened once and collected once per read, with the PREVIOUS read as the baseline. Opening,
    /// sleeping and closing per read would cost a second of wall-clock on a 300 ms poll and read nothing on the
    /// first pass. The constructor takes the seeding collection, the way the CPU-load baseline is seeded.
    /// </remarks>
    internal sealed partial class PdhGpuCounters : IGpuCounters
    {
        private const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
        private const string MemoryPath = @"\GPU Adapter Memory(*)\Dedicated Usage";

        private readonly nint _query;
        private readonly nint _engineCounter;
        private readonly nint _memoryCounter;
        private readonly bool _usable;
        private bool _disposed;

        internal PdhGpuCounters()
        {
            var status = NativeMethods.PdhOpenQuery(null, 0, out _query);
            if (status != 0)
            {
                Log.Debug("GPU counters unavailable: PdhOpenQuery returned 0x{Status:X8}", status);
                return;
            }

            // The ENGLISH counter names: a localised Windows translates the display names, and the paths above
            // are the English ones, which PdhAddEnglishCounter resolves on every locale.
            var engineAdded = NativeMethods.PdhAddEnglishCounter(_query, EnginePath, 0, out _engineCounter);
            var memoryAdded = NativeMethods.PdhAddEnglishCounter(_query, MemoryPath, 0, out _memoryCounter);
            if (engineAdded != 0 || memoryAdded != 0)
            {
                Log.Debug("GPU counters unavailable: PdhAddEnglishCounter returned 0x{Engine:X8}/0x{Memory:X8} "
                          + "(a machine with no WDDM 2.x driver publishes neither)", engineAdded, memoryAdded);
                NativeMethods.PdhCloseQuery(_query);
                _query = 0;
                return;
            }

            // Seed the rate counter's baseline. A failure here is not fatal: the next collection re-seeds.
            NativeMethods.PdhCollectQueryData(_query);
            _usable = true;
        }

        /// <inheritdoc/>
        public IReadOnlyList<(string Instance, double Value)> EngineUtilization() => Collect(_engineCounter);

        /// <inheritdoc/>
        public IReadOnlyList<(string Instance, double Bytes)> AdapterDedicatedUsage() => Collect(_memoryCounter);

        private IReadOnlyList<(string, double)> Collect(nint counter)
        {
            if (!_usable || _disposed) return [];

            // One collection serves both counters on the same query; collecting per counter would halve the
            // interval each rate is measured over. Harmless to repeat, so each call collects and both counters
            // read from the same freshly collected data.
            var status = NativeMethods.PdhCollectQueryData(_query);
            if (status != 0)
            {
                Log.Debug("GPU counters: PdhCollectQueryData returned 0x{Status:X8}", status);
                return [];
            }

            uint size = 0;
            status = NativeMethods.PdhGetFormattedCounterArray(counter, NativeMethods.PdhFmtDouble, ref size,
                                                               out _, 0);
            if (status != NativeMethods.PdhMoreData)
            {
                // PDH_CSTATUS_NO_INSTANCE / NO_DATA on a machine between driver states: no reading, not a fault.
                Log.Debug("GPU counters: sizing call returned 0x{Status:X8}", status);
                return [];
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = NativeMethods.PdhGetFormattedCounterArray(counter, NativeMethods.PdhFmtDouble, ref size,
                                                                   out var count, buffer);
                if (status != 0)
                {
                    Log.Debug("GPU counters: array call returned 0x{Status:X8}", status);
                    return [];
                }

                var stride = Marshal.SizeOf<NativeMethods.PdhFmtCounterValueItemDouble>();
                var items = new List<(string, double)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NativeMethods.PdhFmtCounterValueItemDouble>(
                        buffer + (i * stride));
                    var name = Marshal.PtrToStringUni(item.NamePtr);
                    if (!string.IsNullOrEmpty(name)) items.Add((name, item.Value.DoubleValue));
                }
                return items;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_query != 0) NativeMethods.PdhCloseQuery(_query);
        }

        private static partial class NativeMethods
        {
            internal const uint PdhFmtDouble = 0x00000200;
            internal const uint PdhMoreData = 0x800007D2;

            [StructLayout(LayoutKind.Sequential)]
            internal struct PdhFmtCounterValueDouble
            {
                public uint CStatus;
                public double DoubleValue;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct PdhFmtCounterValueItemDouble
            {
                public nint NamePtr;
                public PdhFmtCounterValueDouble Value;
            }

            // Explicit W entry points: these have A/W variants, and the ANSI one would mangle a localised
            // instance name.
            [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
            internal static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

            [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
            internal static partial uint PdhAddEnglishCounter(nint query, string fullPath, nint userData,
                                                              out nint counter);

            [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
            internal static partial uint PdhGetFormattedCounterArray(nint counter, uint format,
                                                                     ref uint bufferSize, out uint itemCount,
                                                                     nint itemBuffer);

            // No A/W variants on either of these.
            [LibraryImport("pdh.dll")]
            internal static partial uint PdhCollectQueryData(nint query);

            [LibraryImport("pdh.dll")]
            internal static partial uint PdhCloseQuery(nint query);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DXGI — which card each LUID is
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The display adapters, from DXGI, with their LUIDs in the counters' own text form.</summary>
    /// <remarks>
    /// Enumerated fresh on each call rather than cached: a card can be added, removed or reset while the app
    /// runs, and this list is read once per poll against a 300 ms budget, where the enumeration is microseconds.
    /// </remarks>
    internal sealed partial class DxgiGpuAdapters : IGpuAdapters
    {
        /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE — the Basic Render Driver rather than a card.</summary>
        private const uint AdapterFlagSoftware = 2;

        /// <inheritdoc/>
        public IReadOnlyList<GpuAdapter> Adapters()
        {
            var factoryGuid = typeof(NativeMethods.IDxgiFactory1).GUID;
            var hr = NativeMethods.CreateDXGIFactory1(in factoryGuid, out var factoryPtr);
            if (hr != 0 || factoryPtr == 0)
            {
                Log.Debug("DXGI unavailable: CreateDXGIFactory1 returned 0x{Hr:X8}", hr);
                return [];
            }

            var factory = (NativeMethods.IDxgiFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                var adapters = new List<GpuAdapter>();
                for (uint index = 0; ; index++)
                {
                    if (factory.EnumAdapters1(index, out var adapter) != 0 || adapter is null) break;
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0) continue;
                        adapters.Add(new GpuAdapter(
                            (desc.Description ?? "").Trim(),
                            LuidKey(desc.AdapterLuidHigh, desc.AdapterLuidLow),
                            (double)desc.DedicatedVideoMemory / 1024 / 1024 / 1024,
                            (desc.Flags & AdapterFlagSoftware) != 0));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
                return adapters;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
                Marshal.Release(factoryPtr);
            }
        }

        /// <summary>A LUID in the text the GPU counter instance names use.</summary>
        /// <remarks>
        /// High word first, both zero-padded to eight hex digits, upper case — matched against real counter
        /// instances and real DXGI descriptions on a machine with an NVIDIA and an Intel adapter, where the two
        /// agreed exactly (<c>luid_0x00000000_0x0002B42E</c>).
        /// </remarks>
        internal static string LuidKey(int high, uint low) =>
            string.Create(CultureInfo.InvariantCulture, $"luid_0x{high:X8}_0x{low:X8}");

        private static partial class NativeMethods
        {
            [LibraryImport("dxgi.dll")]
            internal static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct DxgiAdapterDesc1
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                public string Description;
                public uint VendorId;
                public uint DeviceId;
                public uint SubSysId;
                public uint Revision;
                public nuint DedicatedVideoMemory;
                public nuint DedicatedSystemMemory;
                public nuint SharedSystemMemory;
                public uint AdapterLuidLow;
                public int AdapterLuidHigh;
                public uint Flags;
            }

            /// <summary>IDXGIAdapter1 — only <c>GetDesc1</c> is called, but every earlier slot must be declared.</summary>
            [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface IDxgiAdapter1
            {
                // IDXGIObject
                void SetPrivateData();
                void SetPrivateDataInterface();
                void GetPrivateData();
                void GetParent();
                // IDXGIAdapter
                void EnumOutputs();
                void GetDesc();
                void CheckInterfaceSupport();
                // IDXGIAdapter1
                [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
            }

            /// <summary>IDXGIFactory1 — only <c>EnumAdapters1</c> is called.</summary>
            [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            internal interface IDxgiFactory1
            {
                // IDXGIObject
                void SetPrivateData();
                void SetPrivateDataInterface();
                void GetPrivateData();
                void GetParent();
                // IDXGIFactory
                void EnumAdapters();
                void MakeWindowAssociation();
                void GetWindowAssociation();
                void CreateSwapChain();
                void CreateSoftwareAdapter();
                // IDXGIFactory1
                [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDxgiAdapter1? adapter);
                [PreserveSig] int IsCurrent();
            }
        }
    }
}
