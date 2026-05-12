// SysManager · IDialogService — abstraction for user confirmation dialogs
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// Abstraction for user confirmation dialogs. Enables unit testing of
/// ViewModels that require user interaction without coupling to WPF MessageBox.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Show a Yes/No confirmation dialog. Returns true if user clicked Yes.
    /// </summary>
    bool Confirm(string message, string title);
}
