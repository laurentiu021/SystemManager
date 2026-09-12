// SysManager · BrowserCleanerCancellationTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Behavioural tests for the two directions of <see cref="BrowserCleanerService"/>'s cancellation
/// contract. That a cancelled scan THROWS is pinned in the unit project instead — see
/// <c>ArchitectureTests.EveryScanThatBreaksOnCancellation_ThrowsBeforeReturning</c> — because this scan
/// takes no progress callback, so there is no deterministic point at which a test can cancel it: on a
/// machine with no browser profiles it finishes in microseconds and would legitimately beat the cancel.
/// A test that accepted "either outcome" would pass against the unfixed code, which is what it must not do.
/// </summary>
public class BrowserCleanerCancellationTests
{
    /// <summary>
    /// A scan that is NOT cancelled returns normally.
    /// </summary>
    /// <remarks>
    /// The other direction of the new throw: placed where the token is not actually cancelled, it would
    /// break every ordinary scan — and no source-shape guard can see that.
    /// </remarks>
    [Fact]
    public async Task ScanAsync_NotCancelled_CompletesNormally()
    {
        var items = await new BrowserCleanerService().ScanAsync();

        Assert.NotNull(items);   // a machine may legitimately have no browser data to clean
    }

    /// <summary>
    /// Cleaning an empty selection deletes nothing and returns zero.
    /// </summary>
    /// <remarks>
    /// The clean path deliberately has NO cancellation throw, unlike the scan: its count is true either way
    /// — those files really were deleted — and an exception would discard it, trading a missing word for
    /// missing information (#2278). That asymmetry is not expressible as a behavioural test, because
    /// <c>Task.Run(…, ct)</c> refuses to invoke the delegate for an already-cancelled token and the await
    /// throws before the body is reached, whatever the body does. So the guard in the unit project checks
    /// the clean path's SHAPE, and this covers the ordinary path it must not break.
    /// </remarks>
    [Fact]
    public async Task CleanAsync_WithNothingSelected_ReturnsZero()
    {
        var deleted = await new BrowserCleanerService()
            .CleanAsync(Array.Empty<BrowserCleanupItem>());

        Assert.Equal(0, deleted);
    }
}
