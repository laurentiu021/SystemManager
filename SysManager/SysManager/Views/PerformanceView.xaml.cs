// SysManager · PerformanceView — performance mode UI
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.ViewModels;

namespace SysManager.Views;

public partial class PerformanceView : UserControl
{
    public PerformanceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PerformanceViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PerformanceViewModel.SelectedPlan))
                    SyncRadioButtons(vm.SelectedPlan);
            };
            SyncRadioButtons(vm.SelectedPlan);
        }
    }

    private void SyncRadioButtons(string plan)
    {
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
