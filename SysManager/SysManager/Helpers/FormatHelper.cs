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
    /// Formats a byte count into a human-readable string (B, KB, MB, GB, TB).
    /// </summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1L << 10 => $"{bytes} B",
        < 1L << 20 => $"{bytes / (double)(1L << 10):F1} KB",
        < 1L << 30 => $"{bytes / (double)(1L << 20):F1} MB",
        < 1L << 40 => $"{bytes / (double)(1L << 30):F1} GB",
        _ => $"{bytes / (double)(1L << 40):F1} TB"
    };
}
