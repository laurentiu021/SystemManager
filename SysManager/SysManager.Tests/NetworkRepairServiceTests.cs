// SysManager · NetworkRepairServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="NetworkRepairService"/> (audit finding tests #8).
/// <para>
/// Each repair routes a fixed command through the <see cref="IPowerShellRunner"/>
/// seam (<c>ipconfig /flushdns</c>, <c>netsh winsock reset</c>,
/// <c>netsh int ip reset</c>) and maps the exit code plus a fixed reboot flag
/// into a <see cref="SysManager.Models.NetworkRepairResult"/>. These tests pin
/// the exact invocation and the Success/NeedsReboot mapping with zero OS
/// interaction by substituting the runner.
/// </para>
/// </summary>
public class NetworkRepairServiceTests
{
    private static IPowerShellRunner RunnerReturning(int exitCode)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(exitCode);
        return runner;
    }

    // ---------- DNS flush — no reboot ----------

    [Fact]
    public async Task FlushDnsAsync_RunsIpconfigFlushDns()
    {
        var runner = RunnerReturning(0);
        using var svc = new NetworkRepairService(runner);

        var result = await svc.FlushDnsAsync();

        await runner.Received(1).RunProcessAsync(
            "ipconfig.exe", "/flushdns", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.True(result.Success);
        Assert.False(result.NeedsReboot);
        Assert.Equal("DNS Flush", result.ToolName);
    }

    // ---------- Winsock reset — needs reboot ----------

    [Fact]
    public async Task ResetWinsockAsync_RunsNetshWinsockReset_NeedsReboot()
    {
        var runner = RunnerReturning(0);
        using var svc = new NetworkRepairService(runner);

        var result = await svc.ResetWinsockAsync();

        await runner.Received(1).RunProcessAsync(
            "netsh.exe", "winsock reset", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.True(result.Success);
        Assert.True(result.NeedsReboot);
        Assert.Equal("Winsock Reset", result.ToolName);
    }

    // ---------- TCP/IP reset — needs reboot ----------

    [Fact]
    public async Task ResetTcpIpAsync_RunsNetshIntIpReset_NeedsReboot()
    {
        var runner = RunnerReturning(0);
        using var svc = new NetworkRepairService(runner);

        var result = await svc.ResetTcpIpAsync();

        await runner.Received(1).RunProcessAsync(
            "netsh.exe", "int ip reset", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.True(result.Success);
        Assert.True(result.NeedsReboot);
        Assert.Equal("TCP/IP Reset", result.ToolName);
    }

    // ---------- non-zero exit maps to failure but keeps the reboot flag ----------

    [Fact]
    public async Task FlushDnsAsync_NonZeroExit_ReportsFailure()
    {
        var runner = RunnerReturning(1);
        using var svc = new NetworkRepairService(runner);

        var result = await svc.FlushDnsAsync();

        Assert.False(result.Success);
        Assert.False(result.NeedsReboot);
    }

    [Fact]
    public async Task ResetWinsockAsync_NonZeroExit_FailsButStillFlagsReboot()
    {
        var runner = RunnerReturning(1);
        using var svc = new NetworkRepairService(runner);

        var result = await svc.ResetWinsockAsync();

        Assert.False(result.Success);
        Assert.True(result.NeedsReboot); // reboot flag is intrinsic to the operation, not the outcome
    }

    // ---------- streamed output is collected into the result ----------

    [Fact]
    public async Task FlushDnsAsync_CollectsStreamedOutputLines()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(_ =>
              {
                  // Simulate the runner streaming a line mid-execution.
                  runner.LineReceived += Raise.Event<Action<SysManager.Models.PowerShellLine>>(
                      SysManager.Models.PowerShellLine.Output("Successfully flushed the DNS Resolver Cache."));
                  return 0;
              });
        using var svc = new NetworkRepairService(runner);

        var result = await svc.FlushDnsAsync();

        Assert.Contains("flushed", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
