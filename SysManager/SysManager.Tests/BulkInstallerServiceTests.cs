// SysManager · BulkInstallerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="BulkInstallerService"/> (audit finding tests #4, #9).
/// <para>
/// The service is the only barrier between preset/user package IDs and a
/// shelled-out winget process: <c>InstallAsync</c> rejects anything that
/// fails <c>PackageIdPattern</c> before any process launches, preventing
/// command injection into winget arguments. These tests pin that guard and
/// assert the exact winget invocation on the happy path — using the
/// <see cref="IPowerShellRunner"/> seam so no winget process ever runs.
/// </para>
/// </summary>
public class BulkInstallerServiceTests
{
    // ---------- injection / validation guard (#4) ----------

    public static IEnumerable<object[]> InvalidPackageIds()
    {
        yield return ["App & calc.exe"];          // command chaining via &
        yield return ["App; calc.exe"];           // command separator
        yield return ["App | calc.exe"];          // pipe
        yield return ["App\"--evil"];             // quote break-out
        yield return ["App`whoami`"];             // backtick subexpression
        yield return ["App$(whoami)"];            // $() subexpression
        yield return ["App\nGit.Git"];            // newline injection
        yield return [new string('A', 300)];      // exceeds the 256-char cap
        yield return ["   "];                      // whitespace only
    }

    [Theory]
    [MemberData(nameof(InvalidPackageIds))]
    public async Task InstallAsync_InvalidId_ThrowsArgumentException_AndNeverRunsProcess(string badId)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var svc = new BulkInstallerService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.InstallAsync(badId));

        // The guard runs before any process launch — the runner must never be called.
        await runner.DidNotReceiveWithAnyArgs()
            .RunProcessAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task InstallAsync_NullOrEmptyId_ThrowsArgumentException(string? badId)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var svc = new BulkInstallerService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.InstallAsync(badId!));
    }

    // ---------- happy path: exact winget invocation (#9) ----------

    [Fact]
    public async Task InstallAsync_ValidId_InvokesWingetWithExpectedArgs()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(0);
        var svc = new BulkInstallerService(runner);

        var exit = await svc.InstallAsync("Git.Git");

        Assert.Equal(0, exit);
        await runner.Received(1).RunProcessAsync(
            "winget",
            Arg.Is<string>(a =>
                a.Contains("install") &&
                a.Contains("--id \"Git.Git\"") &&
                a.Contains("-e") &&
                a.Contains("--silent") &&
                a.Contains("--accept-source-agreements") &&
                a.Contains("--accept-package-agreements")),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData("Mozilla.Firefox")]
    [InlineData("Valve.Steam")]
    public async Task InstallAsync_AcceptsValidPublicIds(string id)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(0);
        var svc = new BulkInstallerService(runner);

        var exit = await svc.InstallAsync(id);

        Assert.Equal(0, exit);
        await runner.Received(1).RunProcessAsync(
            "winget",
            Arg.Is<string>(a => a.Contains($"--id \"{id}\"")),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task InstallAsync_PropagatesNonZeroExitCode()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(1);
        var svc = new BulkInstallerService(runner);

        var exit = await svc.InstallAsync("Git.Git");

        Assert.Equal(1, exit);
    }

    // ---------- search query sanitization (argument-injection guard) ----------

    [Theory]
    [InlineData("firefox", "firefox")]
    [InlineData("  vlc  ", "vlc")]                                   // trimmed
    [InlineData("foo\" & calc \"", "foo & calc")]                   // embedded quotes stripped (the & is harmless inside a quoted, non-shell arg)
    [InlineData("bar\"--source\"evil", "bar--sourceevil")]          // quote break-out stripped
    [InlineData("baz\r\ninject", "bazinject")]                       // control chars (CR/LF) stripped
    public void SanitizeQuery_StripsQuotesAndControlChars(string input, string expected)
        => Assert.Equal(expected, BulkInstallerService.SanitizeQuery(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]      // only a quote → nothing usable remains
    public void SanitizeQuery_EmptyOrUnusable_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, BulkInstallerService.SanitizeQuery(input));

    [Fact]
    public async Task SearchAsync_RoutesThroughRunner_WithSanitizedQuery()
    {
        // Regression: the VM previously hand-built a ProcessStartInfo with FileName="winget"
        // and no WorkingDirectory (binary-planting LPE) AND interpolated the raw query
        // (argument injection). SearchAsync routes through the runner (WorkingDirectory pinned)
        // and sanitizes the query before interpolation.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(0);
        var svc = new BulkInstallerService(runner);

        await svc.SearchAsync("foo\" & calc \"");

        // The embedded double-quotes must be gone from the argument that reaches winget, so the
        // query stays inside its own quoted token and can't inject extra winget arguments. The
        // '&' is retained but harmless — it's inside a quoted arg and there is no shell
        // (UseShellExecute=false). So exactly ONE opening + ONE closing quote wrap the query.
        await runner.Received(1).RunProcessAsync(
            "winget",
            Arg.Is<string>(a => a.Contains("search \"foo & calc\"") && a.Split('"').Length == 3),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task SearchAsync_EmptyAfterSanitize_NeverRunsProcess()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        var svc = new BulkInstallerService(runner);

        var lines = await svc.SearchAsync("\"\"\"");

        Assert.Empty(lines);
        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ListInstalledAsync_RoutesThroughRunner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("winget", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(0);
        var svc = new BulkInstallerService(runner);

        await svc.ListInstalledAsync();

        await runner.Received(1).RunProcessAsync(
            "winget",
            Arg.Is<string>(a => a.Contains("list")),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }
}
