// SysManager · FormatHelper — shared formatting utilities
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Shared formatting methods used across multiple ViewModels.
/// Eliminates duplication of common formatting logic.
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// Formats a byte count into a human-readable string (B, KB, MB, GB).
    /// </summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B"
    };
}
