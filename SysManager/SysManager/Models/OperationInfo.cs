// SysManager · OperationInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Information about a currently running operation.
/// </summary>
public sealed record OperationInfo(string Name, DateTime StartedUtc);
