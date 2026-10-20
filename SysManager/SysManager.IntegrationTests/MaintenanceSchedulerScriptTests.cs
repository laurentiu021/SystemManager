// SysManager · MaintenanceSchedulerScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
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

    private static string Run(string taskStub, string infoStub = InfoStub) =>
        taskStub + infoStub + Guard +
        "& {" + MaintenanceSchedulerService.StatusScript + "} -Folder '\\SysManager\\' -Name 'Maintenance' ; " +
        "'__SM_AFTER_STATUS__'";

    private const string ReadyTask =
        "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) [PSCustomObject]@{ State = 'Ready' } } ; ";

    private static readonly string RemoveGuard = WindowsPowerShellScript.StubsInEffect("Unregister-ScheduledTask");

    private static string Remove(string unregisterStub) =>
        unregisterStub + RemoveGuard +
        "& {" + MaintenanceSchedulerService.RemoveScript + "} -Folder '\\SysManager\\' -Name 'Maintenance' ; " +
        "'__SM_AFTER_REMOVE__'";

    // ── Registering, against stubs that say what Register-ScheduledTask was given ──
    // Dot-sourced, so the script runs in the scope it is started from, as it does in the runner's runspace.

    private static readonly string RegisterGuard = WindowsPowerShellScript.StubsInEffect(
        "New-ScheduledTaskAction", "New-ScheduledTaskTrigger", "New-ScheduledTaskSettingsSet",
        "New-ScheduledTaskPrincipal", "Register-ScheduledTask", "Get-ScheduledTask");

    private const string RegisterStubs =
        "function New-ScheduledTaskTrigger { [CmdletBinding()] param([switch]$Daily, [switch]$Weekly, $DaysOfWeek, $At) " +
        "[PSCustomObject]@{ At = $At; StartBoundary = '2026-07-01T03:00:00+03:00' } } ; " +
        "function New-ScheduledTaskSettingsSet { [CmdletBinding()] param([switch]$StartWhenAvailable, " +
        "[switch]$DontStopOnIdleEnd, $ExecutionTimeLimit, [switch]$AllowStartIfOnBatteries, " +
        "[switch]$DontStopIfGoingOnBatteries, [switch]$RunOnlyIfIdle) [PSCustomObject]@{} } ; " +
        "function New-ScheduledTaskPrincipal { [CmdletBinding()] param($UserId, $LogonType, $RunLevel) " +
        "[PSCustomObject]@{ UserId = $UserId } } ; " +
        "function Register-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath, [ValidateNotNull()]$Action, " +
        "$Trigger, $Settings, $Principal, $Description, [switch]$Force) " +
        "Write-Host \"REGISTERED args=[$($Action.Arguments)] user=[$($Principal.UserId)] start=[$($Trigger.StartBoundary)]\" ; " +
        "[PSCustomObject]@{ State = 'Ready' } } ; " +
        "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) [PSCustomObject]@{ State = 'Ready' } } ; ";

    /// <summary>A stub that makes the action as the real cmdlet does, refusing an empty command line as it does.</summary>
    private const string ActionStub =
        "function New-ScheduledTaskAction { [CmdletBinding()] param([string]$Execute, [ValidateNotNullOrEmpty()][string]$Argument) " +
        "[PSCustomObject]@{ Execute = $Execute; Arguments = $Argument } } ; ";

    private static string Register(string actionStub, string before = "")
    {
        var parameters = MaintenanceSchedulerService.RegisterParameters(
            new MaintenanceSchedule(MaintenanceAction.Cleanup, MaintenanceFrequency.Weekly, 3, 0, DayOfWeek.Sunday),
            @"C:\Apps\SysManager.exe", "S-1-5-21-1000-2000-3000-1001");
        var named = string.Join(" ", parameters.Select(p => p.Value switch
        {
            bool flag => $"-{p.Key} ${(flag ? "true" : "false")}",
            string text => $"-{p.Key} '{text.Replace("'", "''", StringComparison.Ordinal)}'",
            _ => throw new InvalidOperationException($"{p.Key} has no PowerShell literal form here"),
        }));
        return actionStub + RegisterStubs + RegisterGuard + before +
            ". {" + MaintenanceSchedulerService.RegisterScript + "} " + named + " ; '__SM_AFTER_REGISTER__'";
    }

    [Fact]
    public async Task Registering_GivesTheActionTheCommandLine_AndThePrincipalTheAccount()
    {
        // The command line was taken as $Args, which PowerShell sets itself to the arguments no parameter took, so it
        // was always empty: New-ScheduledTaskAction refused it, and no schedule saved from the tab was registered.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Register(ActionStub));

        Assert.Contains("REGISTERED args=[--cleanup --silent] user=[S-1-5-21-1000-2000-3000-1001]", output,
                        StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_REGISTER__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_StartsTheTriggerAtThePickedLocalTime_WithNoOffset()
    {
        // New-ScheduledTaskTrigger -At stores the start with today's UTC offset, as the stub does here, and Task
        // Scheduler then runs the task at that UTC moment, an hour off the picked time after the clocks change (#2868).
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Register(ActionStub));

        var start = System.Text.RegularExpressions.Regex.Match(output, @"start=\[(?<start>[^\]]*)\]").Groups["start"].Value;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T03:00:00$", start);
    }

    [Fact]
    public async Task Registering_StopsAtAStepThatFails_RatherThanRegisterAnEarlierScriptsAction()
    {
        // The runner keeps its runspace for the next script, and a refused parameter leaves the variable it was being
        // assigned to as it was, so the action an earlier script left behind was what got registered.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Register(
            "function New-ScheduledTaskAction { [CmdletBinding()] param([string]$Execute, " +
            "[ValidatePattern('^refused$')][string]$Argument) [PSCustomObject]@{ Arguments = $Argument } } ; ",
            before: "$action = [PSCustomObject]@{ Execute = 'C:\\earlier.exe'; Arguments = '--from-an-earlier-script' } ; "));

        Assert.DoesNotContain("REGISTERED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_REGISTER__", output, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task WhenTheTaskExists_ReportsTheCommandLineItRuns()
    {
        // The tab reads it to spot a schedule that purges standby memory, which fails every run because the task
        // has no administrator rights (#2593). Get-ScheduledTask carries it in the Actions array, so this is the
        // shape that has to reach the output.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Run(
            "function Get-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath) " +
            "[PSCustomObject]@{ State = 'Ready'; Actions = @([PSCustomObject]@{ Arguments = '--purge-standby --silent' }) } } ; "));

        Assert.Contains("--purge-standby --silent", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_STATUS__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheRunInformationCannotBeRead_StopsTheScript()
    {
        // It was read with -ErrorAction SilentlyContinue, so the tab showed "Not run yet" for a task that had run.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Run(ReadyTask,
            "function Get-ScheduledTaskInfo { [CmdletBinding()] param($TaskName, $TaskPath) " +
            "Write-Error -Message 'The Task Scheduler service did not answer.' -Category ResourceUnavailable } ; "));

        Assert.DoesNotContain("__SM_AFTER_STATUS__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_WhenWindowsRefuses_StopsTheScript()
    {
        // With -ErrorAction SilentlyContinue the refusal threw nothing, and the log the tab points to stayed empty.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Remove(
            "function Unregister-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath, [switch]$Confirm) " +
            "Write-Error -Message 'Access is denied.' -Category PermissionDenied } ; "));

        Assert.DoesNotContain("__SM_AFTER_REMOVE__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_ATaskThatIsAlreadyGone_CarriesOn()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Remove(
            "function Unregister-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath, [switch]$Confirm) " +
            "Write-Error -Message 'No MSFT_ScheduledTask objects found.' -Category ObjectNotFound } ; "));

        Assert.Contains("__SM_AFTER_REMOVE__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_ATask_CarriesOn()
    {
        // The positive control, so a script that stopped on everything could not pass the case above's opposite.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(Remove(
            "function Unregister-ScheduledTask { [CmdletBinding()] param($TaskName, $TaskPath, [switch]$Confirm) } ; "));

        Assert.Contains("__SM_AFTER_REMOVE__", output, StringComparison.Ordinal);
    }
}
