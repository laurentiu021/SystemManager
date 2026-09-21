// SysManager · SystemReportRedactionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// No export format carries the machine's MAC address or full local IPv4.
/// </summary>
/// <remarks>
/// The report is the app's own answer to "produce evidence for a bug" — SUPPORT.md, the issue template and
/// the About tab's "Report a problem" button all funnel someone toward attaching one — and a MAC address is
/// permanent: it survives a Windows reinstall and cannot be un-published once it is in a GitHub thread. All
/// three formats carried both fields, undisclosed, and the HTML footer reassured the reader that "no data
/// leaves this machine", which is true of the application and beside the point for a file meant to be sent
/// to somebody (#2352).
/// <para>Redaction now happens in <c>GenerateDataAsync</c>, the single point every format passes through,
/// rather than in each renderer. These tests exercise the helper and the renderers over it; the guard that
/// no format can BYPASS that point is
/// <c>ArchitectureTests.EveryReportFormat_GoesThroughTheRedactingDataPath</c>.</para>
/// </remarks>
public class SystemReportRedactionTests
{
    private const string RealMac = "AA-BB-CC-DD-EE-FF";
    private const string RealIp = "192.168.1.10";

    private static SystemReportData Sample(params NetworkAdapterInfo[] adapters) => new(
        GeneratedAt: new DateTime(2026, 1, 2, 13, 14, 15, DateTimeKind.Local),
        AppVersion: "1.112.0",
        Os: new OsInfo("Windows 11 Pro", "10.0.22631", "22631", TimeSpan.FromHours(50), "64-bit"),
        Cpu: new CpuInfo("Intel Core i7-12700K", 12, 20, 5000, 18.0),
        Memory: new MemoryInfo(32, 16, 16, 50, [new MemoryModule("DIMM0", "Corsair", 16, 3200, 2400, "PN1")]),
        Gpus: [new GpuReportInfo("NVIDIA RTX 4070", 12.0, "551.86")],
        Motherboard: "ASUS ROG STRIX Z690",
        Disks: [new DiskReportInfo("Samsung 980 PRO", "SSD", "NVMe", 1000, "Healthy", "Healthy", 38.0, 5, "1.4y")],
        NetworkAdapters: adapters.Length > 0
            ? adapters
            : [new NetworkAdapterInfo("Intel I225-V", RealIp, RealMac, true)]);

    [Fact]
    public void Redaction_DropsTheMacAndMasksTheHostPart()
    {
        var adapter = Assert.Single(SystemReportService.WithoutMachineIdentifiers(Sample()).NetworkAdapters);

        Assert.Equal("(not included)", adapter.MacAddress);
        Assert.Equal("192.168.x.x", adapter.IPv4);

        // The adapter DESCRIPTION stays: "Intel I225-V" is a hardware model like every other line in the
        // report, and it is what actually answers a "no internet" question.
        Assert.Equal("Intel I225-V", adapter.Description);
    }

    [Fact]
    public void Redaction_KeepsAnAbsentMacDistinctFromARemovedOne()
    {
        // A placeholder for a value that existed, an empty string for one the adapter never reported. The
        // text builder prints this field either way, so collapsing the two would tell the reader the adapter
        // had a MAC we hid when in fact it reported none.
        var adapter = Assert.Single(SystemReportService.WithoutMachineIdentifiers(
            Sample(new NetworkAdapterInfo("Loopback", "", "", false))).NetworkAdapters);

        Assert.Equal("", adapter.MacAddress);
        Assert.Equal("", adapter.IPv4);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.x.x")]
    [InlineData("169.254.13.7", "169.254.x.x")]   // the self-assigned case, the actual signal in a report
    [InlineData("192.168.1.10", "192.168.x.x")]
    public void Redaction_KeepsTheNetworkHalfSoTheDiagnosisSurvives(string input, string expected)
    {
        Assert.Equal(expected, SystemReportService.MaskHostPart(input));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.10.5")]
    [InlineData("192.168.x.x")]        // already masked: masking twice must not corrupt it
    [InlineData("")]
    public void Redaction_LeavesAnythingThatIsNotFourNumericPartsAlone(string input)
    {
        Assert.Equal(input, SystemReportService.MaskHostPart(input));
    }

    [Fact]
    public void Redaction_CannotTellAnAddressFromAVersion_WhichIsWhyItRunsOnTheFieldNotTheText()
    {
        // "1.112.0.0" is four valid octets, so the mask happily turns it into "1.112.x.x". That is not a
        // defect in the mask — it is the reason redaction runs over the TYPED IPv4 field rather than by
        // pattern-matching the rendered report. A regex sweep across the finished text would have hit the
        // version line and silently corrupted it while claiming to protect an address.
        Assert.Equal("1.112.x.x", SystemReportService.MaskHostPart("1.112.0.0"));

        // And the proof that it does not happen in practice: the version survives every format intact.
        var redacted = SystemReportService.WithoutMachineIdentifiers(Sample());
        Assert.Equal("1.112.0", redacted.AppVersion);
        Assert.Contains("1.112.0", SystemReportService.BuildText(redacted), StringComparison.Ordinal);
        Assert.Contains("1.112.0", SystemReportService.BuildHtml(redacted), StringComparison.Ordinal);
    }

    [Fact]
    public void NoRenderedFormat_ShowsTheRealValues()
    {
        // The point of the whole change, asserted on the finished output of all three formats rather than on
        // the data — because the data is not what gets attached to an issue.
        var redacted = SystemReportService.WithoutMachineIdentifiers(Sample());

        foreach (var (format, rendered) in new[]
        {
            ("text", SystemReportService.BuildText(redacted)),
            ("HTML", SystemReportService.BuildHtml(redacted)),
            ("JSON", SystemReportService.BuildJson(redacted)),
        })
        {
            Assert.DoesNotContain(RealMac, rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RealIp, rendered, StringComparison.Ordinal);

            // Non-vacuity: the network section has to actually be in there, or "the MAC is absent" is being
            // asserted about a report that never mentioned the adapter at all.
            Assert.Contains("Intel I225-V", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHtmlFooter_DescribesTheFileRatherThanTheApp()
    {
        // It read "no data leaves this machine" — true of the application, and beside the point printed at the
        // bottom of a file whose purpose is to be sent to somebody. It now says what is true of the FILE.
        var html = SystemReportService.BuildHtml(SystemReportService.WithoutMachineIdentifiers(Sample()));

        Assert.Contains("safe to share", html, StringComparison.Ordinal);
        Assert.DoesNotContain("no data leaves this machine", html, StringComparison.Ordinal);
    }
}
