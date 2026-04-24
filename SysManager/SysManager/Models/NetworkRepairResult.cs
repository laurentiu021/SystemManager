// SysManager · NetworkRepairResult
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Result of a single network repair operation (DNS flush, Winsock reset, TCP/IP reset).
/// </summary>
public sealed record NetworkRepairResult(
    string ToolName,
    bool Success,
    string Output,
    bool NeedsReboot);
