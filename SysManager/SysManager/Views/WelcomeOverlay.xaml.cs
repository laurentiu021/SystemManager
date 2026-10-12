// SysManager · WelcomeOverlay
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;
using System.Windows.Threading;

namespace SysManager.Views;

/// <summary>
/// The one-time Welcome screen over the window (#1635). Its state and its three ways out live in
/// <see cref="ViewModels.WelcomeViewModel"/>; this only puts keyboard focus where the screen starts.
/// </summary>
public partial class WelcomeOverlay : UserControl
{
    public WelcomeOverlay()
    {
        InitializeComponent();

        // Focus starts on "Check my PC's health", so Enter takes the suggested first step and Tab walks the other two.
        // Without this it stayed on the window, behind a page that is disabled while this is up, and the first Tab
        // had to find the card. Posted at Input priority because the screen becomes visible during layout, before
        // the button inside it can take focus. IsVisible rather than Loaded: closing the window to the notification
        // area while the screen is up hides it with the window, and reopening the window shows it again, which is
        // when focus has to come back to it.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => CheckHealthButton.Focus()));
        };
    }
}
