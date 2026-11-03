// SysManager · EventExplainerExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001)]
    [InlineData("Windows Error Reporting", 1001)]
    [InlineData("EventLog", 6008)]
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

    [Fact]
    public void WindowsUpdateEvent25_SaysTheCheckFailed_AndAsksForAction()
    {
        // The provider's own message for 25 is "Windows Update failed to check for updates with error %1", at
        // Error level. The row used to call it an uninstall that needs no action, which only a test of the words
        // themselves would have caught: the theory above checks that some explanation exists.
        Assert.True(EventExplainer.TryExplain("Microsoft-Windows-WindowsUpdateClient", 25, out var info));

        Assert.Contains("could not check for updates", info.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstalled", info.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no action needed", info.Recommendation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reset Windows Update", info.Recommendation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7000)]
    [InlineData(7001)]
    [InlineData(7009)]
    [InlineData(7011)]
    [InlineData(7023)]
    [InlineData(7031)]
    [InlineData(7034)]
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

    /// <summary>
    /// An event ID belongs to the provider that wrote it, so another provider's event with the same ID gets the
    /// explanation built from its severity and source, not one written for something else (#2776). A table keyed by
    /// the ID alone explained every event 1000 as a program crash, an informational .NET Runtime trace line among them.
    /// </summary>
    [Theory]
    [InlineData(".NET Runtime", 1000, EventSeverity.Info, "Informational event from '.NET Runtime'.")]
    [InlineData("Some-Watchdog", 1000, EventSeverity.Info, "Informational event from 'Some-Watchdog'.")]
    [InlineData("Some-Provider", 1001, EventSeverity.Info, "Informational event from 'Some-Provider'.")]
    [InlineData("Weird-Provider", 6008, EventSeverity.Error, "An error was logged by 'Weird-Provider'.")]
    [InlineData("Weird-Provider", 41, EventSeverity.Error, "An error was logged by 'Weird-Provider'.")]
    public void AnotherProvidersEventWithTheSameId_IsNotExplainedAsSomethingElse(
        string provider, int id, EventSeverity severity, string explained)
    {
        var e = Make(provider, id, severity);

        EventExplainer.Enrich(e);

        Assert.Equal(explained, e.Explanation);
        Assert.False(EventExplainer.TryExplain(provider, id, out _));
    }

    /// <summary>
    /// A blue screen is explained as one, by the provider Windows records for "The computer has rebooted from a
    /// bugcheck" (#2776). The row was keyed by BugCheck, the source Event Viewer shows, so it never matched, and a blue
    /// screen got the text for an application's crash report.
    /// </summary>
    [Fact]
    public void ABlueScreen_IsExplainedAsOne()
    {
        var e = Make("Microsoft-Windows-WER-SystemErrorReporting", 1001);

        EventExplainer.Enrich(e);

        Assert.Equal("Windows blue-screened (BSOD) and recovered. A memory dump was written.", e.Explanation);
        Assert.Contains(".dmp", e.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApplicationsCrashReport_IsExplainedAsOne()
    {
        var e = Make("Windows Error Reporting", 1001, EventSeverity.Info);

        EventExplainer.Enrich(e);

        Assert.Equal("A crash report was filed with Windows Error Reporting.", e.Explanation);
    }

    /// <summary>
    /// Tcpip 4227 is a program reusing local ports, in Windows' own words, not a bad link (#2776). It was blamed on
    /// Wi-Fi or a cable, which cannot cause it.
    /// </summary>
    [Fact]
    public void TcpipLocalPortReuse_IsNotBlamedOnTheNetworkLink()
    {
        var e = Make("Tcpip", 4227, EventSeverity.Warning);

        EventExplainer.Enrich(e);

        Assert.Contains("ran short of local ports", e.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("Wi-Fi", e.Explanation + e.Recommendation, StringComparison.Ordinal);
        Assert.DoesNotContain("cable", e.Explanation + e.Recommendation, StringComparison.Ordinal);
    }

    // ---------- Memory test results (#2715) ----------
    // 1201 was explained as "detected memory errors" under a message saying the test detected none, and the results
    // that found errors had no explanation. By the provider manifest, 1101 and 1201 "detected no errors", and 1102 and
    // 1202 "detected hardware errors".

    [Theory]
    [InlineData(1101)]
    [InlineData(1201)]
    public void AMemoryTestThatFoundNoErrors_IsExplainedAsPassed(int id)
    {
        var e = Make("Microsoft-Windows-MemoryDiagnostics-Results", id, EventSeverity.Info);
        EventExplainer.Enrich(e);
        Assert.Equal("Windows Memory Diagnostic tested your RAM and found no errors.", e.Explanation);
        Assert.DoesNotContain("faulty", e.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1102)]
    [InlineData(1202)]
    public void AMemoryTestThatFoundErrors_SaysTheRamIsLikelyFaulty(int id)
    {
        var e = Make("Microsoft-Windows-MemoryDiagnostics-Results", id);
        EventExplainer.Enrich(e);
        Assert.Equal("Windows Memory Diagnostic tested your RAM and found hardware errors.", e.Explanation);
        Assert.Contains("likely faulty", e.Recommendation, StringComparison.Ordinal);
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
