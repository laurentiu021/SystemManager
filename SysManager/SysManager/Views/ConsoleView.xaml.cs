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
            ListBox_Output.ScrollIntoView(ListBox_Output.Items[ListBox_Output.Items.Count - 1]);
        }));
    }
}
