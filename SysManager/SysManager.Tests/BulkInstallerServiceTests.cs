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
}
