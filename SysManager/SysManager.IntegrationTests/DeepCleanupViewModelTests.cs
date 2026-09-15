// SysManager · DeepCleanupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class DeepCleanupViewModelTests
{
    [Fact]
    public void Constructs()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.NotNull(vm);
    }

    [Fact]
    public void InitialSummary_IsNonEmpty()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.False(string.IsNullOrWhiteSpace(vm.ScanSummary));
    }

    [Fact]
    public void CleanSummary_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.Equal(string.Empty, vm.CleanSummary);
    }

    [Fact]
    public void Categories_StartsEmpty()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.Empty(vm.Categories);
    }

    [Fact]
    public void IsScanning_DefaultsFalse()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void IsCleaning_DefaultsFalse()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.False(vm.IsCleaning);
    }

    [Fact]
    public void TotalSelectedBytes_StartsZero()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.Equal(0, vm.TotalSelectedBytes);
    }

    [Fact]
    public void TotalSelectedDisplay_StartsWithZeroBytes()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.StartsWith("0", vm.TotalSelectedDisplay);
    }

    [Fact]
    public async Task ScanCommand_PopulatesCategories()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var task = vm.ScanCommand.ExecuteAsync(null);
        if (task is Task t) await t;
        Assert.True(vm.Categories.Count >= 10);
    }

    [Fact]
    public async Task ScanCommand_EnablesCleanButton_WhenPreSelectedCategoriesArrive()
    {
        // Regression (P2 #43): scanned categories arrive PRE-SELECTED (size>0 &&
        // !IsDestructiveHint), but ScanCoreAsync repopulates via Categories.ReplaceWith —
        // a collection Reset that raises no per-item PropertyChanged. CommunityToolkit's
        // RelayCommand only re-raises CanExecuteChanged via NotifyCanExecuteChanged(), so
        // before the fix the "Clean selected" button kept its initial DISABLED state after
        // the first scan and the user had to untick/retick a category to enable it. The fix
        // calls CleanCommand.NotifyCanExecuteChanged() right after ReplaceWith.
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        Assert.False(vm.CleanCommand.CanExecute(null)); // disabled on the empty pre-scan list

        var task = vm.ScanCommand.ExecuteAsync(null);
        if (task is Task t) await t;

        // A real scan always finds at least one non-destructive, non-empty category
        // (e.g. temp files), which arrives pre-selected — so Clean must now be enabled
        // without any manual toggle.
        if (vm.Categories.Any(c => c.IsSelected))
            Assert.True(vm.CleanCommand.CanExecute(null),
                "Clean button must be enabled after a scan yields pre-selected categories");
    }

    [Fact]
    public async Task ScanCommand_UpdatesScanSummary()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var before = vm.ScanSummary;
        var task = vm.ScanCommand.ExecuteAsync(null);
        if (task is Task t) await t;
        Assert.NotEqual(before, vm.ScanSummary);
    }

    [Fact]
    public async Task SelectAllCommand_True_SelectsNonDestructive()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var t = vm.ScanCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;

        vm.SelectAllCommand.Execute(true);
        // Windows.old must stay unselected.
        foreach (var c in vm.Categories)
        {
            if (c.IsDestructiveHint) Assert.False(c.IsSelected);
            else Assert.True(c.IsSelected);
        }
    }

    [Fact]
    public async Task SelectAllCommand_False_DeselectsEverything()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var t = vm.ScanCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        vm.SelectAllCommand.Execute(false);
        foreach (var c in vm.Categories) Assert.False(c.IsSelected);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void ScanLocation_Records_EquateByValue()
    {
        var a = new ScanLocation("A", "p");
        var b = new ScanLocation("A", "p");
        Assert.Equal(a, b);
    }


    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("CleanCommand")]
    [InlineData("SelectAllCommand")]
    [InlineData("CancelCommand")]
    public void CommandExists(string propertyName)
    {
        var vm = new DeepCleanupViewModel(new DeepCleanupService());
        var prop = vm.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

}
