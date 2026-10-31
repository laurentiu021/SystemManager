// SysManager · MemoryTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The memory-error scan's failure path: a log that cannot be read is an error, not a clean 30 days.
/// </summary>
/// <remarks>
/// The scan that succeeds is covered in the integration project, against the real System log. This half needs no
/// such log, because it names one that does not exist.
/// </remarks>
public class MemoryTestServiceTests
{
    /// <summary>
    /// A log name no machine has, which is the one way to make the real reader fail on demand. The Dashboard and
    /// System Health tests use it too, to reach the same failure through each of them.
    /// </summary>
    internal const string NoSuchLog = "SysManagerTestsNoSuchLog";

    [Fact]
    public async Task CheckErrorLogs_WhenTheLogCannotBeRead_ThrowsInsteadOfReportingNoErrors()
    {
        // #2479. The scan caught this itself and returned zero errors, so the Dashboard and System Health both said
        // "No memory errors" in green, and the could-not-check branch each of them has could never run.
        var svc = new MemoryTestService(NoSuchLog);

        await Assert.ThrowsAnyAsync<EventLogException>(() => svc.CheckErrorLogsAsync());
    }

    // ---------- what is counted (#2715) ----------
    // WHEA 17 to 20 were counted as memory errors, which are PCI Express and processor errors, and of Windows Memory
    // Diagnostic's results only 1201, a test that found no errors. The records are given newest first, as the log
    // reader reads them.

    private const string Whea = "Microsoft-Windows-WHEA-Logger";
    private const string Diagnostic = "Microsoft-Windows-MemoryDiagnostics-Results";
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0);
    private static readonly DateTime Since = Now.AddDays(-30);

    [Fact]
    public void Summarize_CountsMemoryErrors_NotPciExpressOrProcessorOnes()
    {
        var summary = MemoryTestService.Summarize(
        [
            (Whea, 17, Now),
            (Whea, 18, Now),
            (Whea, 19, Now),
            (Whea, 20, Now),
            (Whea, 47, Now.AddHours(-1)),
        ], Since);

        Assert.Equal(1, summary.WheaMemoryErrors);
        Assert.Equal(Now.AddHours(-1), summary.LastError);
    }

    [Fact]
    public void Summarize_ATestThatFoundNoErrors_IsNoError()
    {
        var summary = MemoryTestService.Summarize([(Diagnostic, 1201, Now), (Diagnostic, 1101, Now)], Since);

        Assert.Equal(new MemoryTestService.MemoryErrorSummary(0, 0, null), summary);
    }

    [Fact]
    public void Summarize_ATestThatFoundErrors_IsCounted()
    {
        var summary = MemoryTestService.Summarize([(Diagnostic, 1202, Now)], Since);

        Assert.Equal(1, summary.MemoryDiagnosticErrors);
        Assert.Equal(Now, summary.LastError);
    }

    [Fact]
    public void Summarize_StopsAtTheFirstRecordOlderThanThirtyDays()
    {
        var summary = MemoryTestService.Summarize(
        [
            (Whea, 47, Now),
            (Whea, 47, Now.AddDays(-31)),
            // Never reached: the reader stops at the first record older than the 30 days.
            (Whea, 47, Now.AddDays(-1)),
        ], Since);

        Assert.Equal(1, summary.WheaMemoryErrors);
    }
}
