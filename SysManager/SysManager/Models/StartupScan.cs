// SysManager · StartupScan
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One Startup Manager scan: the entries found, and whether the scheduled tasks of other programs could be listed.
/// </summary>
/// <param name="Entries">Every startup entry found.</param>
/// <param name="ScheduledTasksListed">
/// False when Windows did not let SysManager read its scheduled-task cache, which it grants only to administrators.
/// <see cref="Entries"/> then holds no scheduled tasks at all, which is not the same as there being none.
/// </param>
public sealed record StartupScan(IReadOnlyList<StartupEntry> Entries, bool ScheduledTasksListed);
