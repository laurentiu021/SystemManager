// SysManager · MaintenanceSchedulerService — registers a recurring maintenance task
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Security.Principal;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Creates, reads, and removes the SysManager-owned Windows scheduled task that runs the app
/// headless (via its CLI) on a recurring schedule for the account SysManager runs as. Uses the
/// <c>ScheduledTasks</c> PowerShell module through the shared <see cref="IPowerShellRunner"/> seam.
///
/// Safety: each Windows account has its own task, <see cref="TaskNamePrefix"/> followed by the
/// account's SID in <see cref="TaskFolder"/>, and this service only ever touches that one — it
/// never enumerates or modifies other tasks, including another account's. The command it
/// registers is built from an allowlisted CLI argument string (no free-form user input),
/// pointed at the running executable's own path, and registered in the current user's
/// context (no admin required, runs only when that user is logged on).
/// </summary>
public sealed class MaintenanceSchedulerService
{
    /// <summary>The folder every account's task is in: the root of the Task Scheduler Library.</summary>
    /// <remarks>
    /// One task per account, named with the account's SID as Windows names its own per-user tasks, and in the
    /// root so that no folder one account created can stand in another's way. The name used to be one fixed path
    /// for the whole PC, <c>\SysManager\Scheduled Maintenance</c>: on a PC with two accounts each tab would have
    /// shown the other account's schedule as its own, and Save or Remove run as administrator would have replaced
    /// or deleted it. Nothing was ever registered there, because the register script could not build its action
    /// (see <see cref="RegisterScript"/>), so there is no earlier task to carry over.
    /// </remarks>
    public const string TaskFolder = @"\";

    /// <summary>The start of every account's task name; the account's SID follows it.</summary>
    public const string TaskNamePrefix = "SysManager Scheduled Maintenance-";

    private readonly IPowerShellRunner _ps;
    private readonly string? _accountSid;

    public MaintenanceSchedulerService(IPowerShellRunner ps) : this(ps, CurrentAccountSid()) { }

    /// <summary>A service for the account with the given SID, or for none when it is null.</summary>
    /// <remarks>
    /// Internal so a test can be another account than the one running it; the public constructor uses the
    /// account SysManager runs as.
    /// </remarks>
    internal MaintenanceSchedulerService(IPowerShellRunner ps, string? accountSid)
    {
        _ps = ps;
        _accountSid = accountSid;
    }

    /// <summary>The name of this account's task, or null when Windows did not say which account this is.</summary>
    public string? TaskName => _accountSid is { } sid ? TaskNamePrefix + sid : null;

    /// <summary>Registers (or replaces) this account's maintenance task from the given schedule.
    /// Returns true on success. The executable path defaults to the running process.</summary>
    public async Task<bool> RegisterAsync(MaintenanceSchedule schedule, string? exePath = null, CancellationToken ct = default)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warning("Maintenance schedule not registered — could not resolve executable path.");
            return false;
        }
        if (_accountSid is not { } sid)
        {
            Log.Warning("Maintenance schedule not registered — Windows did not say which account this is.");
            return false;
        }

        try
        {
            Collection<PSObject> results = await _ps
                .RunAsync(RegisterScript, RegisterParameters(schedule, exePath, sid), ct)
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
    /// The parameters <see cref="RegisterScript"/> is invoked with, for a schedule, an executable path and the SID
    /// of the account the task is for.
    /// </summary>
    /// <remarks>
    /// Pure and <c>internal</c> so a test can assert that a schedule's power and idle policy actually reaches
    /// the script. The alternative — calling <c>RegisterAsync</c> — registers a real Windows scheduled task on
    /// the machine running the test, which is not something a unit suite may do to a developer's PC.
    /// </remarks>
    internal static Dictionary<string, object?> RegisterParameters(MaintenanceSchedule schedule, string exePath, string accountSid)
        => new()
        {
            ["Exe"] = exePath,
            ["Arguments"] = schedule.CliArguments,
            ["Folder"] = TaskFolder,
            ["Name"] = TaskNamePrefix + accountSid,
            ["UserId"] = accountSid,
            ["Daily"] = schedule.Frequency == MaintenanceFrequency.Daily,
            ["At"] = $"{schedule.Hour:D2}:{schedule.Minute:D2}",
            ["DayOfWeek"] = schedule.DayOfWeek.ToString(),
            ["OnBattery"] = schedule.RunOnBattery,
            ["OnlyIfIdle"] = schedule.OnlyWhenIdle,
        };

    /// <summary>
    /// Reads the current state of this account's maintenance task: Exists=false when Windows says there is none,
    /// and null when the read itself failed.
    /// </summary>
    /// <remarks>
    /// A failed read used to return "not registered", so the tab said "No maintenance is scheduled yet." and a
    /// Save asked to create a schedule it might be about to replace (#2487). <see cref="StatusScript"/> says
    /// how the two are told apart.
    /// </remarks>
    public async Task<MaintenanceStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        if (TaskName is not { } name) return null;
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = name };
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

    /// <summary>Removes this account's maintenance task. Returns true if it no longer exists afterwards.</summary>
    /// <remarks>
    /// A refusal is logged, because the tab tells the user to look there, and the read-back still decides: a task
    /// that turns out to be gone is removed whatever the refusal said.
    /// </remarks>
    public async Task<bool> RemoveAsync(CancellationToken ct = default)
    {
        if (TaskName is not { } name) return false;
        try
        {
            var parameters = new Dictionary<string, object?> { ["Folder"] = TaskFolder, ["Name"] = name };
            await _ps.RunAsync(RemoveScript, parameters, ct).ConfigureAwait(false);
        }
        catch (RuntimeException ex)
        {
            Log.Warning("Maintenance schedule removal failed: {Error}", ex.Message);
        }
        // Removed only when Windows then answers that there is no task. A read-back that fails proves nothing.
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        return status is { Exists: false };
    }

    /// <summary>The SID of the account SysManager runs as, or null when Windows does not say.</summary>
    private static string? CurrentAccountSid()
    {
        try
        {
            // WindowsIdentity owns a native token handle, so it is disposed here, as RecycleBinHelper does.
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value is { Length: > 0 } sid ? sid : null;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warning("Scheduled Maintenance could not read which account this is: {Error}", ex.Message);
            return null;
        }
    }

    // ── PowerShell scripts (parameterised; no string interpolation of user input) ──

    // Register-ScheduledTask with -Force replaces any existing task of the same name, so
    // re-registering just updates the schedule. The action runs the app's own exe with the
    // allowlisted CLI args; the trigger is daily or weekly at the chosen time. The task is
    // pinned to the account SysManager runs as, by the SID in its name, at the LIMITED run level
    // via an explicit principal — so it needs no admin to register and runs only when that user
    // is logged on, never with elevation. (Previously this relied on the cmdlet's default
    // principal; the explicit principal makes the security posture deliberate rather than implicit.)
    //
    // The command line is $Arguments, never $Args: PowerShell sets $args itself once the parameters
    // are bound, to the arguments nothing bound, so a parameter of that name always reads as empty.
    // It was $Args from 1.50.0, so New-ScheduledTaskAction refused it and no schedule saved from the
    // tab was ever registered.
    //
    // The steps are one try, and each stops on an error, so the first that fails ends the script.
    // A refused parameter otherwise ends only its own statement and leaves the variable it was
    // assigning as it was, and the runner keeps its runspace for the next script, so Register went
    // on with whatever $action an earlier script had left, or with none.
    internal const string RegisterScript = """
        param([string]$Exe, [string]$Arguments, [string]$Folder, [string]$Name, [string]$UserId,
              [bool]$Daily, [string]$At, [string]$DayOfWeek,
              [bool]$OnBattery, [bool]$OnlyIfIdle)
        try {
            $action = New-ScheduledTaskAction -Execute $Exe -Argument $Arguments -ErrorAction Stop
            if ($Daily) {
                $trigger = New-ScheduledTaskTrigger -Daily -At $At -ErrorAction Stop
            } else {
                $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $DayOfWeek -At $At -ErrorAction Stop
            }
            # Splatted from typed [bool] parameters, so the policy is still an allowlisted value and never
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
            $settings = New-ScheduledTaskSettingsSet @settingsArgs -ErrorAction Stop
            $principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited -ErrorAction Stop
            Register-ScheduledTask -TaskName $Name -TaskPath $Folder -Action $action -Trigger $trigger `
                -Settings $settings -Principal $principal -Description "SysManager automated maintenance." `
                -Force -ErrorAction Stop |
                Select-Object @{ n='State'; e={ [string]$_.State } } | Out-Null
            Get-ScheduledTask -TaskName $Name -TaskPath $Folder -ErrorAction Stop |
                Select-Object @{ n='State'; e={ [string]$_.State } }
        } catch { throw }
        """;

    /// <summary>The script <see cref="GetStatusAsync"/> runs: one row for the task, or none when there is none.</summary>
    /// <remarks>
    /// "There is no task" is only what Windows says it is: <c>Get-ScheduledTask</c> reports <c>ObjectNotFound</c>,
    /// and the script returns nothing. Any other error is thrown, so the runner raises it and the read counts
    /// as failed. The read used <c>-ErrorAction SilentlyContinue</c>, which made every failure look like an
    /// absent task (#2487).
    /// <para>The task's run information is read the same way. Read with <c>SilentlyContinue</c> too, a failed read of
    /// it left every field empty, which the tab showed as Last result "Not run yet" for a task that may have run many
    /// times.</para>
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
        try { $info = Get-ScheduledTaskInfo -TaskName $Name -TaskPath $Folder -ErrorAction Stop }
        catch {
            if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return }
            throw
        }
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

    /// <summary>The script <see cref="RemoveAsync"/> runs.</summary>
    /// <remarks>
    /// A task that is already gone is the one quiet case, as in <see cref="StatusScript"/>. Any other refusal is
    /// thrown, so <see cref="RemoveAsync"/> logs the reason the tab tells the user to look for. With
    /// <c>-ErrorAction SilentlyContinue</c> nothing was thrown, the log held no reason, and the tab still said "Could
    /// not remove the task. Check the log." Internal for the same reason as <see cref="StatusScript"/>.
    /// </remarks>
    internal const string RemoveScript = """
        param([string]$Folder, [string]$Name)
        try { Unregister-ScheduledTask -TaskName $Name -TaskPath $Folder -Confirm:$false -ErrorAction Stop }
        catch {
            if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return }
            throw
        }
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
