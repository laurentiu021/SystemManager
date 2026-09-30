// SysManager · FileLockKillTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// File Lock Detector's End process against a real locker: a child this test starts, holding a file this test made.
/// </summary>
/// <remarks>
/// The unit suite ends nothing; it drives the view model and the service through substitutes. Only a real locker
/// shows that the start time Restart Manager reports and the one <see cref="System.Diagnostics.Process.StartTime"/>
/// reads agree to the tick, and the check before ending a process compares exactly those two (#2514).
/// </remarks>
public sealed class FileLockKillTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public FileLockKillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerFileLockKillTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "held.txt");
        File.WriteAllText(_file, "x");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    [Fact]
    public async Task KillProcess_TheLockerAsListed_EndsIt()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = await RunningChild.StartAsync(_file, bounded.Token);
        var locker = ListedLocker(child);

        var outcome = new FileLockService().KillProcess(locker.ProcessId, locker.StartTime);

        Assert.Equal(ProcessManagerService.KillOutcome.Ended, outcome);
        await child.Process.WaitForExitAsync(bounded.Token);
    }

    /// <summary>
    /// A locker whose start time is not the one listed is left alone: its ID has been given to another process.
    /// </summary>
    /// <remarks>
    /// Driven with a start time one second off, which is what a reused ID looks like from the list's side.
    /// </remarks>
    [Fact]
    public async Task KillProcess_ALockerThatStartedAtAnotherTime_IsLeftRunning()
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = await RunningChild.StartAsync(_file, bounded.Token);
        var locker = ListedLocker(child);

        var outcome = new FileLockService().KillProcess(locker.ProcessId, locker.StartTime!.Value.AddSeconds(-1));

        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, outcome);
        Assert.True(await child.AnswersAsync(bounded.Token), "a locker whose start time did not match was ended anyway");
    }

    /// <summary>The child's row in a check of the held file, carrying the start time Windows gives the child.</summary>
    private FileLocker ListedLocker(RunningChild child)
    {
        var scan = new FileLockService().FindLockers(_file);

        Assert.NotNull(scan);
        var locker = Assert.Single(scan.Lockers, l => l.ProcessId == child.Id);
        Assert.Equal(child.StartTime, locker.StartTime);
        return locker;
    }
}
