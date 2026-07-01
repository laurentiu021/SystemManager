// SysManager · EventExplainerEdgeCaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class EventExplainerEdgeCaseTests
{
    [Fact]
    public void Enrich_KnownEvent_KernelPower41_SetsExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "Microsoft-Windows-Kernel-Power",
            EventId = 41,
            Severity = EventSeverity.Critical
        };

        EventExplainer.Enrich(entry);

        Assert.NotEmpty(entry.Explanation);
        Assert.Contains("rebooted", entry.Explanation);
        Assert.NotEmpty(entry.Recommendation);
    }

    [Fact]
    public void Enrich_KnownEvent_Disk7_SetsExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "disk",
            EventId = 7,
            Severity = EventSeverity.Error
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("bad block", entry.Explanation);
    }

    [Fact]
    public void Enrich_KnownById_6008_SetsExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "UnknownProvider",
            EventId = 6008,
            Severity = EventSeverity.Error
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("unexpected", entry.Explanation);
    }

    [Fact]
    public void Enrich_UnknownEvent_CriticalSeverity_GenericExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "SomeCustomProvider",
            EventId = 99999,
            Severity = EventSeverity.Critical
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("Critical", entry.Explanation);
        Assert.Contains("SomeCustomProvider", entry.Explanation);
        Assert.Contains("Event ID", entry.Recommendation);
    }

    [Fact]
    public void Enrich_UnknownEvent_ErrorSeverity_GenericExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "MyApp",
            EventId = 12345,
            Severity = EventSeverity.Error
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("error", entry.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MyApp", entry.Explanation);
    }

    [Fact]
    public void Enrich_UnknownEvent_WarningSeverity_GenericExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "TestProvider",
            EventId = 555,
            Severity = EventSeverity.Warning
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("warning", entry.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe to ignore", entry.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enrich_UnknownEvent_InfoSeverity_GenericExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "TestProvider",
            EventId = 100,
            Severity = EventSeverity.Info
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("Informational", entry.Explanation);
        Assert.Contains("No action", entry.Recommendation);
    }

    [Fact]
    public void Enrich_UnknownEvent_VerboseSeverity_LowLevelDiagnostic()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "TestProvider",
            EventId = 200,
            Severity = EventSeverity.Verbose
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("diagnostic", entry.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enrich_EmptyProviderName_DoesNotThrow()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "",
            EventId = 41,
            Severity = EventSeverity.Error
        };

        var exception = Record.Exception(() => EventExplainer.Enrich(entry));
        Assert.Null(exception);
        Assert.NotEmpty(entry.Explanation);
    }

    [Fact]
    public void Enrich_ApplicationError1000_SetsExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "Application Error",
            EventId = 1000,
            Severity = EventSeverity.Error
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("crashed", entry.Explanation);
    }

    [Fact]
    public void Enrich_DnsClient1014_SetsExplanation()
    {
        var entry = new FriendlyEventEntry
        {
            ProviderName = "Microsoft-Windows-DNS-Client",
            EventId = 1014,
            Severity = EventSeverity.Warning
        };

        EventExplainer.Enrich(entry);

        Assert.Contains("DNS", entry.Explanation);
    }
}
