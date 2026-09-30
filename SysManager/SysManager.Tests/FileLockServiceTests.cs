// SysManager · FileLockServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// <see cref="FileLockService.FindLockers"/> against the real Restart Manager, over temp files this test process
/// creates and holds itself (#2502).
/// </summary>
/// <remarks>
/// Read-only, like the existing view-model tests that use the real service: Restart Manager is asked who is using
/// a file and nothing is ended. The one "locker" is this test process, holding a file it opened.
/// <para>Before the fix, a folder was handed to Restart Manager as it was. <c>RmGetList</c> refuses a folder with
/// <c>ERROR_ACCESS_DENIED</c>, even from an elevated process and with a file inside held open, and the refusal
/// came back as an empty list. A path that does not exist was accepted and reported as unused.</para>
/// </remarks>
public sealed class FileLockServiceTests : IDisposable
{
    private readonly string _dir;

    public FileLockServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerFileLockTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public void AFileHeldOpen_IsReportedWithTheProcessHoldingIt()
    {
        var file = NewFile("held.txt");
        using var held = Hold(file);

        var scan = new FileLockService().FindLockers(file);

        Assert.NotNull(scan);
        Assert.False(scan.IsFolder);
        Assert.Contains(scan.Lockers, l => l.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void AFileHeldOpen_IsReportedWithTheStartTimeWindowsGivesItsProcess()
    {
        // End process checks the locker's start time against the one Process.StartTime reads, so the two must be the
        // same instant to the tick. Restart Manager documents its time as GetProcessTimes' creation time; this pins
        // it (#2514). Were they to differ, every End process would report the locker as already closed.
        var file = NewFile("held.txt");
        using var held = Hold(file);
        using var self = Process.GetCurrentProcess();

        var scan = new FileLockService().FindLockers(file);

        Assert.NotNull(scan);
        var locker = Assert.Single(scan.Lockers, l => l.ProcessId == Environment.ProcessId);
        Assert.Equal(self.StartTime, locker.StartTime);
    }

    [Fact]
    public void AFolderWithAFileHeldOpen_IsReportedWithTheProcessHoldingThatFile()
    {
        NewFile("free.txt");
        var file = NewFile("held.txt");
        using var held = Hold(file);

        var scan = new FileLockService().FindLockers(_dir);

        Assert.NotNull(scan);
        Assert.True(scan.IsFolder);
        Assert.Equal(2, scan.FilesChecked);
        Assert.False(scan.CheckedOnlyPart);
        Assert.Contains(scan.Lockers, l => l.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void AFileInASubfolder_IsCheckedToo()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "nested")).FullName;
        var file = Path.Combine(sub, "deep.txt");
        File.WriteAllText(file, "x");
        using var held = Hold(file);

        var scan = new FileLockService().FindLockers(_dir);

        Assert.NotNull(scan);
        Assert.Contains(scan.Lockers, l => l.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void AnEmptyFolder_ChecksNothing()
    {
        var scan = new FileLockService().FindLockers(_dir);

        Assert.NotNull(scan);
        Assert.True(scan.IsFolder);
        Assert.Equal(0, scan.FilesChecked);
        Assert.Empty(scan.Lockers);
    }

    [Fact]
    public void AFolderTooLargeToCheckWhole_ChecksTheFirstAndSaysSo()
    {
        for (var i = 0; i <= FileLockService.MaxFolderFiles; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");

        var scan = new FileLockService().FindLockers(_dir);

        Assert.NotNull(scan);
        Assert.Equal(FileLockService.MaxFolderFiles, scan.FilesChecked);
        Assert.True(scan.CheckedOnlyPart);
    }

    [Fact]
    public void ACheckRestartManagerRefuses_IsNull_NotAnEmptyList()
    {
        // Restart Manager refuses a folder, which makes it the one failure a test can produce on demand. FindLockers
        // no longer hands it one, so this asks Restart Manager directly. An empty list here is what the tab showed
        // as "no process is using it".
        Assert.Null(FileLockService.QueryRestartManager([_dir]));
    }

    [Fact]
    public void APathThatDoesNotExist_IsRefused_NotReportedAsUnused()
    {
        var missing = Path.Combine(_dir, "no-such-file.txt");

        Assert.Throws<FileNotFoundException>(() => new FileLockService().FindLockers(missing));
    }

    [Fact]
    public void AnEmptyPath_IsRefused()
        => Assert.Throws<ArgumentException>(() => new FileLockService().FindLockers("  "));

    // ── KillProcess (#2514) ──
    // Through the seam: the unit suite ends no process. The integration suite ends a real locker it started.

    [Fact]
    public void KillProcess_PassesTheListedStartTime_AndReturnsWhatHappened()
    {
        var started = new DateTime(2026, 9, 30, 8, 15, 42, DateTimeKind.Local);
        var calls = new List<(int Pid, DateTime StartTime)>();
        var service = new FileLockService((pid, startTime) =>
        {
            calls.Add((pid, startTime));
            return ProcessManagerService.KillOutcome.NotRunning;
        });

        var outcome = service.KillProcess(4242, started);

        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, outcome);
        Assert.Equal([(4242, started)], calls);
    }

    [Fact]
    public void KillProcess_ForALockerWithNoStartTime_SkipsTheCheck()
    {
        // default is how ProcessManagerService.KillProcess is told there is no start time to compare.
        var calls = new List<DateTime>();
        var service = new FileLockService((_, startTime) =>
        {
            calls.Add(startTime);
            return ProcessManagerService.KillOutcome.Ended;
        });

        service.KillProcess(4242, null);

        Assert.Equal([default(DateTime)], calls);
    }

    [Fact]
    public void KillProcess_AsTheAppBuildsTheService_EndsThroughProcessManager()
    {
        // The wiring the tests above cannot run. Read from the delegate, never called: calling it ends a process.
        // Without this, the public constructor could hand the service any kill at all with every test still green.
        var services = new ServiceCollection();
        services.ConfigureServices();
        using var provider = services.BuildServiceProvider();
        var service = Assert.IsType<FileLockService>(provider.GetRequiredService<IFileLockService>());

        var kill = Assert.IsType<Func<int, DateTime, ProcessManagerService.KillOutcome>>(typeof(FileLockService)
            .GetField("_killProcess", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service));

        Assert.Equal(typeof(ProcessManagerService).GetMethod(nameof(ProcessManagerService.KillProcess)), kill.Method);
    }

    [Fact]
    public void KillProcess_ForAnIdNoProcessHas_ReportsNotRunning()
    {
        // The real call, safely: int.MaxValue names no process, and Windows ignores the low two bits of an ID, so
        // a smaller made-up ID could name a real one (see ProcessManagerServiceTests).
        Assert.Equal(ProcessManagerService.KillOutcome.NotRunning, new FileLockService().KillProcess(int.MaxValue, null));
    }
}
