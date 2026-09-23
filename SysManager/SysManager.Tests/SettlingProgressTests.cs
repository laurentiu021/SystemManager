// SysManager · SettlingProgressTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SettlingProgress{T}"/> — that a report raised while an operation runs reaches the
/// handler, and that one raised after it has ended does not, whichever way it ended.
/// </summary>
/// <remarks>
/// <para>Every other test in this project uses <see cref="SyncProgress{T}"/> and constructs no
/// <see cref="Progress{T}"/> at all, because asynchronous delivery is never the thing being measured there —
/// it only makes the assertion race the report. Here it IS the thing being measured, so delivery is made
/// synchronous through a <see cref="SynchronizationContext"/> that runs a post inline: the report has
/// therefore either arrived or been dropped by the time <c>Report</c> returns, and nothing waits on a clock.
/// <c>SettlingProgress</c> captures that context in its own constructor, so every reporter here is built
/// inside <see cref="WithInlineDelivery"/> rather than as a field.</para>
/// <para>What these cannot reach is the lock: proving that a report already inside the handler completes
/// before the settle takes effect needs the two to overlap, and every deterministic way to arrange that
/// reduces to asserting that something did not happen within some number of milliseconds. That is the timing
/// dependence the testing rules exist to keep out, so the reason the lock spans the handler is recorded in
/// the primitive instead of pinned here.</para>
/// </remarks>
public class SettlingProgressTests
{
    [Fact]
    public async Task Report_RaisedBeforeTheOperationStarts_ReachesTheHandler()
    {
        await WithInlineDelivery(() =>
        {
            List<int> seen = [];
            var progress = new SettlingProgress<int>(seen.Add);

            // Construction and the operation are separate calls at every site — SpeedTestViewModel builds
            // the reporter, then starts the engine — so the gap between them must still report.
            progress.Report(7);

            Assert.Equal([7], seen);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Report_RaisedWhileTheOperationRuns_ReachesTheHandler()
    {
        await WithInlineDelivery(async () =>
        {
            List<int> seen = [];
            var progress = new SettlingProgress<int>(seen.Add);

            var passes = await progress.SettleAfterAsync(reporter =>
            {
                reporter.Report(33);
                reporter.Report(66);
                return Task.FromResult(3);
            });

            Assert.Equal(3, passes);
            Assert.Equal([33, 66], seen);
        });
    }

    [Fact]
    public async Task Report_RaisedAfterTheOperationEnded_IsDropped()
    {
        await WithInlineDelivery(async () =>
        {
            List<int> seen = [];
            var progress = new SettlingProgress<int>(seen.Add);
            IProgress<int>? kept = null;

            await progress.SettleAfterAsync(reporter =>
            {
                // A service that holds on to the reporter and raises a last report from a continuation
                // that drains after it returned — the shape that left an erased file labelled
                // "Shredding pass 2/3..." forever and made a required check flaky on CI (#2391).
                kept = reporter;
                reporter.Report(50);
                return Task.FromResult(0);
            });

            kept!.Report(100);

            Assert.Equal([50], seen);
        });
    }

    [Fact]
    public async Task TheCallersTerminalWrite_SurvivesAReportThatDrainsAfterTheAwait()
    {
        await WithInlineDelivery(async () =>
        {
            // Stands in for the bound property at all nine sites: the callback writes it while the
            // operation runs, and the caller writes the outcome to the same property afterwards.
            var status = string.Empty;
            var progress = new SettlingProgress<int>(p => status = $"Shredding pass {p}/3...");
            IProgress<int>? kept = null;

            await progress.SettleAfterAsync(reporter =>
            {
                kept = reporter;
                reporter.Report(2);
                return Task.FromResult(0);
            });

            status = "Done";
            kept!.Report(3);

            Assert.Equal("Done", status);
        });
    }

    [Fact]
    public Task Report_RaisedAfterTheOperationWasCancelled_IsDropped() =>
        // The arm that actually happens in the field: every migrated caller writes its terminal value
        // from a catch, so the settle has to have happened before the catch body runs.
        AssertTheReporterSettlesWhenTheOperationThrows(new OperationCanceledException());

    [Fact]
    public Task Report_RaisedAfterTheOperationFailed_IsDropped() =>
        AssertTheReporterSettlesWhenTheOperationThrows(new IOException("the file is in use"));

    [Fact]
    public void Constructor_RejectsAHandlerThatIsNull() =>
        Assert.Throws<ArgumentNullException>(() => new SettlingProgress<int>(null!));

    [Fact]
    public async Task SettleAfterAsync_RejectsAnOperationThatIsNull()
    {
        var progress = new SettlingProgress<int>(_ => { });

        await Assert.ThrowsAsync<ArgumentNullException>(() => progress.SettleAfterAsync<int>(null!));
    }

    /// <summary>
    /// Asserts that an operation ending in <paramref name="failure"/> still settles the reporter, and that
    /// the exception reaches the caller as itself rather than wrapped.
    /// </summary>
    private static async Task AssertTheReporterSettlesWhenTheOperationThrows<TException>(TException failure)
        where TException : Exception
    {
        await WithInlineDelivery(async () =>
        {
            List<int> seen = [];
            var progress = new SettlingProgress<int>(seen.Add);
            IProgress<int>? kept = null;

            var thrown = await Assert.ThrowsAsync<TException>(() => progress.SettleAfterAsync<int>(reporter =>
            {
                kept = reporter;
                reporter.Report(1);
                throw failure;
            }));

            kept!.Report(2);

            Assert.Same(failure, thrown);
            Assert.Equal([1], seen);
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> with progress delivery made synchronous, and puts the previous context
    /// back afterwards.
    /// </summary>
    private static async Task WithInlineDelivery(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var inline = new InlineContext();
        SynchronizationContext.SetSynchronizationContext(inline);

        try
        {
            await body();

            // Every operation in this file completes synchronously, so no continuation hops threads and the
            // restore below lands on the thread that installed the context. Asserted rather than assumed: a
            // future test that awaits something real would otherwise leave this context installed on a
            // pooled thread, silently making progress delivery synchronous in whatever test runs next.
            Assert.Same(inline, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Delivers a post on the thread that raised it, so a report has arrived — or been dropped — by the time
    /// <see cref="IProgress{T}.Report"/> returns.
    /// </summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
