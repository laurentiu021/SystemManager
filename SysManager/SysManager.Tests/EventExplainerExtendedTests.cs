// SysManager · EventExplainerExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class EventExplainerExtendedTests
{
    private static FriendlyEventEntry Make(string provider, int id, EventSeverity sev = EventSeverity.Error) => new()
    {
        ProviderName = provider,
        EventId = id,
        Severity = sev
    };

    // ---------- Kernel / crashes ----------

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41)]
    [InlineData("BugCheck", 1001)]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001)]
    [InlineData("Application Error", 1000)]
    [InlineData("Application Hang", 1002)]
    [InlineData(".NET Runtime", 1026)]
    public void KernelAndCrashes_AreExplained(string provider, int id)
    {
        var e = Make(provider, id);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(e.Recommendation));
    }

    [Theory]
    [InlineData("disk", 7)]
    [InlineData("disk", 11)]
    [InlineData("disk", 51)]
    [InlineData("Ntfs", 55)]
    [InlineData("volmgr", 161)]
    public void DiskEvents_AreExplained(string provider, int id)
    {
        var e = Make(provider, id);
        EventExplainer.Enrich(e);
        // Each disk-related event should mention one of the storage terms.
        var text = e.Explanation.ToLowerInvariant();
        Assert.True(
            text.Contains("disk") || text.Contains("drive") ||
            text.Contains("pagefile") || text.Contains("file-system") ||
            text.Contains("ntfs") || text.Contains("write") || text.Contains("dump"),
            $"Explanation for {provider}/{id} lacked a storage term: {e.Explanation}");
    }

    [Theory]
    [InlineData("Microsoft-Windows-DNS-Client", 1014)]
    [InlineData("Tcpip", 4227)]
    [InlineData("Microsoft-Windows-NetBT", 4321)]
    public void NetworkEvents_AreExplained(string provider, int id)
    {
        var e = Make(provider, id);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
    }

    [Theory]
    [InlineData("Microsoft-Windows-WindowsUpdateClient", 20)]
    [InlineData("Microsoft-Windows-WindowsUpdateClient", 25)]
    [InlineData("Microsoft-Windows-Servicing", 3)]
    public void WindowsUpdate_AreExplained(string provider, int id)
    {
        var e = Make(provider, id);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
    }

    [Theory]
    [InlineData(7000)] [InlineData(7001)] [InlineData(7009)]
    [InlineData(7011)] [InlineData(7023)] [InlineData(7031)] [InlineData(7034)]
    public void ServiceControlManager_AllEvents_AreExplained(int id)
    {
        var e = Make("Service Control Manager", id);
        EventExplainer.Enrich(e);
        Assert.Contains("service", e.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(4625)]
    [InlineData(4740)]
    public void SecurityEvents_AreExplained(int id)
    {
        var e = Make("Microsoft-Windows-Security-Auditing", id);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
    }

    [Theory]
    [InlineData("Display", 4101)]
    [InlineData("nvlddmkm", 153)]
    [InlineData("amdkmdag", 4101)]
    public void GpuEvents_AreExplained(string provider, int id)
    {
        var e = Make(provider, id);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
    }

    // ---------- Fallback behavior ----------

    [Theory]
    [InlineData(EventSeverity.Critical)]
    [InlineData(EventSeverity.Error)]
    [InlineData(EventSeverity.Warning)]
    [InlineData(EventSeverity.Info)]
    [InlineData(EventSeverity.Verbose)]
    public void UnknownEvent_GetsGenericExplanation(EventSeverity sev)
    {
        var e = Make("Some-Unknown-Provider", 123_456, sev);
        EventExplainer.Enrich(e);
        Assert.False(string.IsNullOrWhiteSpace(e.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(e.Recommendation));
    }

    [Fact]
    public void UnknownProviderButKnownId_FallsBackToIdEntry()
    {
        var e = Make("Weird-Provider", 6008);
        EventExplainer.Enrich(e);
        Assert.Contains("unexpected", e.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Idempotency ----------

    [Fact]
    public void CallingEnrichTwice_YieldsSameResult()
    {
        var a = Make("Microsoft-Windows-Kernel-Power", 41, EventSeverity.Critical);
        var b = Make("Microsoft-Windows-Kernel-Power", 41, EventSeverity.Critical);
        EventExplainer.Enrich(a);
        EventExplainer.Enrich(b);
        EventExplainer.Enrich(a);
        Assert.Equal(a.Explanation, b.Explanation);
        Assert.Equal(a.Recommendation, b.Recommendation);
    }
}
