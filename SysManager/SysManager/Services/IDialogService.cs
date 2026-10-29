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

    /// <summary>
    /// Show a save-file dialog suggesting <paramref name="suggestedFileName"/>, filtered by
    /// <paramref name="filter"/>. Returns the chosen path, or null if the user cancelled.
    /// </summary>
    /// <remarks>
    /// One of three picker methods added so the 23 view-model commands that used to construct a
    /// <c>SaveFileDialog</c>, <c>OpenFileDialog</c> or <c>OpenFolderDialog</c> directly can be exercised
    /// in a test by substituting this interface instead (#2860).
    /// </remarks>
    string? PickSaveFile(string suggestedFileName, string filter);

    /// <summary>
    /// Show an open-file dialog titled <paramref name="title"/> and filtered by <paramref name="filter"/>.
    /// Returns the chosen paths — more than one only when <paramref name="multiselect"/> is true — or an
    /// empty list if the user cancelled.
    /// </summary>
    IReadOnlyList<string> PickOpenFiles(string title, string filter, bool multiselect = false, bool checkFileExists = true);

    /// <summary>
    /// Show a folder picker titled <paramref name="title"/>. Returns the chosen folder, or null if the
    /// user cancelled.
    /// </summary>
    string? PickFolder(string title);
}
