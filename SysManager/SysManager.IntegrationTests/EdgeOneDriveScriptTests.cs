// SysManager · EdgeOneDriveScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>EdgeOneDriveService</c>'s Edge update-task scripts run in real Windows PowerShell, with
/// <c>Get-ScheduledTask</c>, <c>Disable-ScheduledTask</c> and <c>Enable-ScheduledTask</c> shadowed by functions so
/// no real task is read or changed.
/// </summary>
/// <remarks>
/// Current Edge Update adds an ID to each task's name, and the exact names the scripts passed found no task, so
/// Disable and Restore changed nothing while reporting success (#2810). The stubs answer as the cmdlets do: a
/// <c>-TaskName</c> with a wildcard matches by pattern, one without has to match the whole name and is an error
/// when nothing has it, and a task that cannot be changed is a non-terminating error, as "Access is denied." is.
/// </remarks>
public class EdgeOneDriveScriptTests
{
    private static readonly string Guard = WindowsPowerShellScript.StubsInEffect(
        "Get-ScheduledTask", "Disable-ScheduledTask", "Enable-ScheduledTask");

    // Edge's two tasks as Edge Update names them now.
    private const string Core = "MicrosoftEdgeUpdateTaskMachineCore{7A6CBB87-B577-4815-9993-CDA7F95BC192}";
    private const string Ua = "MicrosoftEdgeUpdateTaskMachineUA{A505C5E6-150B-405A-BB86-7C8CCE667859}";

    // A task by the same prefix outside the root folder, which neither script may touch or count.
    private const string Elsewhere = "MicrosoftEdgeUpdateTaskMachineCore{00000000-0000-0000-0000-000000000000}";

    private const string FindStub =
        "function Get-ScheduledTask { [CmdletBinding()] param([string[]]$TaskName, $TaskPath) " +
        "foreach ($p in $TaskName) { $hit = @($SmTasks | Where-Object { $_.TaskName -like $p }) ; " +
        "if ($hit.Count -eq 0 -and -not [Management.Automation.WildcardPattern]::ContainsWildcardCharacters($p)) { " +
        "Write-Error -Message \"No MSFT_ScheduledTask objects found with property 'TaskName' equal to '$p'.\" " +
        "-Category ObjectNotFound } ; $hit } } ; ";

    /// <summary>A stub for <paramref name="verb"/>, printing <paramref name="word"/> and the task changed.</summary>
    private static string ChangeStub(string verb, string word) =>
        $"function {verb} {{ [CmdletBinding()] param($InputObject, $TaskName, $TaskPath) " +
        "$task = if ($InputObject) { $InputObject } " +
        "else { @($SmTasks | Where-Object { $_.TaskName -eq $TaskName })[0] } ; " +
        "if (-not $task) { " +
        "Write-Error -Message 'The system cannot find the file specified.' -Category ObjectNotFound ; return } ; " +
        "if ($SmRefuse -and $task.TaskName -like $SmRefuse) { " +
        "Write-Error -Message 'Access is denied.' -Category PermissionDenied ; return } ; " +
        $"Write-Host \"{word} $($task.TaskName)\" }} ; ";

    private static readonly string Stubs =
        FindStub + ChangeStub("Disable-ScheduledTask", "DISABLED") + ChangeStub("Enable-ScheduledTask", "ENABLED");

    private static string Tasks(params (string Name, string Path, string State)[] tasks) =>
        "$SmTasks = @(" + string.Join(", ", tasks.Select(t =>
            $"[PSCustomObject]@{{ TaskName = '{t.Name}'; TaskPath = '{t.Path}'; State = '{t.State}' }}")) + ") ; ";

    /// <summary>Runs the enable or disable script over <paramref name="tasks"/>, refusing those matching
    /// <paramref name="refuse"/>, and prints what it reported.</summary>
    private static async Task<string> SetAsync(bool enabled, string tasks, string refuse = "")
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            tasks + $"$SmRefuse = '{refuse}' ; " + Stubs + Guard +
            "$r = & {" + EdgeOneDriveService.BuildSetEdgeTasksScript(enabled) + "} ; " +
            "\"FOUND=$($r.Found) REFUSED=$($r.Refused)\" ; '__SM_AFTER__'");
        return output;
    }

    [Fact]
    public async Task Disabling_FindsEdgesTasksByTheirPrefix_InTheRootFolderOnly()
    {
        var output = await SetAsync(enabled: false,
            Tasks((Core, "\\", "Ready"), (Ua, "\\", "Ready"), (Elsewhere, "\\Contoso\\", "Ready")));

        Assert.Contains($"DISABLED {Core}", output, StringComparison.Ordinal);
        Assert.Contains($"DISABLED {Ua}", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Elsewhere, output, StringComparison.Ordinal);
        Assert.Contains("FOUND=2 REFUSED=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_EnablesTheTasksThatAreDisabled()
    {
        var output = await SetAsync(enabled: true, Tasks((Core, "\\", "Disabled"), (Ua, "\\", "Disabled")));

        Assert.Contains($"ENABLED {Core}", output, StringComparison.Ordinal);
        Assert.Contains($"ENABLED {Ua}", output, StringComparison.Ordinal);
        Assert.Contains("FOUND=2 REFUSED=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_LeavesATaskThatIsAlreadyAsAsked()
    {
        var output = await SetAsync(enabled: false, Tasks((Core, "\\", "Disabled"), (Ua, "\\", "Ready")));

        Assert.DoesNotContain($"DISABLED {Core}", output, StringComparison.Ordinal);
        Assert.Contains($"DISABLED {Ua}", output, StringComparison.Ordinal);
        Assert.Contains("FOUND=2 REFUSED=False", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenATaskRefuses_TheOnesAlreadyChangedArePutBack()
    {
        // A change that fails leaves every task as it found it, so the tab's "could not be de-integrated" is the whole
        // story.
        var output = await SetAsync(enabled: false, Tasks((Core, "\\", "Ready"), (Ua, "\\", "Ready")), refuse: "*UA*");

        var disabled = output.IndexOf($"DISABLED {Core}", StringComparison.Ordinal);
        var putBack = output.IndexOf($"ENABLED {Core}", StringComparison.Ordinal);
        Assert.True(disabled >= 0 && putBack > disabled, $"Core was not disabled and then put back:\n{output}");
        Assert.DoesNotContain($"DISABLED {Ua}", output, StringComparison.Ordinal);
        Assert.Contains("REFUSED=True", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoEdgeTask_FindsNone_WithoutStopping()
    {
        // None is an answer, not an error: the service then reports the change as not done.
        var output = await SetAsync(enabled: false, Tasks(("OneDrive Standalone Update Task", "\\", "Ready")));

        Assert.DoesNotContain("DISABLED", output, StringComparison.Ordinal);
        Assert.Contains("FOUND=0 REFUSED=False", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheTasksCannotBeRead_StopsTheScript()
    {
        // Stopping is what makes the runner throw, so the change is reported as failed, not as finding no task.
        var (_, output, error) = await WindowsPowerShellScript.RunAsync(
            "function Get-ScheduledTask { [CmdletBinding()] param([string[]]$TaskName, $TaskPath) " +
            "Write-Error -Message 'The Task Scheduler service is not available.' -Category ResourceUnavailable } ; " +
            ChangeStub("Disable-ScheduledTask", "DISABLED") + ChangeStub("Enable-ScheduledTask", "ENABLED") + Guard +
            "$r = & {" + EdgeOneDriveService.BuildSetEdgeTasksScript(enabled: false) + "} ; '__SM_AFTER__'");

        Assert.DoesNotContain("__SM_AFTER__", output, StringComparison.Ordinal);
        // And it was the read that stopped it, not a script that never ran.
        Assert.Contains("ResourceUnavailable", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ready", "Disabled", "True")]
    [InlineData("Disabled", "Disabled", "False")]
    public async Task TheStatusQuery_SaysWhetherAnEdgeTaskIsEnabled(string core, string ua, string expected)
    {
        // The task elsewhere is enabled, and does not count.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            Tasks((Core, "\\", core), (Ua, "\\", ua), (Elsewhere, "\\Contoso\\", "Ready")) + Stubs + Guard +
            "$r = & {" + EdgeOneDriveService.BuildQueryEdgeTasksScript() + "} ; \"ANYENABLED=$($r.AnyEnabled)\"");

        Assert.Contains($"ANYENABLED={expected}", output, StringComparison.Ordinal);
    }
}
