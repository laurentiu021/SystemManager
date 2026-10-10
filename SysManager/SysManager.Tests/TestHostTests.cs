// SysManager · TestHostTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Xml.Linq;

namespace SysManager.Tests;

/// <summary>
/// How the test processes themselves are set up, where that decides whether a run ends when its last test does.
/// </summary>
/// <remarks>
/// #2671. The STA tests build WPF objects on threads that end with the test, each leaving a <c>Dispatcher</c> that was
/// never shut down and a weak-event table registered for the process's exit. At exit WPF asked each table's ended
/// thread to clear it and waited 300 ms for an answer that could not come, one table after another: the unit suite
/// took 6.8 s to exit after its last test and <c>RebuildFocusTests</c> alone 2.8 s, and past the xUnit console
/// runner's 10 s the process was forced out with exit code 1 although every test had passed. WPF's
/// <c>Switch.MS.Internal.DoNotInvokeInWeakEventTableShutdownListener</c> clears each table where it is instead, and
/// with it the suite exits 0.2 s after its last test.
/// </remarks>
public class TestHostTests
{
    private const string WeakEventSwitch = "Switch.MS.Internal.DoNotInvokeInWeakEventTableShutdownListener";

    [Fact]
    public void ThisProcess_ClearsWeakEventTablesAtExitWithoutWaitingOnEndedThreads() =>
        Assert.True(AppContext.TryGetSwitch(WeakEventSwitch, out var on) && on,
            $"{WeakEventSwitch} is not on in the unit test process, so at exit WPF waits 300 ms on each weak-event "
            + "table whose STA test thread has ended, and past 10 s the console runner exits with code 1.");

    /// <summary>
    /// Every test project gets the switch from the one place that sets it, and the app does not.
    /// </summary>
    /// <remarks>
    /// The integration suite builds views on STA threads as the unit suite does, so it needs the switch as much, and
    /// only this process can be asked directly. So the setting is read where it is made: once, in
    /// <c>Directory.Build.props</c>, for a project that sets <c>IsTestProject</c>, which every test project does and
    /// the app does not.
    /// </remarks>
    [Fact]
    public void EveryTestProject_GetsTheSwitchFromOnePlace_AndTheAppDoesNot()
    {
        var solution = TestPaths.SolutionDir();
        var props = XDocument.Load(Path.Combine(solution, "Directory.Build.props"));
        var option = Assert.Single(props.Descendants("RuntimeHostConfigurationOption"),
            e => (string?)e.Attribute("Include") == WeakEventSwitch);
        Assert.Equal("true", (string?)option.Attribute("Value"));
        Assert.Equal("'$(IsTestProject)' == 'true'", (string?)option.Parent?.Attribute("Condition"));

        var projects = Directory.GetFiles(solution, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => XDocument.Load(p));
        Assert.True(projects.Count >= 4, $"only {projects.Count} projects were found under {solution}.");

        foreach (var (name, project) in projects)
        {
            var isTest = project.Descendants("IsTestProject").Any(e => e.Value.Trim() == "true");
            Assert.True(isTest == name.EndsWith("Tests", StringComparison.Ordinal),
                $"{name} {(isTest ? "sets" : "does not set")} IsTestProject, so it "
                + $"{(isTest ? "gets" : "misses")} {WeakEventSwitch}.");
            Assert.DoesNotContain(project.Descendants("RuntimeHostConfigurationOption"),
                e => (string?)e.Attribute("Include") == WeakEventSwitch);
        }
    }
}
