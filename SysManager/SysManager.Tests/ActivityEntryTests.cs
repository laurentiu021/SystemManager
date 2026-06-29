// SysManager · ActivityEntryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

public class ActivityEntryTests
{
    // TimeAgo is relative to DateTime.Now; build timestamps as offsets from now so the
    // assertions are deterministic regardless of when the test runs.
    private static ActivityEntry Aged(TimeSpan ago) => new("Opened", "Cleanup", DateTime.Now - ago);

    [Fact]
    public void TimeAgo_JustNow_UnderAMinute()
        => Assert.Equal("Just now", Aged(TimeSpan.FromSeconds(20)).TimeAgo);

    [Fact]
    public void TimeAgo_Minutes()
        => Assert.Equal("5m ago", Aged(TimeSpan.FromMinutes(5)).TimeAgo);

    [Fact]
    public void TimeAgo_Hours()
        => Assert.Equal("3h ago", Aged(TimeSpan.FromHours(3)).TimeAgo);

    [Fact]
    public void TimeAgo_Yesterday()
        => Assert.Equal("Yesterday", Aged(TimeSpan.FromHours(30)).TimeAgo);

    [Fact]
    public void TimeAgo_Days()
        => Assert.Equal("4 days ago", Aged(TimeSpan.FromDays(4)).TimeAgo);

    [Fact]
    public void Record_PreservesActionAndDetail()
    {
        var e = new ActivityEntry("DNS & Hosts", "Set DNS to Cloudflare", DateTime.Now);
        Assert.Equal("DNS & Hosts", e.Action);
        Assert.Equal("Set DNS to Cloudflare", e.Detail);
    }
}
