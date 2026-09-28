// SysManager · MemoryTestServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics.Eventing.Reader;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The memory-error scan's failure path: a log that cannot be read is an error, not a clean 30 days.
/// </summary>
/// <remarks>
/// The scan that succeeds is covered in the integration project, against the real System log. This half needs no
/// such log, because it names one that does not exist.
/// </remarks>
public class MemoryTestServiceTests
{
    /// <summary>
    /// A log name no machine has, which is the one way to make the real reader fail on demand. The Dashboard and
    /// System Health tests use it too, to reach the same failure through each of them.
    /// </summary>
    internal const string NoSuchLog = "SysManagerTestsNoSuchLog";

    [Fact]
    public async Task CheckErrorLogs_WhenTheLogCannotBeRead_ThrowsInsteadOfReportingNoErrors()
    {
        // #2479. The scan caught this itself and returned zero errors, so the Dashboard and System Health both said
        // "No memory errors" in green, and the could-not-check branch each of them has could never run.
        var svc = new MemoryTestService(NoSuchLog);

        await Assert.ThrowsAnyAsync<EventLogException>(() => svc.CheckErrorLogsAsync());
    }
}
