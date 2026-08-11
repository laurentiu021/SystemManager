// SysManager · TestAssemblyInit
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.CompilerServices;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Redirects the process-wide singletons that write user data, once, before any test runs.
/// <para>
/// This exists because per-test discipline demonstrably does not hold. The activity log had a
/// <c>configDir</c> seam AND a test suite that used it correctly — and the real
/// <c>%LocalAppData%\SysManager\activity.json</c> still ended up holding twenty entries, all of them
/// DNS changes, six inside a single 60-millisecond window: <c>DnsHostsViewModelTests</c> driving
/// <c>ApplyDnsCommand</c> through a ViewModel that logs via <c>Instance</c>. At
/// <see cref="ActivityLogService.MaxEntries"/> that evicted the user's genuine history entirely. The
/// seam was never the missing piece — the singleton at the call site was (#1772).
/// </para>
/// <para>
/// A module initializer is the only placement a new test cannot forget. Adding a
/// <c>[Collection]</c> attribute or a <c>using</c> scope to each of the seventeen ViewModels that log
/// would work today and rot the first time someone writes the eighteenth. Scoped helpers
/// (<see cref="ActivityLogScope"/>, <c>DialogAnswer</c>) remain the right tool when a test needs to
/// ASSERT on what was written; this is the floor underneath them.
/// </para>
/// </summary>
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Not CreateTempSubdirectory: this directory outlives every test in the assembly and is
        // deliberately never cleaned up here — the run has no "after all tests" hook, and leaving a
        // few kilobytes in TEMP is strictly better than a test writing to the real profile.
        var dir = Path.Combine(
            Path.GetTempPath(), "SysManagerTestRun", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        ActivityLogService.Instance = new ActivityLogService(dir);
    }
}
