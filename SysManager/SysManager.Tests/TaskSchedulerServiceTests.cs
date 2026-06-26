// SysManager · TaskSchedulerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class TaskSchedulerServiceTests
{
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
