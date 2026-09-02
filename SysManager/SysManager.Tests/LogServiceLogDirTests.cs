// SysManager · LogServiceLogDirTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The log directory has to be redirectable, which is what took <c>LogService</c> off the user-data-path
/// ratchet in <c>ArchitectureTests.Services_DoNotHoldUserDataPathsInStaticFields</c>.
/// <para><c>LogDir</c> was <c>static readonly</c>. A resolved path in static state cannot be pointed at a
/// temp directory by any test, because <see cref="Environment.GetFolderPath"/> resolves through the Win32
/// known-folder function and ignores the <c>LOCALAPPDATA</c> environment variable. That is not a
/// hypothetical risk: a service holding its path this way had tests that wrote into the user's real
/// speed-test history.</para>
/// <para>The usual fix — a constructor-injected <c>string? configDir = null</c> — does not apply, because
/// <c>LogService</c> is a static class: Serilog's sink is configured once per process, so there is no
/// instance to hang a parameter on. The seam is <c>Init(string?)</c>, and the decision it makes lives in
/// <c>ResolveLogDir</c>.</para>
/// <para>These test the resolver, never <c>Init</c> itself. Calling <c>Init</c> would build a real Serilog
/// sink and assign the global <c>Log.Logger</c>, which every other test in the run shares.</para>
/// </summary>
public class LogServiceLogDirTests
{
    [Fact]
    public void ResolveLogDir_WithNothingRequested_KeepsThePerUserDefault()
    {
        var resolved = LogService.ResolveLogDir(null, loggerExists: false);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager", "logs");
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLogDir_WithABlankRequest_KeepsThePerUserDefault(string requested)
    {
        // Blank is not a redirect. Taking it as one would point the log at the process directory, which is
        // Program Files for an installed copy and therefore unwritable.
        var resolved = LogService.ResolveLogDir(requested, loggerExists: false);

        Assert.Contains("SysManager", resolved, StringComparison.Ordinal);
        Assert.EndsWith("logs", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLogDir_BeforeTheSinkExists_TakesTheRequestedDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "smlog_" + Guid.NewGuid().ToString("N"));

        Assert.Equal(temp, LogService.ResolveLogDir(temp, loggerExists: false));
    }

    [Fact]
    public void ResolveLogDir_AfterTheSinkExists_RefusesRatherThanIgnoring()
    {
        // Refused, not ignored. Ignoring it would leave LogDir naming one directory while the sink wrote to
        // another, so the crash dialog and the About tab would both send a user to an empty folder.
        var temp = Path.Combine(Path.GetTempPath(), "smlog_" + Guid.NewGuid().ToString("N"));

        var thrown = Assert.Throws<InvalidOperationException>(
            () => LogService.ResolveLogDir(temp, loggerExists: true));
        Assert.Contains("cannot be changed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLogDir_WithNothingRequested_IsAllowedEvenAfterTheSinkExists()
    {
        // The app's own two entry points pass nothing, and they are mutually exclusive — the update-applier
        // branch returns before the normal path — so Init runs once per process. Refusing a no-op call
        // would break startup rather than protect anything, so only an actual redirect is refused.
        Assert.Equal(LogService.ResolveLogDir(null, loggerExists: false),
                     LogService.ResolveLogDir(null, loggerExists: true));
    }
}
