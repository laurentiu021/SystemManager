// SysManager · PerformanceView — performance mode UI
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.ViewModels;

namespace SysManager.Views;

public partial class PerformanceView : UserControl
{
    private System.ComponentModel.PropertyChangedEventHandler? _propertyHandler;

    public PerformanceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // MEM-004: unsubscribe from previous VM to prevent leak
        if (e.OldValue is PerformanceViewModel oldVm && _propertyHandler != null)
            oldVm.PropertyChanged -= _propertyHandler;

        if (e.NewValue is PerformanceViewModel vm)
        {
            _propertyHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(PerformanceViewModel.SelectedPlan))
                    SyncRadioButtons(vm.SelectedPlan);
            };
            vm.PropertyChanged += _propertyHandler;
            SyncRadioButtons(vm.SelectedPlan);
        }
    }

    private void SyncRadioButtons(string plan)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SyncRadioButtons(plan));
            return;
        }
        RbBalanced.IsChecked = plan == "balanced";
        RbHigh.IsChecked = plan == "high";
        RbUltimate.IsChecked = plan == "ultimate";
    }

    private void PowerPlan_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && DataContext is PerformanceViewModel vm)
            vm.SelectedPlan = rb.Tag?.ToString() ?? "balanced";
    }
}
