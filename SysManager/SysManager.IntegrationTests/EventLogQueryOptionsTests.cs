using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class EventLogQueryOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var o = new EventLogQueryOptions();
        Assert.Equal("System", o.LogName);
        Assert.Null(o.Severities);
        Assert.Null(o.Since);
        Assert.Null(o.ProviderName);
        Assert.Null(o.EventId);
        Assert.True(o.MaxResults >= 100);
    }

    [Fact]
    public void SeveritiesCanBeSetAsList()
    {
        var o = new EventLogQueryOptions
        {
            Severities = new() { EventSeverity.Error, EventSeverity.Critical }
        };
        Assert.Equal(2, o.Severities!.Count);
    }

    [Fact]
    public void AllFieldsAreMutable()
    {
        var o = new EventLogQueryOptions
        {
            LogName = "Application",
            Since = DateTime.UtcNow.AddHours(-1),
            ProviderName = "MyProvider",
            EventId = 1000,
            MaxResults = 10_000
        };
        Assert.Equal("Application", o.LogName);
        Assert.Equal("MyProvider", o.ProviderName);
        Assert.Equal(1000, o.EventId);
        Assert.Equal(10_000, o.MaxResults);
    }
}
