// SysManager · SystemReportViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="SystemReportViewModel"/>'s three export commands: the picker each shares through
/// <see cref="IDialogService"/> (#2860), not only the generated content.
/// </summary>
public class SystemReportViewModelTests
{
    private static SystemReportViewModel NewVm(IDialogService? dialog = null) =>
        new(new SystemReportService(new SystemInfoService(), new DiskHealthService(), new BatteryService(), new EventLogService()), dialog);

    [Fact]
    public async Task ExportTextAsync_WhenCancelled_WritesNothing()
    {
        var dialog = Substitute.For<IDialogService>();
        dialog.PickSaveFile(Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);
        var vm = NewVm(dialog);
        var before = vm.StatusMessage;

        await vm.ExportTextCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ExportTextAsync_WhenAPathIsPicked_WritesTheReportThere()
    {
        var path = Path.Combine(Path.GetTempPath(), "smtest_systemreport_" + Guid.NewGuid().ToString("N") + ".txt");
        var dialog = Substitute.For<IDialogService>();
        dialog.PickSaveFile(Arg.Any<string>(), Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.ExportTextCommand.ExecuteAsync(null);

            Assert.True(File.Exists(path));
            Assert.Contains(Path.GetFileName(path), vm.StatusMessage, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportHtmlAsync_WhenAPathIsPicked_WritesTheReportThere()
    {
        var path = Path.Combine(Path.GetTempPath(), "smtest_systemreport_" + Guid.NewGuid().ToString("N") + ".html");
        var dialog = Substitute.For<IDialogService>();
        dialog.PickSaveFile(Arg.Any<string>(), Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.ExportHtmlCommand.ExecuteAsync(null);

            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportJsonAsync_WhenAPathIsPicked_WritesTheReportThere()
    {
        var path = Path.Combine(Path.GetTempPath(), "smtest_systemreport_" + Guid.NewGuid().ToString("N") + ".json");
        var dialog = Substitute.For<IDialogService>();
        dialog.PickSaveFile(Arg.Any<string>(), Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.ExportJsonCommand.ExecuteAsync(null);

            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
