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
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
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
        // which is the right default for an accidental click.
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => CloseChoice.MinimizeToTray,
            MessageBoxResult.No => CloseChoice.Exit,
            _ => CloseChoice.Cancel
        };
    }
}
