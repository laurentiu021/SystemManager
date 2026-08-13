// SysManager · DialogService — WPF MessageBox implementation of IDialogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.Services;

/// <summary>
/// Production implementation of <see cref="IDialogService"/> using WPF MessageBox.
/// Access via <see cref="Instance"/> singleton or inject via constructor.
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>Shared singleton instance for ViewModels without DI.</summary>
    private static volatile IDialogService _instance = new DialogService();
    public static IDialogService Instance
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <inheritdoc/>
    public bool Confirm(string message, string title)
    {
        if (Application.Current == null) return false;

        // MessageBoxResult.No is the DEFAULT, i.e. the focused button. Without it WPF focuses Yes, so a
        // stray Enter — or a second Enter after the keypress that opened the dialog — approves whatever
        // is being confirmed. Every destructive operation in the app funnels through here (file
        // shredding, ending a process, debloat, uninstall), so the safe default belongs in this one
        // place rather than at each of the call sites. A user who means Yes still only presses one key.
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
                                     MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <inheritdoc/>
    public CloseChoice AskCloseOrMinimize(string message, string title)
    {
        // No UI available (unit tests, shutdown) — report Cancel rather than silently
        // choosing an action on the user's behalf.
        if (Application.Current == null) return CloseChoice.Cancel;

        // Yes = keep running in the notification area, No = exit, Cancel = stay open.
        // The caller's message states which option is which, so the mapping is explicit
        // on screen. Cancel is also what Esc and the dialog's own close button produce,
        // which is the right default for an accidental click — and passing it as the
        // defaultResult is what actually makes that true of the Enter key too. Without it
        // WPF focuses Yes, so Enter would silently choose "hide to the notification area"
        // when the user meant to dismiss the question.
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                                     MessageBoxResult.Cancel);
        return result switch
        {
            MessageBoxResult.Yes => CloseChoice.MinimizeToTray,
            MessageBoxResult.No => CloseChoice.Exit,
            _ => CloseChoice.Cancel
        };
    }

    /// <inheritdoc/>
    public void Inform(string message, string title)
    {
        // One OK button and an information glyph, not the Question glyph a Yes/No prompt gets: nothing
        // here is being asked. Same no-UI guard as the others (unit tests, shutdown) — there is nothing
        // to return, so it simply does not show.
        if (Application.Current == null) return;
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
