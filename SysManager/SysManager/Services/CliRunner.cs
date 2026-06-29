// SysManager · CliRunner — headless command-line entry point for scripting/automation
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text;
using System.Text.Json;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Parses and runs SysManager's command-line interface, so power users and sysadmins can
/// automate the safe maintenance actions from scripts, Task Scheduler, or deployment tools
/// (e.g. <c>SysManager.exe --cleanup --silent</c> or <c>SysManager.exe --health --json</c>).
///
/// Only read-only or safe, non-destructive actions are exposed on the CLI — temp cleanup
/// (never follows reparse points), standby-list trim (non-destructive cache drop), and
/// read-only health/inventory. Anything that mutates the system irreversibly stays
/// GUI-only behind a confirmation dialog. <see cref="Parse"/> is pure; <see cref="ExecuteAsync"/>
/// returns the output text rather than writing it, so both are fully unit-testable.
/// </summary>
public sealed class CliRunner
{
    // Derived from the running assembly's version (the single source of truth set in the
    // csproj), so the CLI's reported version can never drift from the build — a hardcoded
    // const here had already gone stale by two minor releases.
    private static readonly string Version = UpdateService.CurrentVersion.ToString(3);

    // CLI verbs that put startup into headless mode. The elevation sentinel
    // (--relaunched-elevated) and the update-applier arg are deliberately absent, so they
    // route to their own startup branches and never get treated as a CLI command.
    private static readonly HashSet<string> CliVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h", "-?", "/?", "--version", "-v", "--list", "--health", "--cleanup", "--trim-ram",
    };

    /// <summary>True only when a recognized CLI verb is present — used by startup to choose
    /// headless mode over the GUI. Deliberately strict: an unrecognized flag (e.g. the
    /// elevation sentinel) does NOT trigger CLI mode, so other startup branches still run.</summary>
    public static bool IsCliInvocation(string[] args) => args.Any(a => CliVerbs.Contains(a.Trim()));

    /// <summary>
    /// Parses the argument list into a <see cref="CliRequest"/>. The first recognized verb
    /// wins; <c>--json</c> and <c>--silent</c> are modifiers. An unrecognized leading
    /// <c>--flag</c> becomes <see cref="CliCommand.Unknown"/> (usage error). Pure.
    /// </summary>
    public static CliRequest Parse(string[] args)
    {
        bool json = false, silent = false;
        CliCommand command = CliCommand.None;
        string? unknown = null;

        foreach (var raw in args)
        {
            var arg = raw.Trim().ToLowerInvariant();
            switch (arg)
            {
                case "--json": json = true; break;
                case "--silent" or "-s" or "/silent": silent = true; break;
                case "--help" or "-h" or "-?" or "/?": command = Pick(command, CliCommand.Help); break;
                case "--version" or "-v": command = Pick(command, CliCommand.Version); break;
                case "--list": command = Pick(command, CliCommand.List); break;
                case "--health": command = Pick(command, CliCommand.Health); break;
                case "--cleanup": command = Pick(command, CliCommand.Cleanup); break;
                case "--trim-ram": command = Pick(command, CliCommand.TrimRam); break;
                default:
                    // An unrecognized option flag is a usage error; bare tokens are ignored.
                    if (arg.StartsWith('-') || arg.StartsWith('/'))
                    {
                        command = CliCommand.Unknown;
                        unknown ??= raw.Trim();
                    }
                    break;
            }
        }
        return new CliRequest(command, json, silent, unknown);
    }

    // First explicit verb wins; later verbs are ignored (a single invocation does one thing).
    private static CliCommand Pick(CliCommand current, CliCommand next)
        => current is CliCommand.None or CliCommand.Unknown ? next : current;

    /// <summary>The recognized commands and one-line help, single source for help text and the in-app reference tab.</summary>
    public static IReadOnlyList<(string Flags, string Description)> Commands { get; } =
    [
        ("--help, -h", "Show this help and exit."),
        ("--version, -v", "Print the SysManager version and exit."),
        ("--list", "List the available CLI commands."),
        ("--health", "Print a system health score (read-only)."),
        ("--cleanup", "Delete temporary files from user and Windows TEMP (safe, never follows junctions)."),
        ("--trim-ram", "Purge the standby memory list (non-destructive; needs administrator)."),
        ("--json", "Modifier: emit machine-readable JSON instead of text."),
        ("--silent, -s", "Modifier: suppress non-essential output."),
    ];

    /// <summary>
    /// Executes a parsed request and returns the exit code plus the text to print. Side effects
    /// are limited to the safe actions described on each command. Never throws for a known
    /// command — failures are reported in the result.
    /// </summary>
    public async Task<CliResult> ExecuteAsync(CliRequest request, CancellationToken ct = default)
    {
        return request.Command switch
        {
            CliCommand.Help or CliCommand.List => new CliResult(CliResult.Ok, BuildHelp(request.Json)),
            CliCommand.Version => new CliResult(CliResult.Ok, request.Json ? Json(new { version = Version }) : Version),
            CliCommand.Health => await RunHealthAsync(request, ct).ConfigureAwait(false),
            CliCommand.Cleanup => await RunCleanupAsync(request, ct).ConfigureAwait(false),
            CliCommand.TrimRam => RunTrimRam(request),
            CliCommand.Unknown => new CliResult(CliResult.UsageError,
                $"Unknown option '{request.UnknownArg}'.\n\n{BuildHelp(false)}"),
            _ => new CliResult(CliResult.UsageError, BuildHelp(false)),
        };
    }

    // ── Command implementations ────────────────────────────────────────────

    private static async Task<CliResult> RunHealthAsync(CliRequest request, CancellationToken ct)
    {
        try
        {
            var svc = new HealthScoreService(new SystemInfoService(), new DiskHealthService(), new BatteryService());
            var r = await svc.ComputeAsync(ct).ConfigureAwait(false);
            return request.Json
                ? new CliResult(CliResult.Ok, Json(new { score = r.Score, label = r.Label, disk = r.DiskScore, ram = r.RamScore, uptime = r.UptimeScore }))
                : new CliResult(CliResult.Ok, $"Health score: {r.Score}/100 ({r.Label})");
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException)
        {
            return new CliResult(CliResult.Error, request.Json ? Json(new { error = ex.Message }) : $"Health check failed: {ex.Message}");
        }
    }

    private static async Task<CliResult> RunCleanupAsync(CliRequest request, CancellationToken ct)
    {
        try
        {
            var (bytes, files, errors) = await TuneUpService.CleanTempFilesAsync(ct).ConfigureAwait(false);
            double mb = bytes / 1024.0 / 1024.0;
            return request.Json
                ? new CliResult(CliResult.Ok, Json(new { freedBytes = bytes, freedMB = Math.Round(mb, 1), filesDeleted = files, errors }))
                : new CliResult(CliResult.Ok, request.Silent
                    ? $"{mb:F0} MB freed"
                    : $"Cleanup complete: freed {mb:F1} MB across {files} file(s){(errors > 0 ? $", {errors} skipped" : "")}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CliResult(CliResult.Error, request.Json ? Json(new { error = ex.Message }) : $"Cleanup failed: {ex.Message}");
        }
    }

    private static CliResult RunTrimRam(CliRequest request)
    {
        var svc = new StandbyMemoryService();
        var before = svc.GetMemoryStatus();
        bool ok = svc.TryPurgeStandbyList(out var error);
        if (!ok)
            return new CliResult(CliResult.Error, request.Json ? Json(new { error }) : $"Standby trim failed: {error}");

        var after = svc.GetMemoryStatus();
        return request.Json
            ? new CliResult(CliResult.Ok, Json(new { freedMB = Math.Round((after.AvailableBytes - before.AvailableBytes) / 1024.0 / 1024.0, 0), loadPercentAfter = after.LoadPercent }))
            : new CliResult(CliResult.Ok, request.Silent ? "Standby list purged" : $"Standby list purged. Memory load now {after.LoadPercent}%.");
    }

    // ── Help / formatting ───────────────────────────────────────────────────

    /// <summary>Builds the help text (or a JSON command list). Pure.</summary>
    public static string BuildHelp(bool json)
    {
        if (json)
            return Json(new { version = Version, commands = Commands.Select(c => new { flags = c.Flags, description = c.Description }) });

        var sb = new StringBuilder();
        sb.AppendLine($"SysManager {Version} — command-line interface");
        sb.AppendLine();
        sb.AppendLine("Usage: SysManager.exe <command> [--json] [--silent]");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        int width = Commands.Max(c => c.Flags.Length);
        foreach (var (flags, description) in Commands)
            sb.AppendLine($"  {flags.PadRight(width)}  {description}");
        sb.AppendLine();
        sb.AppendLine("Exit codes: 0 success · 1 error · 2 usage error.");
        return sb.ToString().TrimEnd();
    }

    private static string Json(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    internal static string CurrentVersion => Version;
}
