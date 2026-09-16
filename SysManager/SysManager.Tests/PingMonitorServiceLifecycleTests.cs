// SysManager · PingMonitorServiceLifecycleTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Lifecycle/regression tests for <see cref="PingMonitorService"/>. The Stop()
/// path was rewritten so the CancellationTokenSource is always disposed (even
/// when the loop is still winding down) instead of being dropped and leaked.
/// These verify the start/stop/dispose cycle is safe and idempotent.
/// </summary>
public class PingMonitorServiceLifecycleTests
{
    [Fact]
    public void StartStop_DoesNotThrow()
    {
        using var svc = new PingMonitorService();
        svc.Start();
        svc.Stop();
    }

    [Fact]
    public void Stop_WithoutStart_IsNoOp()
    {
        using var svc = new PingMonitorService();
        svc.Stop(); // never started — must not throw
    }

    [Fact]
    public void RepeatedStartStop_DoesNotThrow()
    {
        using var svc = new PingMonitorService();
        for (var i = 0; i < 5; i++)
        {
            svc.Start();
            svc.Stop();
        }
    }

    [Fact]
    public void Dispose_AfterStart_DoesNotThrow()
    {
        var svc = new PingMonitorService();
        svc.Start();
        svc.Dispose(); // Dispose() routes through Stop()
    }

    [Fact]
    public void DoubleStart_IsIdempotent()
    {
        using var svc = new PingMonitorService();
        svc.Start();
        svc.Start(); // second Start() must be ignored while running, not leak a CTS
        svc.Stop();
    }

    // ---------------------------------------------------------------------------------------------
    // Cadence. These replace an integration test that started the real pump, slept 1.2 real seconds
    // and asserted a tick COUNT — which on a loaded machine measured how much CPU the host had spare
    // rather than anything about this class, and went red once during a full local run (2 samples
    // against a floor of 4). The pump's delay now comes from an injected TimeProvider, so the delay
    // it ASKS for is observable directly and the assertion is the one the test is named for.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NextDelay_ReadsIntervalEveryTime_SoAChangeAppliesToTheFollowingTick()
    {
        using var svc = new PingMonitorService { Interval = TimeSpan.FromMilliseconds(100) };
        Assert.Equal(TimeSpan.FromMilliseconds(100), svc.NextDelay);

        svc.Interval = TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), svc.NextDelay);
    }

    [Theory]
    [InlineData(0)]              // a caller-supplied zero would be a busy loop
    [InlineData(-5000)]          // so would a negative
    [InlineData(1)]              // and so would anything under the floor
    [InlineData(49)]
    public void NextDelay_FloorsAnythingBelowFiftyMilliseconds(int requestedMs)
    {
        using var svc = new PingMonitorService { Interval = TimeSpan.FromMilliseconds(requestedMs) };
        Assert.Equal(PingMonitorService.MinimumInterval, svc.NextDelay);
    }

    [Theory]
    [InlineData(50)]             // exactly the floor is already legal — it must not be rounded up
    [InlineData(100)]
    [InlineData(1000)]
    public void NextDelay_PassesThroughAnythingAtOrAboveTheFloor(int requestedMs)
    {
        using var svc = new PingMonitorService { Interval = TimeSpan.FromMilliseconds(requestedMs) };
        Assert.Equal(TimeSpan.FromMilliseconds(requestedMs), svc.NextDelay);
    }

    [Fact]
    public async Task RunningPump_AsksForTheNewInterval_OnTheTickAfterItChanges()
    {
        var clock = new ManualDelayProvider();
        using var svc = new PingMonitorService(clock) { Interval = TimeSpan.FromMilliseconds(100) };

        // No targets, so a tick does no network work at all: the pump reduces to "decide a delay,
        // wait for it, decide again", which is exactly the behaviour under test.
        svc.Start();

        Assert.Equal(TimeSpan.FromMilliseconds(100), await clock.NextRequestedDelayAsync());

        // Change the interval while the pump is parked on that delay, then let the delay complete.
        svc.Interval = TimeSpan.FromSeconds(1);
        clock.CompleteOldestDelay();

        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextRequestedDelayAsync());

        // And it keeps re-reading rather than latching the second value either.
        svc.Interval = TimeSpan.FromMilliseconds(250);
        clock.CompleteOldestDelay();

        Assert.Equal(TimeSpan.FromMilliseconds(250), await clock.NextRequestedDelayAsync());

        svc.Stop();
    }

    [Fact]
    public async Task RunningPump_FloorsAnIntervalItIsGivenMidRun()
    {
        var clock = new ManualDelayProvider();
        using var svc = new PingMonitorService(clock) { Interval = TimeSpan.FromMilliseconds(100) };

        svc.Start();
        Assert.Equal(TimeSpan.FromMilliseconds(100), await clock.NextRequestedDelayAsync());

        // 1ms rather than TimeSpan.Zero, and the difference is not cosmetic. Task.Delay short-circuits a
        // zero delay to an already-completed task WITHOUT asking the provider for a timer, so if the floor
        // ever regressed, the pump would busy-loop creating no timers at all: this fake would see nothing,
        // NextRequestedDelayAsync's bounded wait could not be scheduled on a starved thread pool, and the
        // test would hang instead of failing. Measured: 900s and still going. 1ms is under the floor but
        // still positive, so an unfloored pump parks on a real 1ms timer that this fake records and never
        // fires — the assertion then fails in milliseconds with the wrong number in the message.
        svc.Interval = TimeSpan.FromMilliseconds(1);
        clock.CompleteOldestDelay();

        Assert.Equal(PingMonitorService.MinimumInterval, await clock.NextRequestedDelayAsync());
        svc.Stop();
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose delays complete only when the test says so.
    /// <para><c>Task.Delay(delay, provider, ct)</c> asks the provider for a one-shot timer, so
    /// overriding <see cref="CreateTimer"/> is the whole seam: every delay the pump requests lands
    /// in <see cref="_requested"/> and stays pending until <see cref="CompleteOldestDelay"/> fires
    /// its callback. Hand-written rather than taking a dependency on
    /// Microsoft.Extensions.TimeProvider.Testing, matching the fakes in
    /// <c>EtaCalculatorTests</c> and <c>EtwBandwidthSourceTests</c>.</para>
    /// </summary>
    private sealed class ManualDelayProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly Queue<ManualTimer> _pending = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, this);
            lock (_gate) _pending.Enqueue(timer);
            _arrived.Release();
            return timer;
        }

        /// <summary>
        /// Waits for the pump to ask for its next delay and returns the duration it asked for, leaving
        /// the delay pending. Bounded, so a pump that stops asking fails the test in seconds instead of
        /// hanging the whole run.
        /// </summary>
        public async Task<TimeSpan> NextRequestedDelayAsync()
        {
            var arrived = await _arrived.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(arrived, "The pump did not request a delay within 10s — it is not looping.");
            lock (_gate) return _pending.Peek().Due;
        }

        /// <summary>Completes the delay the pump is parked on, releasing it into its next tick.</summary>
        public void CompleteOldestDelay()
        {
            ManualTimer timer;
            lock (_gate)
            {
                Assert.True(_pending.Count > 0, "No delay is pending — nothing to complete.");
                timer = _pending.Dequeue();
            }
            timer.Fire();
        }

        /// <summary>
        /// Drops a timer without firing it. <c>Task.Delay</c> disposes its timer both on completion
        /// and on cancellation, and a cancelled delay must not stay queued or a later
        /// <see cref="CompleteOldestDelay"/> would fire a dead one.
        /// </summary>
        private void Forget(ManualTimer timer)
        {
            lock (_gate)
            {
                if (!_pending.Contains(timer)) return;
                var keep = _pending.Where(t => !ReferenceEquals(t, timer)).ToArray();
                _pending.Clear();
                foreach (var k in keep) _pending.Enqueue(k);
            }
        }

        private sealed class ManualTimer(
            TimerCallback callback, object? state, TimeSpan due, ManualDelayProvider owner) : ITimer
        {
            private int _fired;

            public TimeSpan Due => due;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0)
                    callback(state);
            }

            // The pump never reschedules: Task.Delay arms the timer once, through CreateTimer's dueTime.
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner.Forget(this);

            public ValueTask DisposeAsync()
            {
                owner.Forget(this);
                return ValueTask.CompletedTask;
            }
        }
    }
}
