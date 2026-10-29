// SysManager · IActivityLog — abstraction for ActivityLogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Abstraction for <see cref="ActivityLogService"/>, so a view model can take it through its
/// constructor instead of reaching for <see cref="ActivityLogService.Instance"/> directly (#2860).
/// </summary>
public interface IActivityLog
{
    /// <inheritdoc cref="ActivityLogService.GetRecent"/>
    IReadOnlyList<ActivityEntry> GetRecent(int count = 5);

    /// <inheritdoc cref="ActivityLogService.Log"/>
    void Log(string action, string detail);
}
