// SysManager · FileLockViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// File Lock Detector's Type column says what kind of program holds a file in plain words (#2813).
/// </summary>
/// <remarks>
/// The column was bound to the Restart Manager's own names, so it read "RmExplorer", and "RmCritical" for the one
/// process not to end. The model's words are proved in <c>FileLockerTests</c>; this loads the real XAML with the real
/// view model to prove the column shows them. The service is a fake that scans nothing and ends nothing.
/// </remarks>
[Collection("Network")]
public sealed class FileLockViewUiTests
{
    private sealed class NoScan : IFileLockService
    {
        public FileLockScan? FindLockers(string path) => throw new NotSupportedException("nothing here is scanned");

        public ProcessManagerService.KillOutcome KillProcess(int processId, DateTime? startTime) =>
            throw new NotSupportedException("nothing here is ended");
    }

    [Fact]
    public void TheTypeColumn_SaysWhatKindOfProgramHoldsTheFile_InPlainWords()
    {
        var vm = new FileLockViewModel(new NoScan());
        vm.Lockers.Add(new FileLocker(4321, "explorer.exe", "RmExplorer", null));
        vm.Lockers.Add(new FileLocker(4, "System", "RmCritical", null));
        vm.HasScanned = true;

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var view = new FileLockView { DataContext = vm };
            view.Measure(new Size(1200, 900));
            view.Arrange(new Rect(0, 0, 1200, 900));
            view.UpdateLayout();
            var shown = ShownTexts(view);

            Assert.Contains("File Explorer", shown);
            Assert.Contains("Needed by Windows", shown);
            Assert.DoesNotContain("RmExplorer", shown);
            Assert.DoesNotContain("RmCritical", shown);
        });
    }

    /// <summary>The words actually on screen under <paramref name="root"/>, in tree order.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is System.Windows.Controls.TextBlock { Text.Length: > 0 } block) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }
}
