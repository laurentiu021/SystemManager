// SysManager · TrayIconServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="TrayIconService"/> — validates notification
/// threshold logic and property defaults. Actual tray icon display is
/// integration-level (requires UI thread + Windows shell).
/// </summary>
public class TrayIconServiceTests
{
    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.NotNull(svc);
        svc.Dispose();
    }

    [Fact]
    public void MinimizeToTray_DefaultTrue()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.True(svc.MinimizeToTray);
        svc.Dispose();
    }

    [Fact]
    public void NotificationsEnabled_DefaultTrue()
    {
        var svc = new TrayIconService(new SystemInfoService());
        Assert.True(svc.NotificationsEnabled);
        svc.Dispose();
    }

    [Fact]
    public void MinimizeToTray_CanBeDisabled()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.MinimizeToTray = false;
        Assert.False(svc.MinimizeToTray);
        svc.Dispose();
    }

    [Fact]
    public void NotificationsEnabled_CanBeDisabled()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.NotificationsEnabled = false;
        Assert.False(svc.NotificationsEnabled);
        svc.Dispose();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        svc.Dispose();
        var ex = Record.Exception(() => svc.Dispose());
        Assert.Null(ex);
    }

    // ---------- notification threshold logic ----------

    [Fact]
    public void CheckAndNotify_LowRam_DoesNotThrow()
    {
        // RAM at 50% — should NOT trigger notification
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 50, uptimeDays: 1);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_HighRam_DoesNotThrow()
    {
        // RAM at 95% — should trigger notification (but no tray icon = no crash)
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 95, uptimeDays: 1);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_HighUptime_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = MakeSnapshot(ramUsedPct: 50, uptimeDays: 20);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    [Fact]
    public void CheckAndNotify_UnhealthyDisk_DoesNotThrow()
    {
        var svc = new TrayIconService(new SystemInfoService());
        var snapshot = new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(1), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(16, 8, 8, 50, new List<MemoryModule>()),
            new List<DiskInfo>
            {
                new("TestDisk", "SSD", "NVMe", 500, "Warning", "OK", 45, 10)
            },
            DateTime.Now);
        var ex = Record.Exception(() => svc.CheckAndNotify(snapshot));
        Assert.Null(ex);
        svc.Dispose();
    }

    // ---------- the one-line readout the context menu header shows ----------

    /// <summary>
    /// Builds a snapshot with every figure the menu header renders under the test's control.
    /// </summary>
    private static SystemSnapshot Reading(double cpuPct, double usedGB, double totalGB, TimeSpan uptime) =>
        new(new OsInfo("Windows 11", "10.0", "22631", uptime, "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, cpuPct),
            new MemoryInfo(totalGB, totalGB - usedGB, usedGB, usedGB / totalGB * 100, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);

    [Fact]
    public void MenuStatusText_ReadsCpuThenMemoryThenUptime()
    {
        // The whole string, not fragments: this is the first thing visible when the menu opens, and the
        // order is the point — the CPU figure sits directly above the "What's using my PC" item, which is
        // what makes the menu a short diagnostic path rather than a list of links.
        var text = TrayIconService.MenuStatusText(
            Reading(cpuPct: 12, usedGB: 9.4, totalGB: 31.9, uptime: new TimeSpan(3, 4, 30, 0)));

        Assert.Equal("CPU 12%  ·  RAM 9.4/31.9 GB  ·  up 3d 4h", text);
    }

    [Fact]
    public void MenuStatusText_IsOneLine()
    {
        // A MenuItem header renders a newline literally and would grow the row rather than wrap. The
        // tooltip's three-line shape is next to this in the same file, so the two are easy to mix up.
        var text = TrayIconService.MenuStatusText(Reading(50, 8, 16, TimeSpan.FromHours(5)));

        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
    }

    [Theory]
    [InlineData("ro-RO")]   // comma decimal separator
    [InlineData("de-DE")]
    [InlineData("tr-TR")]   // the dotted-I culture, which also breaks casing
    public void MenuStatusText_UsesInvariantNumbers_OnAnyRegionalSetting(string culture)
    {
        // The figures are formatted through CultureInfo.InvariantCulture deliberately, matching the tooltip
        // beside it and FormatHelper. Without it the same menu reads "9,4/31,9 GB" for most of Europe while
        // every other number in the app stays dotted — a mixed screen, which is worse than either choice.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var text = TrayIconService.MenuStatusText(Reading(12, 9.4, 31.9, new TimeSpan(3, 4, 0, 0)));

            Assert.Contains("9.4/31.9 GB", text, StringComparison.Ordinal);
            Assert.DoesNotContain("9,4", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(12.4, "CPU 12%")]
    [InlineData(12.6, "CPU 13%")]
    [InlineData(0, "CPU 0%")]
    [InlineData(100, "CPU 100%")]
    public void MenuStatusText_RoundsCpuToAWholeNumber(double cpuPct, string expected)
    {
        // No decimal place on a figure that moves every poll: "CPU 12.6%" implies a precision a 60-second
        // sample does not have, and the extra character buys nothing at a glance.
        Assert.Contains(expected, TrayIconService.MenuStatusText(Reading(cpuPct, 8, 16, TimeSpan.FromHours(1))),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 4, "up 0d 4h")]      // under a day still says the day
    [InlineData(0, 0, "up 0d 0h")]      // freshly booted
    [InlineData(21, 23, "up 21d 23h")]
    public void MenuStatusText_ShowsUptimeAsDaysAndHours(int days, int hours, string expected)
    {
        // Days are kept even at zero rather than switching shape below 24 hours: a header that changes
        // format is harder to read at a glance than one that always has the same three parts.
        var text = TrayIconService.MenuStatusText(Reading(10, 8, 16, new TimeSpan(days, hours, 0, 0)));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuStatusText_ReportsTheSameFiguresAsTheTooltip()
    {
        // The two renderings are deliberately separate — one line versus three, capped at 127 characters
        // versus uncapped — but they read ONE snapshot, so they must never disagree about the numbers.
        // That is the part a user would notice, and the only part worth pinning across both.
        var snapshot = Reading(cpuPct: 37, usedGB: 12.5, totalGB: 64, uptime: new TimeSpan(2, 9, 0, 0));
        var text = TrayIconService.MenuStatusText(snapshot);

        Assert.Contains("37%", text, StringComparison.Ordinal);
        Assert.Contains("12.5/64.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("2d 9h", text, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    private static SystemSnapshot MakeSnapshot(double ramUsedPct, int uptimeDays)
    {
        double totalGB = 16;
        double usedGB = totalGB * ramUsedPct / 100;
        return new SystemSnapshot(
            new OsInfo("Windows 11", "10.0", "22631", TimeSpan.FromDays(uptimeDays), "64-bit"),
            new CpuInfo("Test CPU", 8, 16, 3600, 10),
            new MemoryInfo(totalGB, totalGB - usedGB, usedGB, ramUsedPct, new List<MemoryModule>()),
            new List<DiskInfo>(),
            DateTime.Now);
    }

    // ── Disk-health notification: which statuses are actually a problem ──────────────────────────
    //
    // The check was `d.HealthStatus != "Healthy"`, and SystemInfoService.QueryDisks has TWO producer
    // arms. The MSFT_PhysicalDisk arm maps to "Healthy"/"Warning"/"Unhealthy"/"Unknown", but the
    // Win32_DiskDrive FALLBACK passes Win32_DiskDrive.Status straight through — and that reports "OK"
    // for a perfectly good disk. So on any machine that took the fallback the tray fired on every
    // drive and popped "Disk Health Warning — reports status: OK": a toast that contradicts itself and
    // tells a non-technical user to back up over nothing.

    [Theory]
    [InlineData("Healthy")]      // MSFT_PhysicalDisk, healthy
    [InlineData("OK")]           // Win32_DiskDrive fallback, healthy — the false alarm
    [InlineData("Unknown")]      // unreadable: not knowing is not a failure
    [InlineData("")]             // absent value
    [InlineData(null)]
    public void IsDiskProblem_HealthyOrUnknownStatus_DoesNotWarn(string? status)
    {
        Assert.False(TrayIconService.IsDiskProblem(status));
    }

    [Theory]
    [InlineData("Warning")]              // MSFT_PhysicalDisk mapping
    [InlineData("Unhealthy")]
    public void IsDiskProblem_PhysicalDiskProblemStatus_Warns(string status)
    {
        // The other half: keying on problem values rather than "not Healthy" must not stop the tray
        // warning about a drive that genuinely is failing.
        Assert.True(TrayIconService.IsDiskProblem(status));
    }

    [Theory]
    [InlineData("Degraded")]
    [InlineData("Stressed")]
    [InlineData("Pred Fail")]
    [InlineData("Error")]
    [InlineData("NonRecover")]
    [InlineData("Lost Comm")]
    [InlineData("No Contact")]
    public void IsDiskProblem_Win32FallbackProblemStatus_Warns(string status)
    {
        // These are the values the FALLBACK arm can actually produce, and they matter more than they
        // look. Win32_DiskDrive.Status uses CIM's ABBREVIATED vocabulary — capped at 10 characters, so
        // "Pred Fail" and "NonRecover", never "Predictive Failure" or "Non-Recoverable Error". Those
        // long forms belong to OperationalStatus, a different DiskInfo property the tray never reads.
        //
        // Worth stating because the first version of this fix keyed on the long names only: it read as
        // full coverage of failing drives while in fact matching nothing the fallback emits, so a disk
        // reporting "Pred Fail" — a drive announcing its own imminent death — would have fallen through
        // to "unrecognised, therefore fine" and warned nobody. Trading a false alarm for a missed real
        // failure would have been a strictly worse bug than the one being fixed.
        Assert.True(TrayIconService.IsDiskProblem(status));
    }

    [Theory]
    [InlineData("Predictive Failure")]
    [InlineData("Non-Recoverable Error")]
    public void IsDiskProblem_LongFormOperationalStatus_AlsoWarns(string status)
    {
        // No current caller passes OperationalStatus here, but accepting its wording costs nothing and
        // keeps the predicate honest if one ever does.
        Assert.True(TrayIconService.IsDiskProblem(status));
    }

    [Fact]
    public void IsDiskProblem_ToleratesSurroundingWhitespace()
    {
        // WMI string values arrive as-is from the provider; a padded value must not silently become
        // "unrecognised, therefore fine".
        Assert.True(TrayIconService.IsDiskProblem("  Unhealthy  "));
        Assert.False(TrayIconService.IsDiskProblem("  OK  "));
    }

    [Fact]
    public void IsDiskProblem_AgreesWithEveryStatusTheProducerCanEmit()
    {
        // The guard the first attempt at this fix needed and did not have. A predicate keyed on literal
        // strings is only correct while those strings match what the PRODUCER writes, and nothing in the
        // compiler connects the two: SystemInfoService.QueryDisks builds DiskInfo.HealthStatus from two
        // unrelated vocabularies, and getting one wrong fails OPEN — an unmatched value reads as "no
        // problem", so a failing drive warns nobody and no test notices.
        //
        // Enumerating the producer's full value set here closes that. The Win32 half is not a guess: it
        // is the complete ValueMap the CIM schema declares for Win32_DiskDrive.Status, read off this
        // machine with Get-CimClass — all twelve values, partitioned into the two verdicts.
        var win32Ok = new[] { "OK", "Unknown", "Starting", "Stopping", "Service" };
        var win32Problem = new[] { "Error", "Degraded", "Pred Fail", "Stressed", "NonRecover", "No Contact", "Lost Comm" };

        // MSFT_PhysicalDisk's HealthStatus map, the primary arm's three outcomes plus its unknown.
        var physicalOk = new[] { "Healthy", "Unknown" };
        var physicalProblem = new[] { "Warning", "Unhealthy" };

        Assert.All([.. win32Ok, .. physicalOk], (string s) =>
            Assert.False(TrayIconService.IsDiskProblem(s),
                $"\"{s}\" is not a failure, but it would raise a back-up-your-data toast."));
        Assert.All([.. win32Problem, .. physicalProblem], (string s) =>
            Assert.True(TrayIconService.IsDiskProblem(s),
                $"\"{s}\" IS a failure the producer can emit, and it would warn nobody."));

        // The two Win32 groups must together be the WHOLE ValueMap — an unclassified value is precisely
        // the fail-open hole described above, so leaving one out has to break this test rather than pass
        // quietly. Twelve is the schema's count, not a number chosen to match the arrays.
        Assert.Equal(12, win32Ok.Length + win32Problem.Length);

        // "Service"/"Starting"/"Stopping" are transient states, not failures — a disk being serviced is
        // not a disk dying, and an unprompted "back up important data" toast over one would be exactly
        // the false alarm this fix removes.
    }
}
