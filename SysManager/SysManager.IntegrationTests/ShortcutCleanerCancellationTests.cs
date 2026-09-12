// SysManager · ShortcutCleanerCancellationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests that a cancelled shortcut scan reports itself as cancelled rather than as finished.
/// Here rather than in the unit project because the scan walks the real Start Menu and desktop
/// locations; there is no seam to substitute.
/// </summary>
public class ShortcutCleanerCancellationTests
{
    /// <summary>Cancels the scan the first time it reports a location, and records that it did.</summary>
    private sealed class CancelOnFirstReport(CancellationTokenSource cts) : IProgress<string>
    {
        public int Reports { get; private set; }

        public void Report(string value)
        {
            Reports++;
            cts.Cancel();
        }
    }

    /// <summary>
    /// A scan cancelled while it is running surfaces <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// The scan breaks out of its loops on cancellation and used to return the partial list normally, so
    /// the view model took its SUCCESS path: it announced a finished scan, logged "Shortcut scan
    /// completed", raised a "Shortcut scan complete" toast, and — when nothing had been found yet — said
    /// "No broken shortcuts found — your system is clean." Its <c>catch (OperationCanceledException)</c>
    /// branch already existed and was dead code (#2275).
    /// <para><b>Cancellation is triggered from the progress callback, not from a timer or a pre-cancelled
    /// token, and both alternatives would have made this test worthless.</b> A pre-cancelled token never
    /// reaches the method at all — <c>Task.Run(…, ct)</c> refuses to invoke the delegate and the await
    /// throws <c>TaskCanceledException</c> on its own, so the test would pass against the unfixed code. A
    /// timer would be a race. The callback fires once per location, before that location is walked, so
    /// cancelling inside it means the very next loop check breaks and the throw under test is the only
    /// thing that can raise.</para>
    /// </remarks>
    [Fact]
    public async Task ScanAsync_CancelledWhileRunning_ReportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cts);
        var service = new ShortcutCleanerService();

        var ex = await Record.ExceptionAsync(() => service.ScanAsync(progress, cts.Token));

        Assert.True(progress.Reports > 0,
            "the scan never reported a location, so cancellation never fired and this test proves nothing "
            + "about a cancelled scan. It needs at least one of the standard shortcut locations to exist.");

        Assert.True(ex is OperationCanceledException,
            $"a cancelled scan surfaced {ex?.GetType().Name ?? "NO EXCEPTION"}. With no exception the view "
            + "model takes its success path and announces a finished scan — and if nothing had been found "
            + "yet, that the PC is clean.");
    }

    /// <summary>
    /// A scan that is NOT cancelled still returns normally.
    /// </summary>
    /// <remarks>
    /// The other direction of the same line: a <c>ThrowIfCancellationRequested</c> placed where the token
    /// is not actually cancelled would break every ordinary scan, and the test above cannot see that.
    /// </remarks>
    [Fact]
    public async Task ScanAsync_NotCancelled_CompletesNormally()
    {
        var results = await new ShortcutCleanerService().ScanAsync();

        Assert.NotNull(results);   // a broken shortcut may legitimately not exist on this machine
    }
}
