// SysManager · DiagnosticsBundleRedactionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The report inside a diagnostics bundle carries none of THIS machine's network identifiers.
/// </summary>
/// <remarks>
/// Integration rather than unit, because the only assertion worth making runs against the real adapters: a
/// canned report can only prove that a pattern which was never there is still not there. The bundle exists to
/// be attached to a public issue (#1650), a MAC address is permanent and survives a Windows reinstall, and
/// nobody can withdraw one once it is posted — so this is the test that makes the bundle safe rather than
/// merely intended to be safe.
/// <para><b>No failure message here prints an identifier.</b> A failing assertion's text goes into the CI log,
/// which is public, so a naive <c>Assert.DoesNotContain(mac, report)</c> would publish the very value it
/// exists to protect on the day it broke. Every check below states the FIELD and not the VALUE.</para>
/// </remarks>
public sealed class DiagnosticsBundleRedactionTests : IDisposable
{
    private readonly string _root;

    public DiagnosticsBundleRedactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SysManagerBundleRedaction", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    /// <summary>This machine's hardware addresses, in both the spellings a report could render.</summary>
    private static List<string> LocalHardwareAddresses()
    {
        var spellings = new List<string>();
        foreach (var raw in NetworkInterface.GetAllNetworkInterfaces()
                     .Select(n => n.GetPhysicalAddress().ToString())
                     .Where(s => s.Length == 12)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            spellings.Add(raw);
            spellings.Add(string.Join(":", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))));
            spellings.Add(string.Join("-", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))));
        }
        return spellings;
    }

    /// <summary>This machine's IPv4 addresses, excluding loopback.</summary>
    private static List<string> LocalIPv4Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a))
            .Select(a => a.ToString())
            .Distinct(StringComparer.Ordinal)];

    [Fact]
    public async Task TheBundledReport_CarriesNoHardwareAddressAndNoFullLocalIP()
    {
        var macs = LocalHardwareAddresses();
        var ips = LocalIPv4Addresses();

        var service = new DiagnosticsBundleService(
            new SystemReportService(new SystemInfoService(), new DiskHealthService()),
            Path.Combine(_root, "logs"));
        var zipPath = Path.Combine(_root, "bundle.zip");

        await service.WriteAsync(zipPath, "environment block");

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("system-report.txt");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        var report = reader.ReadToEnd();

        // The report was really produced, so the checks below are not passing over an empty string.
        Assert.Contains("SysManager System Report", report, StringComparison.Ordinal);
        Assert.Contains("Network", report, StringComparison.Ordinal);

        // Vacuity floor. With no adapter reporting a hardware address there is nothing to look for, and a
        // green result would mean nothing at all.
        Assert.True(macs.Count > 0,
            "no adapter on this machine reports a hardware address, so the redaction check below would pass "
            + "over an empty list. Run this where at least one adapter exists.");

        var leakedFields = new List<string>();
        if (macs.Any(m => report.Contains(m, StringComparison.OrdinalIgnoreCase)))
            leakedFields.Add("a network adapter's hardware (MAC) address");
        if (ips.Any(ip => report.Contains(ip, StringComparison.Ordinal)))
            leakedFields.Add("a full local IPv4 address");

        // Field names, never values: this message reaches the CI log, which is public.
        Assert.True(leakedFields.Count == 0,
            "the report inside the diagnostics bundle still carries "
            + string.Join(" and ", leakedFields)
            + ". The value is deliberately not printed here. The bundle is built to be attached to a public "
            + "issue, so this is a leak rather than a cosmetic problem — see "
            + "SystemReportService.WithoutMachineIdentifiers.");
    }

    /// <summary>
    /// The FULL report still carries what the sharable one drops, so the two are genuinely different.
    /// </summary>
    /// <remarks>
    /// Without this, the redaction test above would pass just as happily against a machine whose report never
    /// contained an address in the first place — a virtualised runner with no adapter, or a formatting change
    /// that dropped the Network section entirely. Asserting the unredacted report DOES contain the value is
    /// what turns the other test from "nothing found" into "something was removed".
    /// <para>Same rule about the message: it names the field, never the value.</para>
    /// </remarks>
    [Fact]
    public async Task TheFullReport_StillCarriesTheAddress_SoTheRedactionIsRealRatherThanVacuous()
    {
        var macs = LocalHardwareAddresses();
        Assert.True(macs.Count > 0,
            "no adapter on this machine reports a hardware address, so this test cannot establish that the "
            + "redaction removes anything.");

        var report = await new SystemReportService(new SystemInfoService(), new DiskHealthService())
            .GenerateReportAsync();

        Assert.True(macs.Any(m => report.Contains(m, StringComparison.OrdinalIgnoreCase)),
            "the FULL system report no longer contains any of this machine's hardware addresses. Either the "
            + "report stopped emitting the field — in which case the sharable variant and its redaction are "
            + "now pointless machinery and should go — or this machine has no adapter the report can see, in "
            + "which case the companion redaction test is passing vacuously. Value not printed: this message "
            + "reaches a public log.");
    }
}
