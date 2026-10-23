// SysManager · NvidiaGpuReader
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;

namespace SysManager.Services;

/// <summary>
/// Reads NVIDIA's API behind one lock, with one set-up and one set of GPU handles for the whole app.
/// </summary>
/// <remarks>
/// Two faults this replaces (#2829). The three callers each set the global API up themselves, unlocked, and the
/// Dashboard and the Resource History sampler come out of the same first system snapshot together, so a losing
/// race left one of them with its GPU reading off for the session. And the Dashboard enumerated the GPU once and
/// kept the handle for good: NVIDIA invalidates handles when the GPU or the display is reconfigured, for example
/// after a driver reset, after which every 300 ms tick threw, the catch only logged, and the GPU number kept the
/// last value it had read while still being shown as live.
/// <para>So the handles are cached, as the Dashboard's were for the cost, but a read that fails drops them and the
/// next read after <see cref="RetryAfter"/> enumerates again. Until then the readings are empty, which each caller
/// shows as unavailable rather than as a number that stopped moving. A generation that fails is logged once, not
/// once per tick: at 300 ms that was about 10,000 lines an hour, which pushes the history a bug report needs out
/// of the rotating log.</para>
/// </remarks>
public sealed class NvidiaGpuReader : INvidiaGpuReader
{
    /// <summary>How long a failed generation of handles is left alone before enumerating again.</summary>
    /// <remarks>
    /// A driver reset takes a moment to settle, and the Dashboard asks every 300 ms, so retrying on every tick
    /// would be the full enumeration the handle cache exists to avoid. Two seconds is short enough that the GPU
    /// comes back while the user is still looking at the tab.
    /// </remarks>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private readonly INvidiaApi _api;
    private readonly TimeProvider _time;

    // Everything below is guarded by this lock: the API's set-up and enumeration are global native calls that are
    // not documented as thread-safe, and the three callers poll on their own schedules (300 ms, 2 s, 10 s).
    private readonly Lock _gate = new();

    private bool _initTried;
    private bool _available;
    private IReadOnlyList<INvidiaGpu>? _gpus;

    // Nullable rather than 0-for-never: a TimeProvider is free to start its timestamps at 0, and a sentinel of 0
    // then reads as "no failure yet" and lets the cooldown through on the very first one.
    private long? _failedAt;
    private bool _failureLogged;

    /// <summary>Creates the reader over NVIDIA's real API and the system clock.</summary>
    public NvidiaGpuReader() : this(new NvApi(), TimeProvider.System) { }

    /// <summary>Creates the reader over <paramref name="api"/>, so a test drives the set-up and a stale handle.</summary>
    internal NvidiaGpuReader(INvidiaApi api, TimeProvider time)
    {
        _api = api;
        _time = time;
    }

    /// <inheritdoc/>
    public IReadOnlyList<NvidiaGpuUsage> ReadUsage() => Read(gpu =>
    {
        var (usedGB, totalGB) = gpu.Memory;
        return new NvidiaGpuUsage(gpu.Name, gpu.UsagePercent, usedGB, totalGB);
    });

    /// <inheritdoc/>
    public IReadOnlyList<NvidiaGpuTemperature> ReadTemperatures() => Read(gpu =>
        gpu.TemperatureC is { } celsius ? new NvidiaGpuTemperature(gpu.Name, celsius) : null);

    /// <summary>
    /// Reads <paramref name="of"/> from every GPU, dropping the handles when one read fails.
    /// </summary>
    /// <remarks>
    /// A reading <paramref name="of"/> returns null for is left out rather than treated as a failure: a GPU whose
    /// thermal sensors all read zero is answering, it just has nothing to show.
    /// </remarks>
    private IReadOnlyList<T> Read<T>(Func<INvidiaGpu, T?> of) where T : class
    {
        lock (_gate)
        {
            var gpus = Handles();
            if (gpus is null) return [];

            List<T> readings = [];
            try
            {
                foreach (var gpu in gpus)
                    if (of(gpu) is { } reading)
                        readings.Add(reading);

                // A whole pass without a fault: the next failure is worth a line of its own again.
                _failureLogged = false;
                return readings;
            }
            catch (Exception ex) when (IsNvidiaFailure(ex))
            {
                // The handles are stale (a driver reset, a driver update, a display reconfigured) or the driver is
                // gone. Dropped, so the next read past the cooldown enumerates again instead of throwing forever.
                _gpus = null;
                _failedAt = _time.GetTimestamp();
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Log.Debug("NVIDIA GPU read failed, dropping the handles and enumerating again: {Error}", ex.Message);
                }
                return [];
            }
        }
    }

    /// <summary>
    /// The cached handles, enumerating them when there are none, or null while there is nothing to read.
    /// </summary>
    /// <remarks>Called with <see cref="_gate"/> held.</remarks>
    private IReadOnlyList<INvidiaGpu>? Handles()
    {
        if (!_initTried)
        {
            _initTried = true;
            try
            {
                _api.Initialize();
                _available = true;
            }
            catch (Exception ex) when (IsNvidiaFailure(ex))
            {
                // No NVIDIA GPU or driver is the normal case on an AMD or Intel machine, so it is recorded once and
                // every later read returns empty without touching the API again.
                _available = false;
                Log.Debug("NVIDIA GPU API unavailable: {Error}", ex.Message);
            }
        }

        if (!_available) return null;
        if (_gpus is { Count: > 0 }) return _gpus;

        // GetElapsedTime over a stored timestamp, not a wall-clock difference: GetUtcNow moves when the user or NTP
        // changes the clock, which would either freeze this cooldown or expire it instantly.
        if (_failedAt is { } failedAt && _time.GetElapsedTime(failedAt) < RetryAfter) return null;

        try
        {
            var gpus = _api.Gpus();
            _gpus = gpus.Count > 0 ? gpus : null;
            _failedAt = _gpus is null ? _time.GetTimestamp() : null;
            return _gpus;
        }
        catch (Exception ex) when (IsNvidiaFailure(ex))
        {
            _failedAt = _time.GetTimestamp();
            if (!_failureLogged)
            {
                _failureLogged = true;
                Log.Debug("NVIDIA GPU enumeration failed: {Error}", ex.Message);
            }
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is how NVIDIA's API fails rather than a fault of this app's own.
    /// </summary>
    /// <remarks>
    /// Broad on purpose, and each one for a reason the three previous call sites had already met: the wrapper raises
    /// its own exception for a refused call, <see cref="DllNotFoundException"/> when no driver is installed,
    /// <see cref="TypeInitializationException"/> when its static set-up fails, and
    /// <see cref="InvalidOperationException"/> for a handle it will no longer serve.
    /// </remarks>
    private static bool IsNvidiaFailure(Exception ex) =>
        ex is NvAPIWrapper.Native.Exceptions.NVIDIAApiException
            or DllNotFoundException
            or TypeInitializationException
            or InvalidOperationException
            or ArgumentException;

    /// <summary>NVIDIA's API, as the reader uses it.</summary>
    internal interface INvidiaApi
    {
        /// <summary>Sets the API up. Throws when there is no NVIDIA driver to talk to.</summary>
        void Initialize();

        /// <summary>Enumerates the GPUs, which invalidates nothing already handed out.</summary>
        IReadOnlyList<INvidiaGpu> Gpus();
    }

    /// <summary>One GPU handle. Every member may throw once NVIDIA has invalidated it.</summary>
    internal interface INvidiaGpu
    {
        /// <summary>The adapter name.</summary>
        string Name { get; }

        /// <summary>How busy the GPU is, 0 to 100.</summary>
        double UsagePercent { get; }

        /// <summary>Dedicated video memory in use and on the card, in GB.</summary>
        (double UsedGB, double TotalGB) Memory { get; }

        /// <summary>The first thermal sensor reading more than zero, or null when none does.</summary>
        double? TemperatureC { get; }
    }

    /// <summary>The real NVIDIA API, through NvAPIWrapper.</summary>
    private sealed class NvApi : INvidiaApi
    {
        public void Initialize() => NvAPIWrapper.NVIDIA.Initialize();

        public IReadOnlyList<INvidiaGpu> Gpus() =>
            NvAPIWrapper.GPU.PhysicalGPU.GetPhysicalGPUs().Select(gpu => (INvidiaGpu)new NvGpu(gpu)).ToList();
    }

    /// <summary>One real GPU handle.</summary>
    private sealed class NvGpu(NvAPIWrapper.GPU.PhysicalGPU gpu) : INvidiaGpu
    {
        public string Name => gpu.FullName;

        public double UsagePercent => gpu.UsageInformation.GPU.Percentage;

        public (double UsedGB, double TotalGB) Memory
        {
            get
            {
                var memory = gpu.MemoryInformation;
                var total = memory.DedicatedVideoMemoryInkB / 1024.0 / 1024.0;
                var used = (memory.DedicatedVideoMemoryInkB - memory.AvailableDedicatedVideoMemoryInkB)
                           / 1024.0 / 1024.0;
                return (used, total);
            }
        }

        public double? TemperatureC => gpu.ThermalInformation.ThermalSensors
            .FirstOrDefault(sensor => sensor.CurrentTemperature > 0)?.CurrentTemperature;
    }
}
