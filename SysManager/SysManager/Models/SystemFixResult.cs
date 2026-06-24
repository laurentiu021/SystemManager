// SysManager · SystemFixResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Result of a single one-click system repair (Windows Update reset, network reset,
/// WinGet reinstall). Carries the captured output and whether a reboot is recommended.
/// </summary>
public sealed record SystemFixResult(
    string FixName,
    bool Success,
    string Output,
    bool NeedsReboot);
