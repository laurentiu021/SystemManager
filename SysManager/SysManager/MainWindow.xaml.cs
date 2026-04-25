// SysManager — MainWindow shell
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using System.Windows.Input;
using SysManager.ViewModels;

namespace SysManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Click on a single-item group (Dashboard, Network).</summary>
    private void SingleGroup_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }

    /// <summary>Click on a child item inside an expanded group.</summary>
    private void NavChild_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is NavItem item
            && DataContext is MainWindowViewModel vm)
            vm.SelectedNav = item;
    }
}
