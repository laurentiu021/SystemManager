// SysManager · CliRunnerTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

public class CliRunnerTests
{
    // ── Parse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoArgs_IsNone()
        => Assert.Equal(CliCommand.None, CliRunner.Parse([]).Command);

    [Theory]
    [InlineData("--help", CliCommand.Help)]
    [InlineData("-h", CliCommand.Help)]
    [InlineData("/?", CliCommand.Help)]
    [InlineData("--version", CliCommand.Version)]
    [InlineData("-v", CliCommand.Version)]
    [InlineData("--list", CliCommand.List)]
    [InlineData("--health", CliCommand.Health)]
    [InlineData("--cleanup", CliCommand.Cleanup)]
    [InlineData("--purge-standby", CliCommand.PurgeStandby)]
    public void Parse_RecognizesVerbs(string arg, CliCommand expected)
        => Assert.Equal(expected, CliRunner.Parse([arg]).Command);

    /// <summary>
    /// <c>--trim-ram</c> still resolves to the standby purge. This is a compatibility promise, not a
    /// duplicate of the row above.
    /// </summary>
    /// <remarks>
    /// The verb was renamed to <c>--purge-standby</c> because <c>--trim-ram</c> named Performance Mode's
    /// operation while doing Standby List Cleaner's (#1524). The old spelling has to keep working: a
    /// maintenance schedule registered before the rename has <c>"--trim-ram --silent"</c> baked into a
    /// Windows scheduled task's argument string on the user's machine, and an unrecognised flag routes to
    /// <c>Unknown</c> — so dropping the alias would leave that task running <c>--help</c> nightly and
    /// reporting success while doing nothing.
    /// <para>Asserted here rather than as another <c>[InlineData]</c> row so that deleting the alias
    /// fails a test whose name says what broke, instead of one that reads like a parser typo.</para>
    /// </remarks>
    [Fact]
    public void Parse_StillAcceptsTheFormerTrimRamSpelling()
    {
        Assert.Equal(CliCommand.PurgeStandby, CliRunner.Parse(["--trim-ram"]).Command);
        Assert.Equal(CliCommand.PurgeStandby, CliRunner.Parse(["--TRIM-RAM"]).Command);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
        => Assert.Equal(CliCommand.Cleanup, CliRunner.Parse(["--CLEANUP"]).Command);

    [Fact]
    public void Parse_ModifiersSetFlags()
    {
        var r = CliRunner.Parse(["--cleanup", "--json", "--silent"]);
        Assert.Equal(CliCommand.Cleanup, r.Command);
        Assert.True(r.Json);
        Assert.True(r.Silent);
    }

    [Fact]
    public void Parse_FirstVerbWins()
    {
        // A single invocation does one thing; the first explicit verb is kept.
        var r = CliRunner.Parse(["--health", "--cleanup"]);
        Assert.Equal(CliCommand.Health, r.Command);
    }

    [Fact]
    public void Parse_UnknownFlag_IsUnknownWithArgCaptured()
    {
        var r = CliRunner.Parse(["--frobnicate"]);
        Assert.Equal(CliCommand.Unknown, r.Command);
        Assert.Equal("--frobnicate", r.UnknownArg);
    }

    [Fact]
    public void Parse_BareTokens_AreIgnored_NotUnknown()
    {
        // Non-flag tokens (no leading - or /) are not usage errors.
        Assert.Equal(CliCommand.None, CliRunner.Parse(["foo", "bar"]).Command);
    }

    // ── IsCliInvocation: must NOT hijack the elevation sentinel ─────────────

    [Fact]
    public void IsCliInvocation_TrueForKnownVerb()
        => Assert.True(CliRunner.IsCliInvocation(["--health"]));

    [Fact]
    public void IsCliInvocation_TrueForUnknownFlag_SoItReportsUsageError()
        // Regression: an unrecognized flag (typo) must run headless and report a usage
        // error (exit 2), NOT silently fall through and open the GUI window.
        => Assert.True(CliRunner.IsCliInvocation(["--bogus"]));

    /// <summary>
    /// The slash spelling reaches CLI mode too, so <c>SysManager.exe /?</c> prints help instead of
    /// opening the window.
    /// </summary>
    /// <remarks>
    /// Written while removing the dead <c>CliVerbs</c> set (#2159). Nothing asserted this: the parse
    /// tests cover <c>/?</c> mapping to Help, but no test checked that a slash argument makes the app
    /// headless in the first place, and it is the LEADING-SLASH clause of <c>IsCliToken</c> that decides
    /// that. Dropping the clause left every test green, which is the same hole the deleted set created —
    /// so the fix for it comes with the check that would have caught it.
    /// </remarks>
    [Fact]
    public void IsCliInvocation_TrueForSlashSpelledHelp()
        => Assert.True(CliRunner.IsCliInvocation(["/?"]));

    [Fact]
    public void IsCliInvocation_FalseForBareTokens()
        // Non-flag tokens (no leading - or /) never trigger CLI mode.
        => Assert.False(CliRunner.IsCliInvocation(["foo", "bar"]));

    [Fact]
    public void IsCliInvocation_FalseForNoArgs()
        => Assert.False(CliRunner.IsCliInvocation([]));

    [Fact]
    public void IsCliInvocation_FalseForElevationSentinel()
    {
        // The elevation relaunch arg must route to its own startup branch, NOT CLI mode.
        Assert.False(CliRunner.IsCliInvocation(["--relaunched-elevated"]));
    }

    [Fact]
    public void IsCliInvocation_FalseForUpdateApplierArg()
        => Assert.False(CliRunner.IsCliInvocation(["--apply-update", @"C:\x.exe", "1234"]));

    // ── ExecuteAsync (read-only commands only — no system mutation) ─────────

    [Fact]
    public async Task Execute_Version_ReturnsVersionAndOk()
    {
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.Version));
        Assert.Equal(CliResult.Ok, r.ExitCode);
        Assert.Equal(CliRunner.CurrentVersion, r.Output);
    }

    [Fact]
    public void CurrentVersion_MatchesBuildVersion_NeverDrifts()
    {
        // Regression: the CLI version was a hardcoded const that drifted two minor
        // releases behind the build. It must now equal the running assembly version
        // (the csproj single source of truth), so --version can never report stale data.
        Assert.Equal(UpdateService.CurrentVersion.ToString(3), CliRunner.CurrentVersion);
    }

    [Fact]
    public async Task Execute_VersionJson_IsJson()
    {
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.Version, Json: true));
        Assert.Equal(CliResult.Ok, r.ExitCode);
        Assert.Contains("\"version\"", r.Output);
        Assert.Contains(CliRunner.CurrentVersion, r.Output);
    }

    [Fact]
    public async Task Execute_Help_ListsEveryCommand()
    {
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.Help));
        Assert.Equal(CliResult.Ok, r.ExitCode);
        foreach (var (flags, _) in CliRunner.Commands)
        {
            // The first flag of each command must appear in the help text.
            var primary = flags.Split(',')[0].Trim();
            Assert.Contains(primary, r.Output);
        }
    }

    [Fact]
    public async Task Execute_Unknown_IsUsageError()
    {
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.Unknown, UnknownArg: "--nope"));
        Assert.Equal(CliResult.UsageError, r.ExitCode);
        Assert.Contains("--nope", r.Output);
    }

    [Fact]
    public async Task Execute_None_IsUsageErrorWithHelp()
    {
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.None));
        Assert.Equal(CliResult.UsageError, r.ExitCode);
        Assert.Contains("Usage:", r.Output);
    }

    [Fact]
    public async Task Execute_UnknownJson_EmitsParseableJsonError()
    {
        // Regression: the usage-error arm ignored --json and emitted plain help text, so
        // `--bogus --json | ConvertFrom-Json` fed a JSON consumer non-JSON. It must now
        // return a structured {"error": ...} and NOT leak the help prose.
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.Unknown, Json: true, UnknownArg: "--nope"));
        Assert.Equal(CliResult.UsageError, r.ExitCode);
        var doc = System.Text.Json.JsonDocument.Parse(r.Output); // throws if not valid JSON
        Assert.Contains("--nope", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Usage:", r.Output);
    }

    [Fact]
    public async Task Execute_NoneJson_EmitsMachineReadableHelp()
    {
        // The bare-usage arm must also honor --json (machine-readable command catalog),
        // not dump the human help text to a JSON consumer.
        var r = await new CliRunner().ExecuteAsync(new CliRequest(CliCommand.None, Json: true));
        Assert.Equal(CliResult.UsageError, r.ExitCode);
        var doc = System.Text.Json.JsonDocument.Parse(r.Output); // throws if not valid JSON
        Assert.True(doc.RootElement.TryGetProperty("commands", out _));
    }

    // ── Help text / command catalog ─────────────────────────────────────────

    [Fact]
    public void Commands_NonEmpty_AllHaveFlagsAndDescription()
    {
        Assert.NotEmpty(CliRunner.Commands);
        Assert.All(CliRunner.Commands, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Flags));
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
        });
    }

    /// <summary>
    /// Every flag the help text advertises must actually parse. A documented verb that returns
    /// <see cref="CliCommand.Unknown"/> is worse than an undocumented one: the user reads it in
    /// <c>--help</c>, types it, and gets "Unknown option" back from the same program.
    /// </summary>
    /// <remarks>
    /// <c>Commands</c> and <c>Parse</c>'s switch are two independent lists of the same flags, and
    /// nothing compared them. That is the shape of defect the deleted <c>CliVerbs</c> set had (#2159):
    /// a list that looks authoritative, is never consulted, and drifts silently. This closes the
    /// direction that hurts a user; <c>ArchitectureTests</c> closes the other one.
    /// <para>Modifiers are included deliberately. <c>--json</c> on its own leaves the command at
    /// <see cref="CliCommand.None"/> rather than naming one, so the assertion is "not a usage error"
    /// rather than "is a command" — but it still catches a modifier being renamed in one place only.</para>
    /// </remarks>
    [Fact]
    public void EveryFlagInTheHelpCatalog_Parses()
    {
        var flags = CliRunner.Commands
            .SelectMany(c => c.Flags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        // A floor, because the whole check passes over an empty catalog: Commands is a static list
        // and a change to its shape (a rename, a different separator) would empty this silently.
        Assert.True(flags.Count >= 8,
            $"only {flags.Count} flags were read out of the help catalog — its shape changed and this "
            + "guard is no longer reading it.");

        foreach (var flag in flags)
        {
            var parsed = CliRunner.Parse([flag]);
            Assert.False(parsed.Command is CliCommand.Unknown,
                $"'{flag}' is advertised by --help but Parse rejects it as an unknown option. Add an arm "
                + "for it in Parse, or stop documenting it.");
        }
    }

    [Fact]
    public void BuildHelp_Text_MentionsExitCodes()
        => Assert.Contains("Exit codes", CliRunner.BuildHelp(json: false));

    [Fact]
    public void BuildHelp_Json_IsMachineReadable()
    {
        var json = CliRunner.BuildHelp(json: true);
        Assert.Contains("\"commands\"", json);
        Assert.Contains("\"version\"", json);
    }

    [Fact]
    public void ExitCodeConstants_AreConventional()
    {
        Assert.Equal(0, CliResult.Ok);
        Assert.Equal(1, CliResult.Error);
        Assert.Equal(2, CliResult.UsageError);
    }
}
