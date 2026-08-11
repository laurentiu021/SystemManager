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

    /// <summary>
    /// Show a message with nothing to decide — one dismiss button, no return value.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Confirm"/> for the same reason <see cref="AskCloseOrMinimize"/> is: the
    /// shape of the dialog should match the shape of the decision. Using Confirm for a pure notice
    /// presented "this process cannot be safely ended" as a Yes/No question and then discarded the
    /// answer, so the user chose between two buttons that did the same thing. That is how people learn
    /// to click through dialogs without reading them, which quietly erodes the confirmations that DO
    /// gate something destructive.
    /// </remarks>
    void Inform(string message, string title);
}
