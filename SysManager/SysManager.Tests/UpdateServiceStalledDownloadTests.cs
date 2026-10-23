// SysManager · UpdateServiceStalledDownloadTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests that a download whose connection goes quiet part way through ends, instead of waiting for as long as
/// SysManager stays open.
/// </summary>
/// <remarks>
/// <see cref="HttpClient.Timeout"/> ends once the headers arrive, because the download asks for the body with
/// <c>ResponseHeadersRead</c>, so nothing bounded the body reads after it: the bar and the megabyte count froze,
/// the Download button stayed hidden and no failure row appeared (#2831).
/// <para>Nothing here sleeps. The no-data clock runs on an injected <see cref="TimeProvider"/> whose timers fire
/// only when a test says so, and the body hands over each chunk when a test releases it, so every test drives the
/// exact order it is about. Each one also works in its own temp directory, through the download-directory seam, so
/// the developer's real update cache is never touched.</para>
/// </remarks>
public class UpdateServiceStalledDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sysmanager-stalled-download-" + Guid.NewGuid().ToString("N"));

    public UpdateServiceStalledDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static UpdateService.ReleaseInfo Release() => new(
        new Version(99, 99, 99), "v99.99.99", "SysManager 99.99.99", "notes", DateTimeOffset.UnixEpoch,
        "https://example.invalid/release", "https://example.invalid/SysManager-v99.99.99.exe", 85_000_000);

    private UpdateService Service(Stream body, TimeProvider clock) =>
        new(new BodyFrom(body), _dir, clock);

    /// <summary>How long the real service waits with nothing arriving, which these tests fire by hand.</summary>
    private static TimeSpan StallAfter => UpdateService.DefaultStallAfter;

    /// <summary>
    /// The download's result, or a failed assertion if it never finishes.
    /// </summary>
    /// <remarks>
    /// Bounded at the one place that can hang: a download whose reads are not bounded never returns, so without
    /// this the test that proves they are would hang the whole run instead of failing it.
    /// </remarks>
    private static async Task<string?> FinishedAsync(Task<string?> download)
    {
        var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        Assert.Same(download, finished);
        return await download.ConfigureAwait(false);
    }

    [Fact]
    public async Task DownloadAsync_WhenTheBodyStopsArriving_GivesUpAndLeavesNoPartialFile()
    {
        // A body that hands over one chunk and then never completes its next read — a connection that went quiet,
        // not one that was closed, which is why no exception ever arrived on its own.
        var clock = new ManualClock();
        using var body = new ChunksOnDemand(chunks: 1);
        var download = Service(body, clock).DownloadAsync(Release());

        await body.NextChunkAsync();                 // the first chunk arrives
        await body.WaitForTheReadAfterItAsync();     // and the read after it is now pending, with nothing coming
        clock.Advance(StallAfter);                   // so the no-data clock runs out

        // Reported as a failure, which is what makes About show its failure row and offer the manual download.
        Assert.Null(await FinishedAsync(download));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// A body that never sends anything at all is given up on too.
    /// </summary>
    /// <remarks>
    /// The headers arrived, so the client's own timeout is already over; a server that accepts the request and
    /// then says nothing would otherwise be waited on for ever. This is also what proves the clock is armed
    /// BEFORE the first read and not only after a chunk: armed only after one, this download would wait.
    /// </remarks>
    [Fact]
    public async Task DownloadAsync_WhenTheBodyNeverStarts_GivesUpToo()
    {
        var clock = new ManualClock();
        using var body = new ChunksOnDemand(chunks: 1);
        var download = Service(body, clock).DownloadAsync(Release());

        await body.WaitForTheReadAfterItAsync();     // the first read is pending and nothing has arrived
        clock.Advance(StallAfter);

        Assert.Null(await FinishedAsync(download));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task DownloadAsync_WhileTheBodyKeepsArriving_IsNotGivenUpOn()
    {
        // The negative control, and the reason the clock is re-armed after every chunk rather than started once:
        // this download takes four times the no-data timeout in total, in gaps that are each just inside it, and
        // must still finish. Armed once, it would have been given up on during the second chunk.
        var clock = new ManualClock();
        using var body = new ChunksOnDemand(chunks: 4);
        var download = Service(body, clock).DownloadAsync(Release());

        for (var chunk = 0; chunk < 4; chunk++)
        {
            await body.WaitForTheReadAfterItAsync();
            clock.Advance(StallAfter - TimeSpan.FromSeconds(1));   // a gap, but not a stall

            // Checked here, at the moment the gap ends, rather than only at the end: a download given one timeout
            // for the whole of it is already over by the second gap, and asserting that here fails the test on the
            // spot instead of leaving it waiting for a chunk nobody will read.
            Assert.False(download.IsCompleted,
                $"the download was given up on during gap {chunk + 1}, which is inside the no-data timeout");

            await body.NextChunkAsync();
        }

        // Four gaps of 29 s is 116 s on the clock, nearly four times the timeout: a download given one timeout
        // for the whole of it, rather than one per chunk, is dead by now.
        Assert.True(clock.Now > 3 * StallAfter);

        await body.WaitForTheReadAfterItAsync();
        await body.EndTheBodyAsync();

        var path = await FinishedAsync(download);
        Assert.NotNull(path);
        Assert.Equal(4 * ChunksOnDemand.ChunkBytes, new FileInfo(path).Length);
    }

    [Fact]
    public async Task DownloadAsync_WhenTheCallerCancels_IsNotReportedAsAStall()
    {
        // The two look the same at the read: both end it with an OperationCanceledException. The caller's own
        // cancellation must stay a cancellation — About says "Download cancelled" for it and keeps the Download
        // button, where a stall gets the failure row. The clock is never fired here, so only the Cancel can end it.
        var clock = new ManualClock();
        using var cts = new CancellationTokenSource();
        using var body = new ChunksOnDemand(chunks: 1);
        var download = Service(body, clock).DownloadAsync(Release(), progress: null, cts.Token);

        await body.NextChunkAsync();
        await body.WaitForTheReadAfterItAsync();
        await cts.CancelAsync();

        Assert.Null(await FinishedAsync(download));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock moves only when a test says so, and whose timers fire when it
    /// passes their due moment.
    /// </summary>
    /// <remarks>
    /// <c>CancellationTokenSource(TimeSpan, TimeProvider)</c> and its <c>CancelAfter</c> both ask the provider for
    /// a timer, so overriding <see cref="CreateTimer"/> is the whole seam — the shape
    /// <c>PingMonitorServiceLifecycleTests</c> uses. Hand-written rather than taking a dependency on
    /// Microsoft.Extensions.TimeProvider.Testing.
    /// <para>The time ACCUMULATES, and that is the point: a clock that only compared each advance against a
    /// timer's delay could never add several gaps together, so the download that takes four short gaps would look
    /// identical whether the timeout was re-armed after every chunk or started once for the whole download — and
    /// the mutation that starts it once went unnoticed. Here <see cref="Advance"/> moves a running total, a timer
    /// records the moment it is due at, and re-arming it through <c>Change</c> moves that moment forward.</para>
    /// </remarks>
    private sealed class ManualClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _now;

        /// <summary>How long this clock says has passed.</summary>
        public TimeSpan Now
        {
            get { lock (_gate) return _now; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, this);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        /// <summary>
        /// Moves the clock on by <paramref name="by"/> and fires every timer that moment has reached.
        /// </summary>
        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToArray();
            }

            // Fired outside the lock: a callback cancels a token source, whose continuations can run inline and
            // reach back into this clock to drop their own timer.
            foreach (var timer in due) timer.Fire();
        }

        internal void Forget(ManualTimer timer)
        {
            lock (_gate) _timers.Remove(timer);
        }

        internal sealed class ManualTimer(TimerCallback callback, object? state, ManualClock clock) : ITimer
        {
            /// <summary>The moment on the clock this fires at, or null while it is not armed.</summary>
            public TimeSpan? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // Timeout.InfiniteTimeSpan disarms it, which is how the download stops the clock once the body is
                // read: nothing after that may be cancelled by it.
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => clock.Forget(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Answers the asset request with a 200 whose body is the given stream.</summary>
    private sealed class BodyFrom(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>
    /// A body that hands over one chunk each time a test asks, and otherwise leaves the read pending.
    /// </summary>
    /// <remarks>
    /// So a test can say exactly when a chunk arrives and when the download is waiting for the next one, with no
    /// sleeping and nothing depending on how fast the machine is.
    /// </remarks>
    private sealed class ChunksOnDemand(int chunks) : Stream, IDisposable
    {
        public const int ChunkBytes = 16;

        private readonly SemaphoreSlim _reading = new(0);
        private TaskCompletionSource<int> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Waits until the download is parked on a read, so a test knows nothing is in flight.</summary>
        public async Task WaitForTheReadAfterItAsync()
        {
            var arrived = await _reading.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.True(arrived, "the download never asked for the next chunk — it is not reading the body.");
        }

        /// <summary>Hands the pending read its chunk.</summary>
        public Task NextChunkAsync()
        {
            _next.TrySetResult(ChunkBytes);
            return Task.CompletedTask;
        }

        /// <summary>Ends the body, which is a read of zero.</summary>
        public Task EndTheBodyAsync()
        {
            _next.TrySetResult(0);
            return Task.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= chunks && _next.Task.IsCompleted && _next.Task.Result == 0) return 0;

            var pending = _next;
            _reading.Release();
            var size = await pending.Task.WaitAsync(ct).ConfigureAwait(false);
            _next = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (size == 0) return 0;

            _sent++;
            buffer.Span[..size].Clear();
            return size;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _reading.Dispose();
            base.Dispose(disposing);
        }
    }
}
