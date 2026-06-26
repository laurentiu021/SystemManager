// SysManager · RunningProcess
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A running process the user can target for CPU affinity. <see cref="AffinityMask"/>
/// is the current processor-affinity bitmask (bit i = logical CPU i), or 0 if it
/// couldn't be read (e.g. access denied).
/// </summary>
public sealed record RunningProcess(int ProcessId, string Name, long AffinityMask)
{
    public string Display => $"{Name} ({ProcessId})";
}
