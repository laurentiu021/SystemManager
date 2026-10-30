// SysManager · EtwBandwidthSourceKernelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// Precise mode's kernel trace, with a real kernel session (#2707).
/// </summary>
/// <remarks>
/// Here rather than in the unit suite because the session is the dependency: the trace ending, its buffer and a
/// session a killed run left behind are all Windows' state, and nothing in the unit suite can open one. A kernel
/// session needs administrator rights, which the CI runner has; without them each test says so and skips. They use
/// the app's own session name, which is what they are about, so on a PC where SysManager runs precise mode at the
/// same time, that trace is stopped and the tab falls back, as it would for any other tool.
/// </remarks>
[Collection("Sequential")]
public class EtwBandwidthSourceKernelTests
{
    private static void NeedsAdministrator()
    {
        if (!AdminHelper.IsElevated()) Assert.Skip("A kernel trace session needs administrator rights.");
    }

    /// <summary>
    /// A trace stopped from outside, by its name, is no longer available.
    /// </summary>
    /// <remarks>
    /// <c>Process()</c> returns normally then, and <c>IsAvailable</c> stayed true, so the tab showed zero rates under
    /// "live kernel trace" for as long as it was open. Waited for at the point the processing ends, bounded.
    /// </remarks>
    [Fact]
    public void ATraceStoppedFromOutside_IsNoLongerAvailable()
    {
        NeedsAdministrator();
        using var source = new EtwBandwidthSource();
        Assert.True(source.Start(), "precondition: the kernel session starts");

        EtwBandwidthSource.StopLeftoverSession();

        Assert.True(SpinWait.SpinUntil(() => !source.IsAvailable, TimeSpan.FromSeconds(10)),
            "the source still says it is available ten seconds after its session was stopped");
    }

    /// <summary>
    /// The session asks Windows for a few MB of buffers rather than TraceEvent's 64.
    /// </summary>
    [Fact]
    public void TheTrace_UsesASmallBuffer()
    {
        NeedsAdministrator();
        using var source = new EtwBandwidthSource();
        Assert.True(source.Start(), "precondition: the kernel session starts");

        using var live = TraceEventSession.GetActiveSession(EtwBandwidthSource.SessionName);

        Assert.NotNull(live);
        Assert.Equal(EtwBandwidthSource.BufferSizeMB, live.BufferSizeMB);
    }

    /// <summary>
    /// A session a killed run left running is stopped by the sweep SysManager runs as it starts as administrator.
    /// </summary>
    [Fact]
    public void TheStartupSweep_StopsASessionAKilledRunLeft()
    {
        NeedsAdministrator();
        // What a killed run leaves: a session with the app's name that nothing will stop on its way out.
        var leftover = new TraceEventSession(EtwBandwidthSource.SessionName) { StopOnDispose = false };
        try
        {
            leftover.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            using (var before = TraceEventSession.GetActiveSession(EtwBandwidthSource.SessionName))
                Assert.NotNull(before);

            EtwBandwidthSource.StopLeftoverSession();

            using var after = TraceEventSession.GetActiveSession(EtwBandwidthSource.SessionName);
            Assert.Null(after);
        }
        finally
        {
            leftover.Stop(noThrow: true);
            leftover.Dispose();
        }
    }
}
