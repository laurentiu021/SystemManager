// SysManager · SystemFixResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Result of a single one-click system repair (Windows Update reset, network reset,
/// WinGet reinstall). Carries the captured output, whether a reboot is recommended and,
/// for a fix that stopped, why.
/// </summary>
/// <param name="FailureReason">
/// Why the fix stopped, in the script's or Windows' words, or null when it completed. It arrives as the run's
/// exception rather than on the output stream, so nothing else carries it to the tab.
/// </param>
public sealed record SystemFixResult(
    string FixName,
    bool Success,
    string Output,
    bool NeedsReboot,
    string? FailureReason = null);
