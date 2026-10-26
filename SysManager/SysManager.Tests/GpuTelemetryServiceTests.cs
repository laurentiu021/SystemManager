// SysManager · GpuTelemetryServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="GpuTelemetryService"/>: the aggregation rules, and the instance-name parsing they
/// depend on.
/// </summary>
/// <remarks>
/// The counters and DXGI cannot be called from a test — they need a WDDM driver and a real adapter — so the
/// service takes both behind internal seams and these tests feed it instance names copied verbatim from a real
/// machine's <c>typeperf</c> output (#2857).
/// </remarks>
public class GpuTelemetryServiceTests
{
    // Real LUIDs from the machine this was developed against: an NVIDIA RTX 4080 and an Intel UHD 770.
    private const string NvidiaLuid = "luid_0x00000000_0x0002B42E";
    private const string IntelLuid = "luid_0x00000000_0x0002EE83";
    private const string SoftwareLuid = "luid_0x00000000_0x0002EE36";

    private static GpuTelemetryService.GpuAdapter Nvidia(double vram = 15.7) =>
        new("NVIDIA GeForce RTX 4080", NvidiaLuid, vram, IsSoftware: false);

    private static GpuTelemetryService.GpuAdapter Intel(double vram = 0.1) =>
        new("Intel(R) UHD Graphics 770", IntelLuid, vram, IsSoftware: false);

    private static GpuTelemetryService.GpuAdapter Software() =>
        new("Microsoft Basic Render Driver", SoftwareLuid, 0, IsSoftware: true);

    /// <summary>A <c>\GPU Engine</c> instance name in the shape Windows actually publishes.</summary>
    private static string Engine(string luid, int pid, string engineType, int engine = 0) =>
        $"pid_{pid}_{luid}_phys_0_eng_{engine}_engtype_{engineType}";

    /// <summary>A <c>\GPU Adapter Memory</c> instance name, which carries no pid and no engine.</summary>
    private static string Memory(string luid) => $"{luid}_phys_0";

    // ---- instance-name parsing ----

    [Theory]
    [InlineData("pid_45000_luid_0x00000000_0x0002B42E_phys_0_eng_0_engtype_3D", "luid_0x00000000_0x0002B42E")]
    [InlineData("luid_0x00000000_0x0002B42E_phys_0", "luid_0x00000000_0x0002B42E")]
    [InlineData("pid_1_luid_0x00000001_0xFFFFFFFF_phys_1_eng_7_engtype_VideoEncode", "luid_0x00000001_0xFFFFFFFF")]
    public void LuidOf_ReadsTheAdapterOutOfEitherInstanceShape(string instance, string expected)
        => Assert.Equal(expected, GpuTelemetryService.LuidOf(instance));

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    // "luid_" with nothing usable after it: two underscore-separated words are the minimum.
    [InlineData("luid_0x00000000")]
    public void LuidOf_ReturnsNullWhenThereIsNoAdapterInTheName(string instance)
        => Assert.Null(GpuTelemetryService.LuidOf(instance));

    [Theory]
    [InlineData("pid_45000_luid_0x00000000_0x0002B42E_phys_0_eng_0_engtype_3D", "3D")]
    [InlineData("pid_1_luid_0x0_0x1_phys_0_eng_6_engtype_VideoEncode", "VideoEncode")]
    [InlineData("pid_1_luid_0x0_0x1_phys_0_eng_8_engtype_OFA_0", "OFA_0")]
    public void EngineTypeOf_ReadsTheEngineOutOfTheName(string instance, string expected)
        => Assert.Equal(expected, GpuTelemetryService.EngineTypeOf(instance));

    [Fact]
    public void EngineTypeOf_ReturnsNullForAnAdapterMemoryInstance()
        => Assert.Null(GpuTelemetryService.EngineTypeOf(Memory(NvidiaLuid)));

    // ---- aggregation ----

    /// <summary>
    /// Several processes on the SAME engine type add up: each holds its own counter instance for a share of
    /// one engine.
    /// </summary>
    [Fact]
    public void Aggregate_SumsTheInstancesSharingAnEngineType()
    {
        var engine = new[]
        {
            (Engine(NvidiaLuid, 1644, "3D"), 1.5),
            (Engine(NvidiaLuid, 2708, "3D"), 2.0),
            (Engine(NvidiaLuid, 29464, "3D"), 0.5),
        };

        var reading = Assert.Single(GpuTelemetryService.Aggregate(engine, [], [Nvidia()]));

        Assert.Equal(4.0, reading.UsagePercent, precision: 6);
    }

    /// <summary>
    /// Engine TYPES do not add up: the adapter's figure is the busiest one, which is what Task Manager shows.
    /// </summary>
    /// <remarks>
    /// 3D and video decode run in parallel on the same card, so summing them reports a GPU that is not
    /// saturated as over 100% busy. Here 60 + 50 + 10 would be 120%; the answer is 60.
    /// </remarks>
    [Fact]
    public void Aggregate_TakesTheBusiestEngineTypeRatherThanTheirSum()
    {
        var engine = new[]
        {
            (Engine(NvidiaLuid, 100, "3D"), 60.0),
            (Engine(NvidiaLuid, 100, "VideoDecode", engine: 2), 50.0),
            (Engine(NvidiaLuid, 100, "Copy", engine: 4), 10.0),
        };

        var reading = Assert.Single(GpuTelemetryService.Aggregate(engine, [], [Nvidia()]));

        Assert.Equal(60.0, reading.UsagePercent, precision: 6);
    }

    /// <summary>Each adapter gets only its OWN instances — the bug this service exists to fix, in reverse.</summary>
    [Fact]
    public void Aggregate_KeepsEachAdaptersWorkToItself()
    {
        var engine = new[]
        {
            (Engine(NvidiaLuid, 100, "3D"), 40.0),
            (Engine(IntelLuid, 200, "3D"), 7.0),
        };

        var readings = GpuTelemetryService.Aggregate(engine, [], [Nvidia(), Intel()]);

        Assert.Equal(2, readings.Count);
        Assert.Equal(40.0, readings.Single(r => r.Name.StartsWith("NVIDIA", StringComparison.Ordinal)).UsagePercent, precision: 6);
        Assert.Equal(7.0, readings.Single(r => r.Name.StartsWith("Intel", StringComparison.Ordinal)).UsagePercent, precision: 6);
    }

    /// <summary>
    /// An Intel or AMD adapter with real work on it reports that work — the whole point of the change.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails against the old NVIDIA-only reading: there, an Intel LUID produced no
    /// reading at all and the Dashboard showed a confident "GPU 0%" with an empty bar (#2857).
    /// </remarks>
    [Fact]
    public void Aggregate_ReportsANonNvidiaAdapterThatIsBusy()
    {
        var engine = new[] { (Engine(IntelLuid, 4242, "3D"), 73.5) };
        var memory = new[] { (Memory(IntelLuid), 512d * 1024 * 1024) };

        var reading = Assert.Single(GpuTelemetryService.Aggregate(engine, memory, [Intel()]));

        Assert.Equal("Intel(R) UHD Graphics 770", reading.Name);
        Assert.Equal(73.5, reading.UsagePercent, precision: 6);
        Assert.Equal(0.5, reading.MemoryUsedGB, precision: 6);
    }

    /// <summary>The busiest adapter comes first, so a caller showing one GPU shows the one doing the work.</summary>
    [Fact]
    public void Aggregate_OrdersTheBusiestAdapterFirst()
    {
        var engine = new[]
        {
            (Engine(NvidiaLuid, 100, "3D"), 5.0),
            (Engine(IntelLuid, 200, "3D"), 65.0),
        };

        var readings = GpuTelemetryService.Aggregate(engine, [], [Nvidia(), Intel()]);

        Assert.Equal("Intel(R) UHD Graphics 770", readings[0].Name);
    }

    /// <summary>A software adapter is not a GPU and is never listed.</summary>
    [Fact]
    public void Aggregate_LeavesOutTheSoftwareAdapter()
    {
        var engine = new[] { (Engine(SoftwareLuid, 100, "3D"), 90.0) };

        var readings = GpuTelemetryService.Aggregate(engine, [], [Software()]);

        Assert.Empty(readings);
    }

    /// <summary>Memory comes from its own counter, per adapter, and is reported in gigabytes.</summary>
    [Fact]
    public void Aggregate_ReportsDedicatedMemoryAgainstTheCardsTotal()
    {
        var memory = new[]
        {
            (Memory(NvidiaLuid), 2689044480d),   // 2.5 GiB, a real reading from the machine
            (Memory(IntelLuid), 0d),
        };

        var readings = GpuTelemetryService.Aggregate([], memory, [Nvidia(vram: 15.7), Intel(vram: 0.1)]);

        var nvidia = readings.Single(r => r.Name.StartsWith("NVIDIA", StringComparison.Ordinal));
        // 2689044480 / 1024³ — gibibytes, the same unit the RAM tile uses, not decimal GB.
        Assert.Equal(2.5043678283691406, nvidia.MemoryUsedGB, precision: 6);
        Assert.Equal(15.7, nvidia.MemoryTotalGB, precision: 6);
    }

    /// <summary>
    /// An adapter DXGI reports but the counters say nothing about is idle, not missing: it exists, so it is
    /// listed, at zero.
    /// </summary>
    [Fact]
    public void Aggregate_ReportsZeroForAnAdapterWithNoInstances()
    {
        var reading = Assert.Single(GpuTelemetryService.Aggregate([], [], [Intel()]));

        Assert.Equal(0.0, reading.UsagePercent, precision: 6);
        Assert.Equal(0.0, reading.MemoryUsedGB, precision: 6);
    }

    /// <summary>With no hardware adapter at all there is NO reading, rather than a zeroed one.</summary>
    /// <remarks>
    /// "Nothing can measure this" and "the GPU is idle" are different answers, and the Dashboard shows them
    /// differently — "N/A" against a real 0%. An empty list is what carries the first one.
    /// </remarks>
    [Fact]
    public void Aggregate_WithNoHardwareAdapter_ReportsNothingRatherThanZero()
    {
        var engine = new[] { (Engine(NvidiaLuid, 100, "3D"), 50.0) };

        Assert.Empty(GpuTelemetryService.Aggregate(engine, [], []));
        Assert.Empty(GpuTelemetryService.Aggregate(engine, [], [Software()]));
    }

    /// <summary>A malformed instance name is skipped without taking the rest of the reading with it.</summary>
    [Fact]
    public void Aggregate_SkipsAnInstanceItCannotParse()
    {
        var engine = new[]
        {
            ("something_unexpected", 99.0),
            (Engine(NvidiaLuid, 100, "3D"), 12.0),
        };

        var reading = Assert.Single(GpuTelemetryService.Aggregate(engine, [], [Nvidia()]));

        Assert.Equal(12.0, reading.UsagePercent, precision: 6);
    }

    /// <summary>A counter that reports past 100 is clamped: a percentage is a percentage.</summary>
    [Fact]
    public void Aggregate_ClampsToAPercentage()
    {
        var engine = new[]
        {
            (Engine(NvidiaLuid, 1, "3D"), 80.0),
            (Engine(NvidiaLuid, 2, "3D"), 80.0),
        };

        var reading = Assert.Single(GpuTelemetryService.Aggregate(engine, [], [Nvidia()]));

        Assert.Equal(100.0, reading.UsagePercent, precision: 6);
    }

    // ---- the service over its seams ----

    private sealed class StubCounters(
        IReadOnlyList<(string, double)> engine,
        IReadOnlyList<(string, double)> memory) : GpuTelemetryService.IGpuCounters
    {
        internal int Disposals { get; private set; }
        public IReadOnlyList<(string Instance, double Value)> EngineUtilization() => engine;
        public IReadOnlyList<(string Instance, double Bytes)> AdapterDedicatedUsage() => memory;
        public void Dispose() => Disposals++;
    }

    private sealed class StubAdapters(IReadOnlyList<GpuTelemetryService.GpuAdapter> adapters)
        : GpuTelemetryService.IGpuAdapters
    {
        public IReadOnlyList<GpuTelemetryService.GpuAdapter> Adapters() => adapters;
    }

    [Fact]
    public void ReadUsage_GoesThroughBothSeamsAndAggregatesThem()
    {
        var counters = new StubCounters(
            [(Engine(IntelLuid, 1, "3D"), 33.0)],
            [(Memory(IntelLuid), 1024d * 1024 * 1024)]);
        using var service = new GpuTelemetryService(counters, new StubAdapters([Intel()]));

        var reading = Assert.Single(service.ReadUsage());

        Assert.Equal("Intel(R) UHD Graphics 770", reading.Name);
        Assert.Equal(33.0, reading.UsagePercent, precision: 6);
        Assert.Equal(1.0, reading.MemoryUsedGB, precision: 6);
    }

    /// <summary>Disposing closes the counters, and a read afterwards answers nothing instead of throwing.</summary>
    /// <remarks>
    /// The service holds an open PDH query for the life of the app, and the Dashboard's poll can land after a
    /// shutdown has begun — a throw there would surface as a crash from a timer the user never invoked.
    /// </remarks>
    [Fact]
    public void Dispose_ClosesTheCountersOnceAndLaterReadsAnswerNothing()
    {
        var counters = new StubCounters([(Engine(IntelLuid, 1, "3D"), 33.0)], []);
        var service = new GpuTelemetryService(counters, new StubAdapters([Intel()]));

        Assert.Single(service.ReadUsage());
        service.Dispose();
        service.Dispose();

        Assert.Empty(service.ReadUsage());
        Assert.Equal(1, counters.Disposals);
    }
}
