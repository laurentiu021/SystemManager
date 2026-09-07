// SysManager · InfrastructureTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

public class AdminHelperTests
{
    [Fact]
    public void IsElevated_ReturnsBool_NoThrow()
    {
        var ex = Record.Exception(() => AdminHelper.IsElevated());
        Assert.Null(ex);
    }

    [Fact]
    public void IsElevated_RepeatedCalls_AreStable()
    {
        var a = AdminHelper.IsElevated();
        var b = AdminHelper.IsElevated();
        Assert.Equal(a, b);
    }
}

public class LogServiceTests
{
    [Fact]
    public void LogDir_IsUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, LogService.LogDir);
    }

    [Fact]
    public void LogDir_IncludesAppAndLogsSubfolders()
    {
        Assert.Contains("SysManager", LogService.LogDir);
        Assert.Contains("logs", LogService.LogDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Init_CreatesDirectory_AndLogsInit()
    {
        LogService.Init();
        try
        {
            Assert.True(Directory.Exists(LogService.LogDir));
            Assert.NotNull(LogService.Logger);
        }
        finally
        {
            LogService.Shutdown();
        }
    }

    [Fact]
    public void Shutdown_WithoutInit_IsSafe()
    {
        // Should not throw even if Init wasn't called in this test session.
        LogService.Shutdown();
    }
}

public class WingetServiceTests
{
    // We can test the parsing helper privately via reflection so we don't
    // have to invoke winget.exe for unit tests.
    private static List<AppPackage> Parse(List<string> lines)
    {
        var m = typeof(WingetService).GetMethod("ParseUpgradeTable", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<AppPackage>)m.Invoke(null, new object[] { lines })!;
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsEmptyList()
    {
        var result = Parse(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_WithoutHeader_ReturnsEmpty()
    {
        var result = Parse(new List<string> { "random", "lines", "no header" });
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SampleWingetTable_ReturnsPackages()
    {
        // A shortened but realistic winget upgrade table.
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Visual Studio Code           Microsoft.VisualStudioCode     1.94.0    1.95.0      winget",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "2 upgrades available."
        };
        var result = Parse(lines);
        Assert.Equal(2, result.Count);
        Assert.Equal("Microsoft.VisualStudioCode", result[0].Id);
        Assert.Equal("Git.Git", result[1].Id);
        Assert.All(result, p => Assert.Equal("winget", p.Source));
    }

    [Fact]
    public void Parse_SkipsBlankLines()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "",
            "Visual Studio Code           Microsoft.VisualStudioCode     1.94.0    1.95.0      winget",
            "",
        };
        var result = Parse(lines);
        Assert.Single(result);
    }

    [Fact]
    public void Parse_StopsAtSummaryLine()
    {
        var lines = new List<string>
        {
            "Name                         Id                             Version   Available   Source",
            "-----------------------------------------------------------------------------------------",
            "Git                          Git.Git                        2.47.0    2.48.0      winget",
            "1 upgrades available.",
            "This should be ignored       Fake.Pkg                       1.0.0     2.0.0       winget",
        };
        var result = Parse(lines);
        Assert.Single(result);
    }

    /// <summary>
    /// A line the runner emits reaches whoever subscribed through <see cref="WingetService"/>, and stops
    /// reaching them once they unsubscribe.
    /// </summary>
    /// <remarks>
    /// <c>WingetService.LineReceived</c> is a pass-through: its <c>add</c>/<c>remove</c> accessors forward
    /// straight to the runner's event, holding no list of their own. That is the whole behaviour worth
    /// pinning, because getting it wrong is silent — a service that swallowed the subscription would leave
    /// the App Updates console permanently blank while every other assertion stayed green.
    /// <para>This test asserted nothing at all before. It built a real <c>PowerShellRunner</c>, called
    /// <c>GetField</c> and discarded the result, then ended with <c>_ = gotLine;</c> under a comment saying
    /// the path "requires a running session". So it passed on every machine no matter what the service did.
    /// With the runner behind a seam there is no session to need: raise the far end and watch the near
    /// end.</para>
    /// </remarks>
    [Fact]
    public void WingetService_ForwardsRunnerLines_ToItsOwnSubscribers()
    {
        var runner = new RecordingRunner();
        var winget = new WingetService(runner);

        var received = new List<string>();
        void Sink(PowerShellLine line) => received.Add(line.Text);
        winget.LineReceived += Sink;

        runner.RaiseLine(PowerShellLine.Output("Git.Git  2.47.0  2.48.0  winget"));

        Assert.Equal(["Git.Git  2.47.0  2.48.0  winget"], received);

        // Removing must reach the runner too — a remove accessor that forgot to forward would leak every
        // subscriber for the life of the process.
        winget.LineReceived -= Sink;
        runner.RaiseLine(PowerShellLine.Output("this line has no subscriber left"));

        Assert.Single(received);
    }
}
