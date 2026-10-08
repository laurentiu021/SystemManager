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
            Collection<PSObject> results = await _ps
                .RunAsync(RegisterScript, RegisterParameters(schedule, exePath), ct)
                .ConfigureAwait(false);
            // The script emits the registered task's state; one row back == success.
            return results.Count == 1 && Str(results[0], "State") is not null;
        }
        catch (RuntimeException ex)
        {
            Log.Warning("Maintenance schedule registration failed: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The parameters <see cref="RegisterScript"/> is invoked with, for a schedule and an executable path.
    /// </summary>
    /// <remarks>
    /// Pure and <c>internal</c> so a test can assert that a schedule's power and idle policy actually reaches
    /// the script. The alternative — calling <c>RegisterAsync</c> — registers a real Windows scheduled task on
    /// the machine running the test, which is not something a unit suite may do to a developer's PC.
    /// </remarks>
    internal static Dictionary<string, object?> RegisterParameters(MaintenanceSchedule schedule, string exePath)
        => new()
        {
            ["Exe"] = exePath,
            ["Args"] = schedule.CliArguments,
            ["Folder"] = TaskFolder,
            ["Name"] = TaskName,
            ["Daily"] = schedule.Frequency == MaintenanceFrequency.Daily,
            ["At"] = $"{schedule.Hour:D2}:{schedule.Minute:D2}",
            ["DayOfWeek"] = schedule.DayOfWeek.ToString(),
            ["OnBattery"] = schedule.RunOnBattery,
            ["OnlyIfIdle"] = schedule.OnlyWhenIdle,
        };

    /// <summary>
    /// Reads the current state of the maintenance task: Exists=false when Windows says there is no task, and
    /// null when the read itself failed.
    /// </summary>
    /// <remarks>
    /// A failed read used to return "not registered", so the tab said "No maintenance is scheduled yet." and a
    /// Save asked to create a schedule it might be about to replace (#2487). <see cref="StatusScript"/> says
    /// how the two are told apart.
    /// </remarks>
    public async Task<MaintenanceStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = TaskName };
            Collection<PSObject> results = await _ps.RunAsync(StatusScript, parameters, ct).ConfigureAwait(false);
            if (results.Count == 0) return MaintenanceStatus.NotRegistered;

            var row = results[0];
            return new MaintenanceStatus(
                Exists: true,
                State: Str(row, "State"),
                LastRun: Date(row, "LastRunTime"),
                NextRun: Date(row, "NextRunTime"),
                LastResultDescription: DescribeResult(row),
                MissedRuns: Count(row, "MissedRunsCount"),
                PurgesStandby: IsStandbyPurge(Str(row, "Arguments")));
        }
        catch (RuntimeException ex)
        {
            Log.Debug("Maintenance status read failed: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>Removes the maintenance task. Returns true if it no longer exists afterwards.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = TaskName };
            await _ps.RunAsync(RemoveScript, parameters, ct).ConfigureAwait(false);
            // Removed only when Windows then answers that there is no task. A read-back that fails proves nothing.
            var status = await GetStatusAsync(ct).ConfigureAwait(false);
            return status is { Exists: false };
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
    internal const string RegisterScript = """
        param([string]$Exe, [string]$Args, [string]$Folder, [string]$Name,
              [bool]$Daily, [string]$At, [string]$DayOfWeek,
              [bool]$OnBattery, [bool]$OnlyIfIdle)
        $action = New-ScheduledTaskAction -Execute $Exe -Argument $Args
        if ($Daily) {
            $trigger = New-ScheduledTaskTrigger -Daily -At $At
        } else {
            $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DayOfWeek -At $At
        }
        # Splatted from typed [bool] parameters, so the policy is still a whitelisted value and never
        # free-form text. AllowStartIfOnBatteries defaults to $FALSE in New-ScheduledTaskSettingsSet, and
        # inheriting that default is the defect: on a laptop running unplugged the task simply did not start,
        # and -StartWhenAvailable then fired it late, whenever the condition next cleared.
        # [TimeSpan]::FromHours, not New-TimeSpan: a .NET call needs no module. Until #2476 a standard user's
        # runspace loaded Microsoft.PowerShell.Core only, and a Utility cmdlet here failed there.
        $settingsArgs = @{
            StartWhenAvailable = $true
            DontStopOnIdleEnd  = $true
            ExecutionTimeLimit = [TimeSpan]::FromHours(1)
        }
        if ($OnBattery) {
            $settingsArgs['AllowStartIfOnBatteries'] = $true
            $settingsArgs['DontStopIfGoingOnBatteries'] = $true
        }
        if ($OnlyIfIdle) { $settingsArgs['RunOnlyIfIdle'] = $true }
        $settings = New-ScheduledTaskSettingsSet @settingsArgs
        $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $Name -TaskPath $Folder -Action $action -Trigger $trigger `
            -Settings $settings -Principal $principal -Description "SysManager automated maintenance." `
            -Force -ErrorAction Stop |
            Select-Object @{ n='State'; e={ [string]$_.State } } | Out-Null
        Get-ScheduledTask -TaskName $Name -TaskPath $Folder -ErrorAction Stop |
            Select-Object @{ n='State'; e={ [string]$_.State } }
        """;

    /// <summary>The script <see cref="GetStatusAsync"/> runs: one row for the task, or none when there is none.</summary>
    /// <remarks>
    /// "There is no task" is only what Windows says it is: <c>Get-ScheduledTask</c> reports <c>ObjectNotFound</c>,
    /// and the script returns nothing. Any other error is thrown, so the runner raises it and the read counts
    /// as failed. The read used <c>-ErrorAction SilentlyContinue</c>, which made every failure look like an
    /// absent task (#2487).
    /// <para>Internal so the integration suite can run this exact text in Windows PowerShell with
    /// <c>Get-ScheduledTask</c> shadowed by a function.</para>
    /// </remarks>
    internal const string StatusScript = """
        param([string]$Folder, [string]$Name)
        try { $task = Get-ScheduledTask -TaskName $Name -TaskPath $Folder -ErrorAction Stop }
        catch {
            if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return }
            throw
        }
        if ($null -eq $task) { return }
        $info = Get-ScheduledTaskInfo -TaskName $Name -TaskPath $Folder -ErrorAction SilentlyContinue
        # Arguments is the command line the task runs, so the tab can spot a standby purge, which fails every run
        # without administrator rights (#2593). SysManager registers one action, so the first is the one it set.
        [PSCustomObject]@{
            State           = [string]$task.State
            LastRunTime     = $info.LastRunTime
            NextRunTime     = $info.NextRunTime
            LastTaskResult  = $info.LastTaskResult
            MissedRunsCount = $info.NumberOfMissedRuns
            Arguments       = [string](@($task.Actions)[0].Arguments)
        }
        """;

    private const string RemoveScript = """
        param([string]$Folder, [string]$Name)
        Unregister-ScheduledTask -TaskName $Name -TaskPath $Folder -Confirm:$false -ErrorAction SilentlyContinue
        """;

    /// <summary>Plain-language description of the last task result code. Pure/testable.</summary>
    /// <remarks>
    /// The task's result is the exit code of the SysManager it started, so the CLI's own codes are described too.
    /// They read "Last run returned 0x00000001", and that 1 was every scheduled standby purge failing for want of
    /// administrator rights (#2593).
    /// </remarks>
    public static string DescribeResultCode(int? code) => code switch
    {
        null => "Not run yet",
        0 => "Last run succeeded",
        CliResult.Error => "Last run failed",
        CliResult.UsageError => "Last run failed (command not recognised)",
        267009 => "Currently running",
        267011 => "Not run yet",
        unchecked((int)0x80070002) => "Last run failed (file not found)",
        _ => $"Last run returned 0x{unchecked((uint)code.Value):X8}",
    };

    /// <summary>
    /// Whether a task's command line purges standby memory, read the way the CLI reads it when the task runs.
    /// </summary>
    /// <remarks>
    /// Parsed by <see cref="CliRunner.Parse"/> rather than matched as text, so the old <c>--trim-ram</c> name counts,
    /// and the answer is whatever the scheduled run will actually do (#2593).
    /// </remarks>
    internal static bool IsStandbyPurge(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && CliRunner.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Command == CliCommand.PurgeStandby;

    private static string DescribeResult(PSObject row)
    {
        var value = row.Properties["LastTaskResult"]?.Value;
        if (value is null) return DescribeResultCode(null);
        if (value is int signed) return DescribeResultCode(signed);
        if (value is uint unsigned) return DescribeResultCode(unchecked((int)unsigned));

        var text = value.ToString();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out signed))
            return DescribeResultCode(signed);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unsigned)
            ? DescribeResultCode(unchecked((int)unsigned))
            : DescribeResultCode(null);
    }

    private static string? Str(PSObject? obj, string property) => obj?.Properties[property]?.Value?.ToString();

    /// <summary>
    /// Reads a count Windows reports as one of several integral CIM types, or null when it is absent.
    /// </summary>
    /// <remarks>
    /// <c>NumberOfMissedRuns</c> comes back as <c>uint</c> from the CIM layer but as <c>int</c> through some
    /// hosts, and the existing <c>DescribeResult</c> already has to handle both for <c>LastTaskResult</c> —
    /// so a single-type cast here would silently read null on whichever host disagreed.
    /// </remarks>
    private static int? Count(PSObject? obj, string property) => obj?.Properties[property]?.Value switch
    {
        int value => value,
        uint value => (int)Math.Min(value, int.MaxValue),
        long value => (int)Math.Clamp(value, 0, int.MaxValue),
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => null,
    };

    private static DateTime? Date(PSObject? obj, string property) => obj?.Properties[property]?.Value switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };
}
