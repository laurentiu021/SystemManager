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
    [InlineData("--trim-ram", CliCommand.TrimRam)]
    public void Parse_RecognizesVerbs(string arg, CliCommand expected)
        => Assert.Equal(expected, CliRunner.Parse([arg]).Command);

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
