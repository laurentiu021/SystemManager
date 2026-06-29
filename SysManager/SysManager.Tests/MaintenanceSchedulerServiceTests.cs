// SysManager · MaintenanceSchedulerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class MaintenanceSchedulerServiceTests
{
    private static PSObject StateRow(string state)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("State", state));
        return o;
    }

    private static PSObject StatusRow(string state, object? lastResult)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("State", state));
        o.Properties.Add(new PSNoteProperty("LastRunTime", new DateTime(2026, 6, 29, 3, 0, 0)));
        o.Properties.Add(new PSNoteProperty("NextRunTime", new DateTime(2026, 6, 30, 3, 0, 0)));
        o.Properties.Add(new PSNoteProperty("LastTaskResult", lastResult));
        return o;
    }

    private static (MaintenanceSchedulerService svc, IPowerShellRunner ps) NewService(params PSObject[] rows)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>(rows.ToList()));
        return (new MaintenanceSchedulerService(ps), ps);
    }

    // ── RegisterAsync: only whitelisted args reach PowerShell (security invariant) ──

    [Fact]
    public async Task RegisterAsync_PassesWhitelistedArgs_NeverFreeText()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Weekly, 3, 30, DayOfWeek.Sunday);

        var ok = await svc.RegisterAsync(schedule, exePath: @"C:\Apps\SysManager.exe");

        Assert.True(ok);
        await ps.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Is<IDictionary<string, object?>?>(p =>
                p != null &&
                (string)p["Exe"]! == @"C:\Apps\SysManager.exe" &&
                (string)p["Args"]! == "--cleanup --silent" &&     // whitelisted, never arbitrary
                (string)p["Folder"]! == MaintenanceSchedulerService.TaskFolder &&
                (string)p["Name"]! == MaintenanceSchedulerService.TaskName &&
                (bool)p["Daily"]! == false &&
                (string)p["At"]! == "03:30" &&
                (string)p["DayOfWeek"]! == "Sunday"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_DailySchedule_SetsDailyTrue()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.TrimRam, MaintenanceFrequency.Daily, 9, 0);
        await svc.RegisterAsync(schedule, exePath: @"C:\x.exe");
        await ps.Received(1).RunAsync(Arg.Any<string>(),
            Arg.Is<IDictionary<string, object?>?>(p => (bool)p!["Daily"]! && (string)p["Args"]! == "--trim-ram --silent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_NullExePath_ReturnsFalse_AndDoesNotRun()
    {
        var (svc, ps) = NewService(StateRow("Ready"));
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        var ok = await svc.RegisterAsync(schedule, exePath: "");
        Assert.False(ok);
        await ps.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_EmptyResults_ReturnsFalse()
    {
        var (svc, _) = NewService(); // no rows back
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        Assert.False(await svc.RegisterAsync(schedule, exePath: @"C:\x.exe"));
    }

    [Fact]
    public async Task RegisterAsync_PowerShellThrows_ReturnsFalse()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new RuntimeException("denied"));
        var svc = new MaintenanceSchedulerService(ps);
        var schedule = new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Daily, 1, 0);
        Assert.False(await svc.RegisterAsync(schedule, exePath: @"C:\x.exe"));
    }

    // ── GetStatusAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NoRows_ReturnsExistsFalse()
    {
        var (svc, _) = NewService();
        var status = await svc.GetStatusAsync();
        Assert.False(status.Exists);
    }

    [Fact]
    public async Task GetStatusAsync_MapsStateRunsAndResult()
    {
        var (svc, _) = NewService(StatusRow("Ready", 0));
        var status = await svc.GetStatusAsync();
        Assert.True(status.Exists);
        Assert.Equal("Ready", status.State);
        Assert.Equal(new DateTime(2026, 6, 29, 3, 0, 0), status.LastRun);
        Assert.Equal(new DateTime(2026, 6, 30, 3, 0, 0), status.NextRun);
        Assert.Equal("Last run succeeded", status.LastResultDescription);
    }

    [Fact]
    public async Task GetStatusAsync_PowerShellThrows_ReturnsExistsFalse()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new RuntimeException("boom"));
        var svc = new MaintenanceSchedulerService(ps);
        Assert.False((await svc.GetStatusAsync()).Exists);
    }
}
