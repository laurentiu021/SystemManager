// SysManager · TaskSchedulerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class TaskSchedulerServiceTests
{
    // ── SetEnabledAsync read-back: exactly-one-match guard (wildcard-injection hardening) ──

    private static PSObject TaskRow(string name, string path, string state)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("TaskName", name));
        o.Properties.Add(new PSNoteProperty("TaskPath", path));
        o.Properties.Add(new PSNoteProperty("State", state));
        o.Properties.Add(new PSNoteProperty("Author", "Tester"));
        o.Properties.Add(new PSNoteProperty("Description", ""));
        return o;
    }

    private static TaskSchedulerService ServiceReturning(params PSObject[] readBackRows)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<System.Threading.CancellationToken>())
          .Returns(new Collection<PSObject>(readBackRows.ToList()));
        return new TaskSchedulerService(ps);
    }

    [Fact]
    public async Task SetEnabledAsync_ExactlyOneMatch_CorrectState_Succeeds()
    {
        var svc = ServiceReturning(TaskRow("Backup", @"\Vendor\", "Disabled"));
        var result = await svc.SetEnabledAsync("Backup", @"\Vendor\", enabled: false);
        Assert.NotNull(result);
        Assert.Equal("Disabled", result!.State);
    }

    [Fact]
    public async Task SetEnabledAsync_OverMatch_MultipleRows_ReturnsNull()
    {
        // A wildcard over-match (e.g. a "Backup*" name) would disable several tasks and the
        // read-back would return >1 row. We can't honestly claim the selected task toggled,
        // so the result must be null (failure) — not a false success from results[0].
        var svc = ServiceReturning(
            TaskRow("BackupA", @"\Vendor\", "Disabled"),
            TaskRow("BackupB", @"\Vendor\", "Disabled"));
        var result = await svc.SetEnabledAsync("Backup", @"\Vendor\", enabled: false);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetEnabledAsync_NoMatch_ReturnsNull()
    {
        // A bracket-named task ([Pro]) that doesn't match itself, or a needs-admin no-op,
        // yields zero read-back rows — must report failure, not crash or false success.
        var svc = ServiceReturning();
        var result = await svc.SetEnabledAsync("Adobe [Pro] Updater", @"\Vendor\", enabled: false);
        Assert.Null(result);
    }

    [Fact]
    public async Task SetEnabledAsync_StateDoesNotMatchIntent_ReturnsNull()
    {
        // Disable requested but the task is still Ready (the change didn't take) → failure.
        var svc = ServiceReturning(TaskRow("Backup", @"\Vendor\", "Ready"));
        var result = await svc.SetEnabledAsync("Backup", @"\Vendor\", enabled: false);
        Assert.Null(result);
    }


    [Theory]
    [InlineData(@"\Microsoft\Windows\Application Experience\", "Microsoft Corporation")]
    [InlineData(@"\Microsoft\Windows\Customer Experience Improvement Program\", "Microsoft")]
    [InlineData(@"\Microsoft\Windows\DiskDiagnostic\", "Microsoft")]
    [InlineData(@"\Microsoft\Windows\Feedback\Siuf\", "Microsoft")]
    [InlineData(@"\Microsoft\Windows\Windows Error Reporting\", "Microsoft")]
    public void ClassifyTask_KnownTelemetryFolders_AreTelemetry(string path, string author)
        => Assert.Equal(TaskCategory.Telemetry, TaskSchedulerService.ClassifyTask(path, author));

    [Fact]
    public void ClassifyTask_AutochkProxy_IsTelemetry()
        => Assert.Equal(TaskCategory.Telemetry, TaskSchedulerService.ClassifyTask(@"\Microsoft\Windows\Autochk\Proxy", "Microsoft"));

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\", "Microsoft Corporation")]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\", "Microsoft")]
    public void ClassifyTask_OtherMicrosoftTasks_AreSystem(string path, string author)
        => Assert.Equal(TaskCategory.System, TaskSchedulerService.ClassifyTask(path, author));

    [Theory]
    [InlineData(@"\", "Valve")]
    [InlineData(@"\GoogleSystem\", "Google")]
    [InlineData(@"\", null)]
    public void ClassifyTask_NonMicrosoft_AreThirdParty(string path, string? author)
        => Assert.Equal(TaskCategory.ThirdParty, TaskSchedulerService.ClassifyTask(path, author));

    [Fact]
    public void ClassifyTask_MicrosoftAuthorButRootPath_IsSystem()
        => Assert.Equal(TaskCategory.System, TaskSchedulerService.ClassifyTask(@"\", "Microsoft Corporation"));

    [Fact]
    public void ClassifyTask_NullPath_IsThirdParty()
        => Assert.Equal(TaskCategory.ThirdParty, TaskSchedulerService.ClassifyTask(null!, null));
}
