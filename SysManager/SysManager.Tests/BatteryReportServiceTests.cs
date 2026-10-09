// SysManager · BatteryReportServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.IO;
using System.Text;
using NSubstitute;
using SysManager.Services;
using static SysManager.Tests.BatteryReportFixture;

namespace SysManager.Tests;

/// <summary>
/// <see cref="BatteryReportService"/> asks <c>powercfg</c> for the battery report, reads its history and removes the
/// file again (#1513).
/// </summary>
/// <remarks>
/// <c>powercfg</c> is never run: the runner is substituted, and it does what <c>powercfg</c> does, writing the report
/// where the arguments say, as UTF-8 with a byte order mark. Every file goes to this test's own temporary folder.
/// </remarks>
public sealed class BatteryReportServiceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 4, 6, 3, 21, 6);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public BatteryReportServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    private static readonly string TwoWeeks = Report(Entry(Start, 57000, 57000), Entry(Start.AddDays(7), 57000, 56430));

    /// <summary>The path <c>powercfg</c> was told to write to, read back out of its arguments.</summary>
    private static string OutputPath(string arguments)
    {
        const string marker = "/output \"";
        var at = arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no output path in: {arguments}");
        var start = at + marker.Length;
        return arguments[start..arguments.IndexOf('"', start)];
    }

    /// <summary>A runner that writes <paramref name="report"/> where it is told, or nothing when it is null.</summary>
    private static IPowerShellRunner PowercfgWriting(string? report, int exitCode = 0)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("powercfg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
              .Returns(call =>
              {
                  if (report is not null)
                      File.WriteAllText(OutputPath(call.ArgAt<string>(1)), report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                  return Task.FromResult(exitCode);
              });
        return runner;
    }

    [Fact]
    public async Task AReport_IsRead_AndItsFileRemoved()
    {
        var read = await new BatteryReportService(PowercfgWriting(TwoWeeks), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Read, read.Outcome);
        Assert.Equal(new[] { 100d, 99d }, read.History.Select(p => p.HealthPercent));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task TheReport_IsAskedForAsXml_InANewFileInTheGivenFolder_EachTime()
    {
        List<string> arguments = [];
        var runner = PowercfgWriting(TwoWeeks);
        var svc = new BatteryReportService(runner, _dir);

        await svc.ReadHistoryAsync();
        await svc.ReadHistoryAsync();

        foreach (var call in runner.ReceivedCalls())
            arguments.Add((string)call.GetArguments()[1]!);
        Assert.Equal(2, arguments.Count);
        Assert.All(arguments, a =>
        {
            Assert.StartsWith("/batteryreport /xml /output \"", a, StringComparison.Ordinal);
            var path = OutputPath(a);
            Assert.Equal(_dir, Path.GetDirectoryName(path));
            Assert.Matches("^SysManager-battery-[0-9a-f]{32}\\.xml$", Path.GetFileName(path));
        });
        Assert.NotEqual(OutputPath(arguments[0]), OutputPath(arguments[1]));
        await runner.Received(2).RunProcessAsync(
            "powercfg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), PowerShellRunner.OemEncoding);
    }

    [Fact]
    public async Task ADesktopsReport_IsNoHistory_NotAFailure()
    {
        var read = await new BatteryReportService(PowercfgWriting(Report(Entry(Start, 0, 0))), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.NoHistory, read.Outcome);
        Assert.Empty(read.History);
    }

    [Fact]
    public async Task ARunThatFailed_IsAFailure_EvenWithAFileWritten_AndTheFileIsRemoved()
    {
        var read = await new BatteryReportService(PowercfgWriting(TwoWeeks, exitCode: 1), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Failed, read.Outcome);
        Assert.Empty(read.History);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task ARunThatWroteNothing_IsAFailure()
    {
        var read = await new BatteryReportService(PowercfgWriting(report: null), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Failed, read.Outcome);
    }

    [Fact]
    public async Task AFileThatIsNotAReport_IsAFailure_AndIsRemoved()
    {
        var read = await new BatteryReportService(PowercfgWriting("Battery life report saved."), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Failed, read.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task AFileTooLargeToBeAReport_IsNotRead()
    {
        // A valid report, padded past the limit with a comment, so only the size check can refuse it.
        var padded = TwoWeeks.Replace("</BatteryReport>",
            "<!-- " + new string(' ', (int)BatteryReportService.MaxReportBytes) + " --></BatteryReport>", StringComparison.Ordinal);

        var read = await new BatteryReportService(PowercfgWriting(padded), _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Failed, read.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ARunThatCouldNotStart_IsAFailure(int fault)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("powercfg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
              .Returns<Task<int>>(_ => throw StartFault(fault));

        var read = await new BatteryReportService(runner, _dir).ReadHistoryAsync();

        Assert.Equal(BatteryReportOutcome.Failed, read.Outcome);
    }

    /// <summary>A run that ends only when its token is cancelled, the way a stalled <c>powercfg</c> would.</summary>
    private static Task<int> UntilCancelled(CancellationToken ct)
    {
        var stalled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => stalled.TrySetCanceled(ct));
        return stalled.Task;
    }

    /// <summary>What starting <c>powercfg</c> can throw: no disk, no access, no such file, no process.</summary>
    private static Exception StartFault(int fault) => fault switch
    {
        0 => new IOException("disk"),
        1 => new UnauthorizedAccessException("denied"),
        2 => new Win32Exception(2),
        _ => new InvalidOperationException("no process"),
    };

    [Fact]
    public async Task AStalledRun_TimesOutAfterThirtySeconds_AsAFailure()
    {
        var clock = new HeldTimers();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("powercfg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
              .Returns(call =>
              {
                  File.WriteAllText(OutputPath(call.ArgAt<string>(1)), "<BatteryReport");
                  started.SetResult();
                  return UntilCancelled(call.ArgAt<CancellationToken>(2));
              });

        var read = new BatteryReportService(runner, _dir, clock).ReadHistoryAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(read.IsCompleted);
        Assert.Equal(BatteryReportService.Timeout, clock.OnlyDue);
        Assert.Equal(TimeSpan.FromSeconds(30), BatteryReportService.Timeout);

        clock.FireAll();

        Assert.Equal(BatteryReportOutcome.Failed, (await read.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task ACancelledRead_IsCancelled_NotReportedAsAFailure_AndLeavesNoFile()
    {
        using var cts = new CancellationTokenSource();
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("powercfg.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
              .Returns(call =>
              {
                  File.WriteAllText(OutputPath(call.ArgAt<string>(1)), "<BatteryReport");
                  return UntilCancelled(call.ArgAt<CancellationToken>(2));
              });

        var read = new BatteryReportService(runner, _dir).ReadHistoryAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }
}
