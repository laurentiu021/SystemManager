// SysManager · FolderPathBoxUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// A folder typed into Disk Analyzer's and Duplicate Finder's path box reaches the view model, and the box waits for a
/// scan that is running (#2731).
/// </summary>
/// <remarks>
/// Both boxes were editable and bound only through <c>SelectedItem</c>, so a typed folder that matched no preset set it
/// to null while the text stayed on screen, and Analyze or Scan then did nothing. Only the real XAML shows which
/// property the box writes, so this loads it on the STA thread with the app's dictionaries. Nothing scans a disk.
/// </remarks>
public sealed class FolderPathBoxUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private static T Laid<T>(T view) where T : FrameworkElement
    {
        view.Measure(new Size(1200, 900));
        view.Arrange(new Rect(0, 0, 1200, 900));
        view.UpdateLayout();
        return view;
    }

    private static ComboBox BoxNamed(DependencyObject view, string name) =>
        Assert.Single(Descendants<ComboBox>(view), c => AutomationProperties.GetName(c) == name);

    /// <summary>Types into the box and leaves it, which is when the box hands its text over.</summary>
    private static void Type(ComboBox box, string text)
    {
        box.Text = text;
        var typed = BindingOperations.GetBindingExpression(box, ComboBox.TextProperty);
        Assert.True(typed is not null, "the box's text is bound to nothing, so what is typed into it goes nowhere");
        typed.UpdateSource();
    }

    [Fact]
    public async Task DiskAnalyzer_AFolderTypedIntoTheBox_ReachesTheViewModel_AndTheBoxWaitsForAnAnalysis()
    {
        var vm = new DiskAnalyzerViewModel(new DiskAnalyzerService(),
            new DiskScanHistoryService(Path.Combine(_dir, "history")),
            new DiskAnalyzerPreferenceService(Path.Combine(_dir, "preferences")));
        await vm.InitializationComplete;

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var box = BoxNamed(Laid(new DiskAnalyzerView { DataContext = vm }), "Folder path to analyze");

            Type(box, @"D:\Not a preset");

            Assert.Equal(@"D:\Not a preset", vm.SelectedPath);
            Assert.True(box.IsEnabled);
            vm.IsBusy = true;
            Assert.False(box.IsEnabled);
        });
    }

    [Fact]
    public async Task DuplicateFinder_AFolderTypedIntoTheBox_ReachesTheViewModel_AndTheBoxWaitsForAScan()
    {
        var vm = new DuplicateFileViewModel(new DuplicateFileService());
        await vm.InitializationComplete;

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var box = BoxNamed(Laid(new DuplicateFileView { DataContext = vm }), "Folder to scan");

            Type(box, @"D:\Not a preset");

            Assert.Equal(@"D:\Not a preset", vm.SelectedFolder);
            Assert.True(box.IsEnabled);
            vm.IsBusy = true;
            Assert.False(box.IsEnabled);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
