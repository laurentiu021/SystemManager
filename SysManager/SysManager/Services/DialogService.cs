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
    public static IDialogService Instance { get; set; } = new DialogService();

    /// <inheritdoc/>
    public bool Confirm(string message, string title)
    {
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }
}
