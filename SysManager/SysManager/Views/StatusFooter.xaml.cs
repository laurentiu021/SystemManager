// SysManager · StatusFooter
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.Views;

/// <summary>
/// The scan-progress bar plus status line that every scan-capable tab ends with. Centralizes the idiom so
/// the progress colour, the accessibility wiring and the "Done" affordance can never drift between views.
/// </summary>
/// <remarks>
/// Bindings inside are unqualified: <see cref="ViewModels.ViewModelBase"/> owns
/// <c>IsProgressIndeterminate</c>, <c>IsBusy</c> and <c>StatusMessage</c>, so the control inherits the view's
/// DataContext and needs no plumbing. Place it where the copied <c>DockPanel</c> was, keeping that element's
/// <c>Grid.Row</c> and <c>Margin</c> on the call site — the two layout strategies disagree about the footer's
/// gutter, and the row index differs per view.
/// <para><see cref="ProgressName"/> has no default on purpose. Every view names its own bar — "Cleanup
/// progress", "Uninstall progress" — and a default would let a call site silently ship an unnamed progress
/// bar to a screen reader, which is the regression this control was most at risk of introducing.</para>
/// </remarks>
public partial class StatusFooter : UserControl
{
    public StatusFooter() => InitializeComponent();

    /// <summary>
    /// What a screen reader calls this tab's progress bar, e.g. "Cleanup progress". Required: an unnamed
    /// progress bar is worse than the duplication this control replaces.
    /// </summary>
    public static readonly DependencyProperty ProgressNameProperty =
        DependencyProperty.Register(nameof(ProgressName), typeof(string), typeof(StatusFooter),
            new PropertyMetadata(string.Empty));

    /// <inheritdoc cref="ProgressNameProperty"/>
    public string ProgressName
    {
        get => (string)GetValue(ProgressNameProperty);
        set => SetValue(ProgressNameProperty, value);
    }
}
