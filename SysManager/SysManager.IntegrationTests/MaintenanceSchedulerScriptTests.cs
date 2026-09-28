// SysManager · MaintenanceSchedulerScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>MaintenanceSchedulerService.StatusScript</c> run in real Windows PowerShell, with <c>Get-ScheduledTask</c>
/// and <c>Get-ScheduledTaskInfo</c> shadowed by functions so no real task is read or changed.
/// </summary>
/// <remarks>
/// "There is no task" must be only what Windows says it is. <c>Get-ScheduledTask</c> reports a missing task as
/// an <c>ObjectNotFound</c> error, and the script returns nothing for that. Any other error has to stop the
/// script, so the runner throws and the tab says it could not read the schedule instead of that nothing is
/// scheduled (#2487). The script opens with a <c>param</c> block, so it is invoked as a script block after
/// the stubs are defined.
/// </remarks>
public class MaintenanceSchedulerScriptTests
{
    private static readonly string Guard = WindowsPowerShellScript.StubsInEffect("Get-ScheduledTask", "Get-ScheduledTaskInfo");

    private const string InfoStub =
        "function Get-ScheduledTaskInfo { [CmdletBinding()] param($TaskName, $TaskPath) " +
        "[PSCustomObject]@{ LastRunTime = $null; NextRunTime = $null; LastTaskResult = 0; NumberOfMissedRuns = 0 } } ; ";

    private static string Run(string taskStub) =>
        taskStub + InfoStub + Guard +
        "& {" + MaintenanceSchedulerService.StatusScript + "} -Folder '\\SysManager\\' -Name 'Maintenance' ; " +
        "'__SM_AFTER_STATUS__'";

    [Fact]
    public async Task WhenWindowsSaysThereIsNoTask_ReturnsNothingAndCarriesOn()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Run(
            "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) " +
            "Write-Error -Message 'No MSFT_ScheduledTask objects found.' -Category ObjectNotFound } ; "));

        Assert.Contains("__SM_AFTER_STATUS__", output, StringComparison.Ordinal);
        Assert.DoesNotContain("State", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheReadFails_StopsTheScript()
    {
        // It used to read the task with -ErrorAction SilentlyContinue, so this looked exactly like an absent task.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Run(
            "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) " +
            "Write-Error -Message 'The Task Scheduler service did not answer.' -Category ResourceUnavailable } ; "));

        Assert.DoesNotContain("__SM_AFTER_STATUS__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheTaskExists_ReportsIt()
    {
        // The positive control, so a script that returned nothing for everything could not pass the first case.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Run(
            "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) " +
            "[PSCustomObject]@{ State = 'Ready' } } ; "));

        Assert.Contains("Ready", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_STATUS__", output, StringComparison.Ordinal);
    }
}
