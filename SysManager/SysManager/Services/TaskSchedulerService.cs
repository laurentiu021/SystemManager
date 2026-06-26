// SysManager · TaskSchedulerService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Browses Windows scheduled tasks and enables/disables them through the Defender-free
/// <c>ScheduledTasks</c> PowerShell module (<c>Get-ScheduledTask</c> /
/// <c>Get-ScheduledTaskInfo</c> / <c>Enable</c>/<c>Disable-ScheduledTask</c>) via the
/// shared <see cref="IPowerShellRunner"/> seam.
///
/// Disabling a task is fully reversible and non-destructive (it sets State=Disabled, it
/// does NOT unregister the task); this service never deletes a task. Modifying a system
/// task needs admin — without it the cmdlet fails on the error stream rather than
/// throwing, so every toggle is verified by reading the task's state back.
/// </summary>
public sealed class TaskSchedulerService
{
    private readonly IPowerShellRunner _ps;

    public TaskSchedulerService(IPowerShellRunner ps) => _ps = ps;

    private const string ListScript = """
        Get-ScheduledTask | ForEach-Object {
            [PSCustomObject]@{
                TaskName    = $_.TaskName
                TaskPath    = $_.TaskPath
                State       = [string]$_.State
                Author      = $_.Author
                Description = $_.Description
            }
        }
        """;

    private const string InfoScript = """
        param([string]$Name, [string]$Path)
        Get-ScheduledTaskInfo -TaskName $Name -TaskPath $Path | Select-Object LastRunTime, NextRunTime
        """;

    private const string SetEnabledScript = """
        param([string]$Name, [string]$Path, [bool]$Enabled)
        if ($Enabled) { Enable-ScheduledTask -TaskName $Name -TaskPath $Path -ErrorAction SilentlyContinue | Out-Null }
        else { Disable-ScheduledTask -TaskName $Name -TaskPath $Path -ErrorAction SilentlyContinue | Out-Null }
        Get-ScheduledTask -TaskName $Name -TaskPath $Path |
            Select-Object TaskName, TaskPath, @{ n='State'; e={ [string]$_.State } }, Author, Description
        """;

    /// <summary>List all scheduled tasks (run info loaded lazily on selection).</summary>
    public async Task<IReadOnlyList<ScheduledTaskInfo>> ListTasksAsync(CancellationToken ct = default)
    {
        try
        {
            Collection<PSObject> results = await _ps.RunAsync(ListScript, cancellationToken: ct).ConfigureAwait(false);
            var list = new List<ScheduledTaskInfo>(results.Count);
            foreach (var item in results)
            {
                string? name = Str(item, "TaskName");
                string? path = Str(item, "TaskPath");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;

                string? author = Str(item, "Author");
                list.Add(new ScheduledTaskInfo(
                    name, path, Str(item, "State") ?? "Unknown", author,
                    Str(item, "Description"), ClassifyTask(path, author), null, null));
            }
            return list.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("List scheduled tasks failed: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>Fetch last/next run for one task (separate per-task query).</summary>
    public async Task<ScheduledTaskInfo> LoadRunInfoAsync(ScheduledTaskInfo task, CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Name"] = task.Name, ["Path"] = task.Path };
            Collection<PSObject> results = await _ps.RunAsync(InfoScript, parameters, ct).ConfigureAwait(false);
            var row = results.Count > 0 ? results[0] : null;
            return task with { LastRun = Date(row, "LastRunTime"), NextRun = Date(row, "NextRunTime") };
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Read task run info failed: {Error}", ex.Message);
            return task;
        }
    }

    /// <summary>
    /// Enable or disable a task, then re-read its state to verify. Returns the observed
    /// task on success, or null if the change didn't take effect (e.g. needs admin).
    /// </summary>
    public async Task<ScheduledTaskInfo?> SetEnabledAsync(string name, string path, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Name"] = name, ["Path"] = path, ["Enabled"] = enabled };
            Collection<PSObject> results = await _ps.RunAsync(SetEnabledScript, parameters, ct).ConfigureAwait(false);
            var row = results.Count > 0 ? results[0] : null;
            if (row is null) return null;

            string observed = Str(row, "State") ?? "Unknown";
            bool observedDisabled = string.Equals(observed, "Disabled", StringComparison.OrdinalIgnoreCase);
            // Intent check: enable => not disabled; disable => disabled.
            if (enabled == observedDisabled) return null;

            string? author = Str(row, "Author");
            return new ScheduledTaskInfo(
                Str(row, "TaskName") ?? name, Str(row, "TaskPath") ?? path, observed,
                author, Str(row, "Description"), ClassifyTask(path, author), null, null);
        }
        catch (System.Management.Automation.RuntimeException ex)
        {
            Log.Debug("Set task enabled failed: {Error}", ex.Message);
            return null;
        }
    }

    // Well-known telemetry/CEIP folders targeted by reputable debloat tools — small and
    // conservative, prefix-matched on TaskPath.
    private static readonly FrozenSet<string> TelemetryFolders = new[]
    {
        @"\Microsoft\Windows\Application Experience\",
        @"\Microsoft\Windows\Customer Experience Improvement Program\",
        @"\Microsoft\Windows\DiskDiagnostic\",
        @"\Microsoft\Windows\Feedback\",
        @"\Microsoft\Windows\Windows Error Reporting\",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TelemetryFullPaths = new[]
    {
        @"\Microsoft\Windows\Autochk\Proxy",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pure classification: telemetry folders first (safe-to-disable), then any other
    /// Microsoft/Windows task as System (caution), else Third-party.
    /// </summary>
    public static TaskCategory ClassifyTask(string path, string? author)
    {
        path ??= "";
        foreach (var folder in TelemetryFolders)
            if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return TaskCategory.Telemetry;

        if (TelemetryFullPaths.Contains(path.TrimEnd('\\')))
            return TaskCategory.Telemetry;

        bool microsoftPath = path.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);
        bool microsoftAuthor = author is not null && author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        return microsoftPath || microsoftAuthor ? TaskCategory.System : TaskCategory.ThirdParty;
    }

    private static string? Str(PSObject? obj, string property) => obj?.Properties[property]?.Value?.ToString();

    private static DateTime? Date(PSObject? obj, string property) => obj?.Properties[property]?.Value switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };
}
