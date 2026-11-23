// SysManager · SystemFixServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SystemFixService"/> over a substituted runner: which folders the Windows Update reset is
/// given, and that it changes nothing when it does not know them. The script itself runs in the integration suite's
/// <c>SystemFixScriptTests</c>.
/// </summary>
/// <remarks>
/// The reset renamed the folders under whatever the SystemRoot environment variable named. A program running as the
/// user can set that variable, also for a SysManager started as administrator, so the folders it renamed, or the
/// ones it found nothing in and still called the reset done, were not necessarily Windows'.
/// </remarks>
public class SystemFixServiceTests
{
    private static IPowerShellRunner Runner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        return runner;
    }

    [Fact]
    public async Task ResetWindowsUpdate_RenamesTheFoldersUnderTheWindowsFolderItWasGiven()
    {
        var runner = Runner();
        using var service = new SystemFixService(runner, @"W:\Win", @"W:\Win\System32");

        var result = await service.ResetWindowsUpdateAsync();

        Assert.True(result.Success);
        await runner.Received(1).RunAsync(SystemFixService.ResetWindowsUpdateScript,
            Arg.Is<IDictionary<string, object?>?>(p => p != null && p.Count == 2
                && Equals(p["SoftwareDistribution"], @"W:\Win\SoftwareDistribution")
                && Equals(p["Catroot2"], @"W:\Win\System32\catroot2")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Without a full path for both folders the reset runs nothing, and says so, rather than renaming whatever a
    /// relative path resolves to from the current folder.
    /// </summary>
    [Theory]
    [InlineData(null, @"W:\Win\System32")]
    [InlineData("", @"W:\Win\System32")]
    [InlineData("Windows", @"W:\Win\System32")]
    [InlineData(@"W:\Win", null)]
    [InlineData(@"W:\Win", @"System32")]
    public async Task ResetWindowsUpdate_WithoutAFullWindowsFolder_ChangesNothingAndSaysWhy(
        string? windowsDirectory, string? systemDirectory)
    {
        var runner = Runner();
        using var service = new SystemFixService(runner, windowsDirectory, systemDirectory);

        var result = await service.ResetWindowsUpdateAsync();

        Assert.False(result.Success);
        Assert.True(result.NeedsReboot);
        Assert.Equal("SysManager could not find the Windows folder, so nothing was changed.", result.FailureReason);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public void ResetWindowsUpdateScript_TakesBothFoldersAsParameters_AndReadsNoEnvironment()
    {
        var script = SystemFixService.ResetWindowsUpdateScript;

        Assert.StartsWith("param([string]$SoftwareDistribution, [string]$Catroot2)", script, StringComparison.Ordinal);
        Assert.Contains("$sd = $SoftwareDistribution", script, StringComparison.Ordinal);
        Assert.Contains("$cr = $Catroot2", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheServiceTheAppBuilds_TakesBothFoldersFromSystem32()
    {
        using var service = new SystemFixService(Substitute.For<IPowerShellRunner>());

        Assert.Equal(Environment.SystemDirectory, service.SystemDirectory);
        Assert.Equal(Path.GetDirectoryName(Environment.SystemDirectory), service.WindowsDirectory);
    }

    [Fact]
    public async Task ReinstallWinGet_IsGivenNoParameters()
    {
        var runner = Runner();
        using var service = new SystemFixService(runner, @"W:\Win", @"W:\Win\System32");

        await service.ReinstallWinGetAsync();

        await runner.Received(1).RunAsync(SystemFixService.ReinstallWinGetScript,
            Arg.Is<IDictionary<string, object?>?>(p => p == null), Arg.Any<CancellationToken>());
    }
}
