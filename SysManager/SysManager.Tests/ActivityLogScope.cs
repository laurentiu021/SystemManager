// SysManager · ActivityLogScope
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Points <see cref="ActivityLogService.Instance"/> at a throwaway directory for the life of the
/// scope and restores the previous instance on dispose, so a test can drive a destructive operation
/// without appending to the developer's real activity history.
/// <para>
/// The restore matters for the same reason it does in <see cref="DialogAnswer"/>: the singleton is
/// process-wide static state, so a test that swapped it and threw would leak the temp instance into
/// every later test in the collection.
/// </para>
/// <para>
/// This exists because the seam alone was not enough. <see cref="ActivityLogService"/> already took a
/// <c>configDir</c>, but <c>Instance</c> was get-only, so the 20+ ViewModel call sites that log
/// through the singleton had no way to reach it. The evidence was in the file: a real
/// <c>activity.json</c> holding twenty entries, every one of them a DNS change, six inside a single
/// 60-millisecond window — <c>DnsHostsViewModelTests</c> driving <c>ApplyDnsCommand</c>, not a person.
/// At <see cref="ActivityLogService.MaxEntries"/> that had evicted the user's genuine history
/// entirely (#1772).
/// </para>
/// </summary>
public sealed class ActivityLogScope : IDisposable
{
    private readonly ActivityLogService _previous;
    private readonly DirectoryInfo _dir;

    public ActivityLogScope()
    {
        _previous = ActivityLogService.Instance;
        _dir = Directory.CreateTempSubdirectory("ActivityLog_");
        ActivityLogService.Instance = new ActivityLogService(_dir.FullName);
    }

    /// <summary>The redirected store, so a test can assert on what was written.</summary>
    public string Path => _dir.FullName;

    public void Dispose()
    {
        ActivityLogService.Instance = _previous;
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleanup only. A leftover temp directory must never fail a passing test.
        }
    }
}
