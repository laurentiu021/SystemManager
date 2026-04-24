// SysManager · ConsoleView.xaml
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.Specialized;
using System.Windows.Controls;
using SysManager.ViewModels;

namespace SysManager.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();

        // Prevent auto-scroll from bubbling BringIntoView to the parent
        // ScrollViewer, which would cause the entire page to jump down.
        ListBox_Output.AddHandler(
            System.Windows.FrameworkElement.RequestBringIntoViewEvent,
            new System.Windows.RequestBringIntoViewEventHandler((s, e) => e.Handled = true));

        DataContextChanged += (_, __) =>
        {
            if (DataContext is ConsoleViewModel vm)
            {
                vm.Lines.CollectionChanged += OnLinesChanged;
            }
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm || !vm.AutoScroll) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (ListBox_Output.Items.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Scroll the internal ScrollViewer directly instead of using
            // ScrollIntoView, which fires RequestBringIntoView and causes
            // the parent page ScrollViewer to jump to the bottom (#93).
            var sv = FindVisualChild<ScrollViewer>(ListBox_Output);
            sv?.ScrollToEnd();
        }));
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
