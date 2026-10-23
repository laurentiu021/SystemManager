// SysManager · TimerResolutionServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for what <see cref="TimerResolutionService"/> asks Windows for, and in which order, over a stand-in for
/// its native calls.
/// </summary>
/// <remarks>
/// The real calls change this test process's timer and power throttling, so no test makes them. The stand-in
/// records each call instead, which is what makes the order provable: the request has to be kept while
/// SysManager is hidden before it is made, and given back to Windows once it is released (#2830).
/// </remarks>
public class TimerResolutionServiceTests
{
    private const uint Coarsest = 156250;
    private const uint Finest = 5000;

    private sealed class FakeTimer : TimerResolutionService.ITimerResolutionNative
    {
        public List<string> Calls { get; } = [];

        /// <summary>The NTSTATUS a request returns; a release always succeeds.</summary>
        public int RequestStatus { get; set; }

        /// <summary>The Win32 error the power-throttling call returns.</summary>
        public int ThrottlingError { get; set; }

        public (int Status, uint Coarsest, uint Finest, uint Current) Query() => (0, Coarsest, Finest, Coarsest);

        public int Set(uint desiredTime, bool setResolution)
        {
            Calls.Add(setResolution ? $"request {desiredTime}" : "release");
            return setResolution ? RequestStatus : 0;
        }

        public int KeepRequestWhileHidden(bool keep)
        {
            Calls.Add(keep ? "keep while hidden" : "leave to Windows");
            return ThrottlingError;
        }
    }

    /// <summary>
    /// Enable keeps the request while SysManager is hidden, then requests the finest resolution.
    /// </summary>
    /// <remarks>
    /// Windows 11 stops honoring a timer request from a process whose windows are hidden, minimized or covered, which
    /// is SysManager's state for as long as a full-screen game runs, and the service never opted out, so the request
    /// had the least effect exactly while the game it was made for ran.
    /// </remarks>
    [Fact]
    public void Enable_KeepsTheRequestWhileHidden_BeforeMakingIt()
    {
        var native = new FakeTimer();
        var service = new TimerResolutionService(native);

        var status = service.Enable();

        Assert.Equal(["keep while hidden", $"request {Finest}"], native.Calls);
        Assert.True(status.EnabledByApp);
    }

    /// <summary>
    /// Disable releases the request, then gives the choice back to Windows.
    /// </summary>
    [Fact]
    public void Disable_ReleasesTheRequest_ThenLeavesItToWindows()
    {
        var native = new FakeTimer();
        var service = new TimerResolutionService(native);
        service.Enable();
        native.Calls.Clear();

        var status = service.Disable();

        Assert.Equal(["release", "leave to Windows"], native.Calls);
        Assert.False(status.EnabledByApp);
    }

    /// <summary>
    /// A request Windows refuses leaves nothing kept, since nothing is requested.
    /// </summary>
    [Fact]
    public void Enable_RefusedRequest_LeavesItToWindowsAgain()
    {
        var native = new FakeTimer { RequestStatus = unchecked((int)0xC0000001) };
        var service = new TimerResolutionService(native);

        var status = service.Enable();

        Assert.Equal(["keep while hidden", $"request {Finest}", "leave to Windows"], native.Calls);
        Assert.False(status.EnabledByApp);
    }

    /// <summary>
    /// A second request that is refused does not give up the first one's keeping, which is still requested.
    /// </summary>
    [Fact]
    public void Enable_RefusedAfterAnEarlierRequest_KeepsTheEarlierOneKept()
    {
        var native = new FakeTimer();
        var service = new TimerResolutionService(native);
        service.Enable();
        native.Calls.Clear();
        native.RequestStatus = unchecked((int)0xC0000001);

        var status = service.Enable();

        Assert.Equal(["keep while hidden", $"request {Finest}"], native.Calls);
        Assert.True(status.EnabledByApp);
    }

    /// <summary>
    /// A Windows that refuses the keeping flag still gets the request: the flag is a help, not a condition.
    /// </summary>
    [Fact]
    public void Enable_KeepingRefused_StillMakesTheRequest()
    {
        var native = new FakeTimer { ThrottlingError = 87 };
        var service = new TimerResolutionService(native);

        var status = service.Enable();

        Assert.Contains($"request {Finest}", native.Calls);
        Assert.True(status.EnabledByApp);
    }
}
