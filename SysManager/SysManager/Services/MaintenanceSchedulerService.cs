// SysManager · MaintenanceSchedulerService — registers a recurring maintenance task
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Creates, reads, and removes a single SysManager-owned Windows scheduled task that runs
/// the app headless (via its CLI) on a recurring schedule. Uses the <c>ScheduledTasks</c>
/// PowerShell module through the shared <see cref="IPowerShellRunner"/> seam.
///
/// Safety: this service only ever touches the one task at <see cref="TaskFolder"/> +
/// <see cref="TaskName"/> — it never enumerates or modifies other tasks. The command it
/// registers is built from a whitelisted CLI argument string (no free-form user input),
/// pointed at the running executable's own path, and registered in the current user's
/// context (no admin required, runs only when that user is logged on).
/// </summary>
public sealed class MaintenanceSchedulerService
{
    public const string TaskFolder = @"\SysManager\";
    public const string TaskName = "Scheduled Maintenance";

    private readonly IPowerShellRunner _ps;

    public MaintenanceSchedulerService(IPowerShellRunner ps) => _ps = ps;

    /// <summary>Registers (or replaces) the maintenance task from the given schedule.
    /// Returns true on success. The executable path defaults to the running process.</summary>
    public async Task<bool> RegisterAsync(MaintenanceSchedule schedule, string? exePath = null, CancellationToken ct = default)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warning("Maintenance schedule not registered — could not resolve executable path.");
            return false;
        }

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["Exe"] = exePath,
                ["Args"] = schedule.CliArguments,
                ["Folder"] = TaskFolder,
                ["Name"] = TaskName,
                ["Daily"] = schedule.Frequency == MaintenanceFrequency.Daily,
                ["At"] = $"{schedule.Hour:D2}:{schedule.Minute:D2}",
                ["DayOfWeek"] = schedule.DayOfWeek.ToString(),
            };
            Collection<PSObject> results = await _ps.RunAsync(RegisterScript, parameters, ct).ConfigureAwait(false);
            // The script emits the registered task's state; one row back == success.
            return results.Count == 1 && Str(results[0], "State") is not null;
        }
        catch (RuntimeException ex)
        {
            Log.Warning("Maintenance schedule registration failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>Reads the current state of the maintenance task (Exists=false if not registered).</summary>
    public async Task<MaintenanceStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = TaskName };
            Collection<PSObject> results = await _ps.RunAsync(StatusScript, parameters, ct).ConfigureAwait(false);
            if (results.Count == 0) return new MaintenanceStatus(false, null, null, null, null);

            var row = results[0];
            return new MaintenanceStatus(
                Exists: true,
                State: Str(row, "State"),
                LastRun: Date(row, "LastRunTime"),
                NextRun: Date(row, "NextRunTime"),
                LastResultDescription: DescribeResult(row));
        }
        catch (RuntimeException ex)
        {
            Log.Debug("Maintenance status read failed: {Error}", ex.Message);
            return new MaintenanceStatus(false, null, null, null, null);
        }
    }

    /// <summary>Removes the maintenance task. Returns true if it no longer exists afterwards.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = TaskName };
            await _ps.RunAsync(RemoveScript, parameters, ct).ConfigureAwait(false);
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            return !status.Exists;
        }
        catch (RuntimeException ex)
        {
            Log.Warning("Maintenance schedule removal failed: {Error}", ex.Message);
            return false;
        }
    }

    // ── PowerShell scripts (parameterised; no string interpolation of user input) ──

    // Register-ScheduledTask with -Force replaces any existing task of the same name, so
    // re-registering just updates the schedule. The action runs the app's own exe with the
    // whitelisted CLI args; the trigger is daily or weekly at the chosen time. The task is
    // pinned to the current interactive user at the LIMITED run level via an explicit
    // principal — so it needs no admin to register and runs only when that user is logged on,
    // never with elevation. (Previously this relied on the cmdlet's default principal; the
    // explicit principal makes the security posture deliberate rather than implicit.)
    private const string RegisterScript = """
        param([string]$Exe, [string]$Args, [string]$Folder, [string]$Name,
              [bool]$Daily, [string]$At, [string]$DayOfWeek)
        $action = New-ScheduledTaskAction -Execute $Exe -Argument $Args
        if ($Daily) {
            $trigger = New-ScheduledTaskTrigger -Daily -At $At
        } else {
            $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DayOfWeek -At $At
        }
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $Name -TaskPath $Folder -Action $action -Trigger $trigger `
            -Settings $settings -Principal $principal -Description "SysManager automated maintenance." `
            -Force -ErrorAction Stop |
            Select-Object @{ n='State'; e={ [string]$_.State } } | Out-Null
        Get-ScheduledTask -TaskName $Name -TaskPath $Folder -ErrorAction Stop |
            Select-Object @{ n='State'; e={ [string]$_.State } }
        """;

    private const string StatusScript = """
        param([string]$Folder, [string]$Name)
        $task = Get-ScheduledTask -TaskName $Name -TaskPath $Folder -ErrorAction SilentlyContinue
        if ($null -eq $task) { return }
        $info = Get-ScheduledTaskInfo -TaskName $Name -TaskPath $Folder -ErrorAction SilentlyContinue
        [PSCustomObject]@{
            State          = [string]$task.State
            LastRunTime    = $info.LastRunTime
            NextRunTime    = $info.NextRunTime
            LastTaskResult = $info.LastTaskResult
        }
        """;

    private const string RemoveScript = """
        param([string]$Folder, [string]$Name)
        Unregister-ScheduledTask -TaskName $Name -TaskPath $Folder -Confirm:$false -ErrorAction SilentlyContinue
        """;

    /// <summary>Plain-language description of the last task result code. Pure/testable.</summary>
    public static string DescribeResultCode(int? code) => code switch
    {
        null => "Not run yet",
        0 => "Last run succeeded",
        267009 => "Currently running",
        267011 => "Not run yet",
        unchecked((int)0x80070002) => "Last run failed (file not found)",
        _ => $"Last run returned 0x{unchecked((uint)code.Value):X8}",
    };

    private static string DescribeResult(PSObject row)
    {
        var v = row.Properties["LastTaskResult"]?.Value;
        if (v is null) return DescribeResultCode(null);
        if (v is int i) return DescribeResultCode(i);
        return int.TryParse(v.ToString(), out var parsed) ? DescribeResultCode(parsed) : DescribeResultCode(null);
    }

    private static string? Str(PSObject? obj, string property) => obj?.Properties[property]?.Value?.ToString();

    private static DateTime? Date(PSObject? obj, string property) => obj?.Properties[property]?.Value switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };
}
