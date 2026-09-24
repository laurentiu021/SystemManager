// SysManager · ConnectionBandwidthSourceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// How <see cref="ConnectionBandwidthSource"/> — the Bandwidth Monitor's default, no-admin mode — names a
/// process, which has to match the name precise mode gives the same process.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="BandwidthAggregationTests"/>, which is pure: these read the test process's own
/// name from Windows, a lightweight OS call the unit suite allows.
/// </remarks>
public class ConnectionBandwidthSourceTests
{
    /// <summary>
    /// A process is named the way the rest of the app names it: <c>Process.ProcessName</c>, no extension.
    /// </summary>
    /// <remarks>
    /// This appended ".exe", so connection mode said "chrome.exe" where precise mode — TraceEvent's image name
    /// without the extension — said "chrome", and turning precise rates on relabelled every row. It also made the
    /// kernel's own row "System.exe", a file that does not exist, which then missed both the path search and the
    /// Windows-process list and fell to the generic icon (#2426). The test process names itself, so the expected
    /// value is whatever Windows reports rather than a fixture that could drift from it.
    /// </remarks>
    [Fact]
    public void ResolveName_UsesTheProcessNameTheRestOfTheAppShows()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        var name = ConnectionBandwidthSource.ResolveName(self.Id, new Dictionary<int, string>());

        Assert.Equal(self.ProcessName, name);
        Assert.False(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), name);
    }

    /// <summary>
    /// A PID that is not running gets the app's own wording, which precise mode now uses too.
    /// </summary>
    /// <remarks>
    /// <see cref="int.MaxValue"/> is not a live PID in practice: Windows hands out process ids in multiples of
    /// four, and this one is odd.
    /// </remarks>
    [Fact]
    public void ResolveName_ForAPidThatIsNotRunning_SaysPid()
    {
        Assert.Equal($"PID {int.MaxValue}",
            ConnectionBandwidthSource.ResolveName(int.MaxValue, new Dictionary<int, string>()));
    }
}
