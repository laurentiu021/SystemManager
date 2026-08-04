// SysManager · IDialogService — abstraction for user confirmation dialogs
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// The user's answer to a three-way close prompt.
/// </summary>
public enum CloseChoice
{
    /// <summary>Dismiss the prompt and leave the window open.</summary>
    Cancel,

    /// <summary>Keep running in the notification area.</summary>
    MinimizeToTray,

    /// <summary>Exit the application.</summary>
    Exit
}

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

    /// <summary>
    /// Ask whether closing the window should exit the app or keep it in the notification
    /// area. Separate from <see cref="Confirm"/> because the choice is genuinely three-way:
    /// a Yes/No prompt cannot express "minimize", "exit" and "I clicked X by mistake"
    /// without making one of them the ambiguous default.
    /// </summary>
    CloseChoice AskCloseOrMinimize(string message, string title);
}
