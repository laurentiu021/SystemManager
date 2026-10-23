// SysManager · NvidiaGpuReaderTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="NvidiaGpuReader"/>: one set-up, cached handles, and handles replaced after a read fails.
/// </summary>
/// <remarks>
/// NVIDIA's API cannot be called from a test — it needs the driver, and its set-up is global to the process — so the
/// reader takes its API behind an internal seam and these tests hand it stand-ins that count their calls and can be
/// made to throw the way a stale handle does (#2829).
/// </remarks>
public class NvidiaGpuReaderTests
{
    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when a test says so.</summary>
    /// <remarks>
    /// The reader needs <c>GetTimestamp</c> and <c>GetElapsedTime</c>, and the second derives from
    /// <c>TimestampFrequency</c>, so overriding those two is the whole seam — the same hand-written shape
    /// <c>EtaCalculatorTests</c> and <c>EtwBandwidthSourceTests</c> use.
    /// </remarks>
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>A stand-in GPU handle that can be made to throw the way an invalidated one does.</summary>
    private sealed class FakeGpu(string name, double usage, double? temperature) : NvidiaGpuReader.INvidiaGpu
    {
        /// <summary>Set to make every member throw, as NVIDIA does once it has invalidated the handle.</summary>
        public bool Stale { get; set; }

        public string Name => Stale ? throw Invalid() : name;
        public double UsagePercent => Stale ? throw Invalid() : usage;
        public (double UsedGB, double TotalGB) Memory => Stale ? throw Invalid() : (2.0, 8.0);
        public double? TemperatureC => Stale ? throw Invalid() : temperature;

        private static InvalidOperationException Invalid() => new("the handle is no longer valid");
    }

    /// <summary>A stand-in for NVIDIA's API that counts its set-ups and enumerations, and how many callers are
    /// inside it at once.</summary>
    private sealed class FakeApi : NvidiaGpuReader.INvidiaApi
    {
        private readonly Queue<IReadOnlyList<NvidiaGpuReader.INvidiaGpu>> _generations = new();
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _secondEntrant = new(0);
        private int _inside;

        public int Initializations { get; private set; }
        public int Enumerations { get; private set; }

        /// <summary>The most callers that have been inside a call at the same time.</summary>
        public int MostAtOnce { get; private set; }

        /// <summary>
        /// Set to have the first caller inside a call run this — a test starts a second read on another thread with
        /// it — and then wait, up to <see cref="WaitForACompanion"/>, for that second caller to arrive. Under a lock
        /// it cannot, so the wait times out and <see cref="MostAtOnce"/> stays 1; unlocked it arrives at once.
        /// </summary>
        public Action? WhileInsideTheFirstCall { get; set; }

        /// <summary>How long that wait is bounded to, so an unlocked reader fails rather than hanging the run.</summary>
        public static readonly TimeSpan WaitForACompanion = TimeSpan.FromSeconds(2);

        /// <summary>Set to make the set-up fail, as it does with no NVIDIA driver installed.</summary>
        public bool InitializeFails { get; set; }

        /// <summary>Set to make enumeration fail.</summary>
        public bool EnumerationFails { get; set; }

        /// <summary>Queues what the next enumeration returns; the last one is returned from then on.</summary>
        public FakeApi Returning(params NvidiaGpuReader.INvidiaGpu[] gpus)
        {
            _generations.Enqueue(gpus);
            return this;
        }

        public void Initialize()
        {
            Enter();
            try
            {
                lock (_gate) Initializations++;
                if (InitializeFails) throw new DllNotFoundException("nvapi64.dll");
            }
            finally { Leave(); }
        }

        public IReadOnlyList<NvidiaGpuReader.INvidiaGpu> Gpus()
        {
            Enter();
            try
            {
                lock (_gate) Enumerations++;
                if (EnumerationFails) throw new InvalidOperationException("the API is not answering");
                lock (_gate) return _generations.Count > 1 ? _generations.Dequeue() : _generations.Peek();
            }
            finally { Leave(); }
        }

        /// <summary>Records this caller, and holds the first one open for a companion when a test asked for one.</summary>
        private void Enter()
        {
            var inside = Interlocked.Increment(ref _inside);
            lock (_gate) MostAtOnce = Math.Max(MostAtOnce, inside);

            // Only a SECOND caller releases: releasing on the first would satisfy its own wait and prove nothing.
            if (inside > 1)
            {
                _secondEntrant.Release();
                return;
            }

            if (WhileInsideTheFirstCall is not { } startTheSecond) return;

            // Only the first call of the test holds itself open, or the second read would do it again from inside.
            WhileInsideTheFirstCall = null;
            startTheSecond();
            _secondEntrant.Wait(WaitForACompanion);
        }

        private void Leave() => Interlocked.Decrement(ref _inside);
    }

    private static (NvidiaGpuReader Reader, TestTimeProvider Clock) Reader(FakeApi api)
    {
        var clock = new TestTimeProvider();
        return (new NvidiaGpuReader(api, clock), clock);
    }

    [Fact]
    public void ReadUsage_ReportsEachGpusLoadAndMemory()
    {
        var api = new FakeApi().Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, _) = Reader(api);

        var gpu = Assert.Single(reader.ReadUsage());

        Assert.Equal(("Test GPU", 42.0, 2.0, 8.0), (gpu.Name, gpu.UsagePercent, gpu.MemoryUsedGB, gpu.MemoryTotalGB));
    }

    /// <summary>
    /// Many reads set the API up once and enumerate once.
    /// </summary>
    /// <remarks>
    /// The three callers it replaces each set the global API up themselves and two of them enumerated on every
    /// read: the Dashboard polls at 300 ms, the sampler every 10 s.
    /// </remarks>
    [Fact]
    public void ManyReads_SetUpAndEnumerateOnce()
    {
        var api = new FakeApi().Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, _) = Reader(api);

        for (var i = 0; i < 5; i++)
        {
            reader.ReadUsage();
            reader.ReadTemperatures();
        }

        Assert.Equal((1, 1), (api.Initializations, api.Enumerations));
    }

    /// <summary>
    /// A set-up that fails is not tried again, and every read answers with nothing.
    /// </summary>
    /// <remarks>No NVIDIA GPU or driver is the normal case on an AMD or Intel machine.</remarks>
    [Fact]
    public void FailedSetUp_IsNotRetried()
    {
        var api = new FakeApi { InitializeFails = true }.Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, clock) = Reader(api);

        Assert.Empty(reader.ReadUsage());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(reader.ReadUsage());

        Assert.Equal((1, 0), (api.Initializations, api.Enumerations));
    }

    /// <summary>
    /// A handle NVIDIA has invalidated is dropped and replaced, and the reading comes from the new one.
    /// </summary>
    /// <remarks>
    /// The Dashboard's defect: it enumerated once and kept the handle for good, so after a driver reset every
    /// 300 ms tick threw while the GPU number kept the last value it had read and was still shown as live.
    /// </remarks>
    [Fact]
    public void StaleHandle_IsReplaced_AfterTheCooldown()
    {
        var stale = new FakeGpu("Old GPU", 10, 40);
        var api = new FakeApi().Returning(stale).Returning(new FakeGpu("New GPU", 70, 65));
        var (reader, clock) = Reader(api);

        Assert.Single(reader.ReadUsage());          // the first generation answers
        stale.Stale = true;

        Assert.Empty(reader.ReadUsage());           // the failure drops the handles
        Assert.Empty(reader.ReadUsage());           // and the cooldown holds off the next enumeration
        Assert.Equal(1, api.Enumerations);

        clock.Advance(NvidiaGpuReader.RetryAfter);
        var gpu = Assert.Single(reader.ReadUsage());

        Assert.Equal("New GPU", gpu.Name);
        Assert.Equal(2, api.Enumerations);
    }

    /// <summary>
    /// An enumeration that comes back empty is not repeated until the cooldown has passed.
    /// </summary>
    /// <remarks>
    /// An AMD or Intel machine with NVIDIA's driver still installed answers the set-up and then lists no GPU, and
    /// at 300 ms a retry on every tick would be the enumeration the handle cache exists to avoid.
    /// </remarks>
    [Fact]
    public void NoGpus_IsNotEnumeratedOnEveryRead()
    {
        var api = new FakeApi().Returning();
        var (reader, clock) = Reader(api);

        Assert.Empty(reader.ReadUsage());
        Assert.Empty(reader.ReadUsage());
        Assert.Equal(1, api.Enumerations);

        clock.Advance(NvidiaGpuReader.RetryAfter);
        Assert.Empty(reader.ReadUsage());
        Assert.Equal(2, api.Enumerations);
    }

    /// <summary>An enumeration that throws is held off by the same cooldown.</summary>
    [Fact]
    public void FailedEnumeration_IsHeldOffByTheCooldown()
    {
        var api = new FakeApi { EnumerationFails = true }.Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, clock) = Reader(api);

        Assert.Empty(reader.ReadUsage());
        Assert.Empty(reader.ReadUsage());
        Assert.Equal(1, api.Enumerations);

        api.EnumerationFails = false;
        clock.Advance(NvidiaGpuReader.RetryAfter);

        Assert.Single(reader.ReadUsage());
    }

    /// <summary>
    /// A GPU whose sensors all read zero is left out of the temperatures without its handle being dropped.
    /// </summary>
    /// <remarks>
    /// Nothing to report is not a failure: treated as one, a machine whose GPU exposes no thermal sensor would
    /// have its handles replaced every two seconds for the life of the process.
    /// </remarks>
    [Fact]
    public void GpuWithNoTemperature_IsLeftOut_WithoutDroppingTheHandles()
    {
        var api = new FakeApi().Returning(new FakeGpu("Quiet GPU", 42, null));
        var (reader, _) = Reader(api);

        Assert.Empty(reader.ReadTemperatures());
        Assert.Single(reader.ReadUsage());

        Assert.Equal(1, api.Enumerations);
    }

    [Fact]
    public void ReadTemperatures_ReportsEachGpusSensor()
    {
        var api = new FakeApi().Returning(new FakeGpu("First GPU", 10, 55), new FakeGpu("Second GPU", 20, 61));
        var (reader, _) = Reader(api);

        var temperatures = reader.ReadTemperatures();

        Assert.Equal(new (string, double)[] { ("First GPU", 55), ("Second GPU", 61) },
            temperatures.Select(t => (t.Name, t.TemperatureC)).ToArray());
    }

    /// <summary>
    /// Concurrent first reads set the API up once.
    /// </summary>
    /// <remarks>
    /// The race the reader exists for: the Dashboard's loop and the Resource History sampler both start by awaiting
    /// the same first system snapshot, so they came out of it together and set the global API up at almost the same
    /// moment, and the one that lost had its GPU reading off for the session.
    /// </remarks>
    [Fact]
    public void ConcurrentFirstReads_SetUpTheApiOnce()
    {
        var api = new FakeApi().Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, _) = Reader(api);

        Parallel.For(0, 32, _ =>
        {
            reader.ReadUsage();
            reader.ReadTemperatures();
        });

        Assert.Equal((1, 1), (api.Initializations, api.Enumerations));
    }

    /// <summary>
    /// A read that arrives while another is inside NVIDIA's API waits for it, rather than calling in beside it.
    /// </summary>
    /// <remarks>
    /// The fault this reader exists for, and the proof has to be made to happen rather than hoped for: the window
    /// between reading the init-once flag and setting it is a few instructions wide, so the test above passed with
    /// the lock removed. Here the first caller inside the API starts the second read itself and then waits, bounded,
    /// for it to arrive. Under the reader's lock it cannot arrive, so that wait times out and nothing is ever
    /// inside the API twice; without the lock it arrives at once and <see cref="FakeApi.MostAtOnce"/> is 2.
    /// </remarks>
    [Fact]
    public async Task ASecondRead_DuringTheFirst_WaitsRatherThanCallingInBesideIt()
    {
        var api = new FakeApi().Returning(new FakeGpu("Test GPU", 42, 55));
        var (reader, _) = Reader(api);
        Task<IReadOnlyList<NvidiaGpuUsage>>? second = null;
        api.WhileInsideTheFirstCall = () => second = Task.Run(reader.ReadUsage);

        Assert.Single(reader.ReadUsage());

        Assert.NotNull(second);
        // Bounded: a reader that deadlocked rather than queued must fail the test, not hang the run.
        var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(second, finished);
        Assert.Single(await second);
        Assert.Equal(1, api.MostAtOnce);
        Assert.Equal((1, 1), (api.Initializations, api.Enumerations));
    }

    /// <summary>The cooldown is long enough to matter against the Dashboard's poll and short enough to recover.</summary>
    [Fact]
    public void RetryAfter_IsLongerThanAPollAndShortEnoughToRecover()
    {
        Assert.True(NvidiaGpuReader.RetryAfter >= TimeSpan.FromSeconds(1),
            "a cooldown near the 300 ms poll interval would re-enumerate almost every tick");
        Assert.True(NvidiaGpuReader.RetryAfter <= TimeSpan.FromSeconds(10),
            "the GPU must come back while the user is still looking at the tab");
    }
}
