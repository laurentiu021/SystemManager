// SysManager · LegacyPanelsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Legacy Panels opens each classic applet from Windows' own folder, opens nothing else, and says what happened.
/// </summary>
/// <remarks>
/// No test reached the tab (#2741). The applets are never opened here: the start goes to a recorder.
/// </remarks>
public class LegacyPanelsViewModelTests
{
    private const string Prompt = "Click any panel to open the classic Windows applet.";

    private readonly List<ProcessStartInfo> _started = [];

    private LegacyPanelsViewModel NewVm(Action<ProcessStartInfo>? start = null) =>
        new(new LegacyPanelService(start ?? _started.Add));

    [Fact]
    public void BeforeAnythingIsOpened_TheLineSaysWhatToDo() => Assert.Equal(Prompt, NewVm().StatusMessage);

    [Fact]
    public void OpeningAPanel_StartsItsAppletFromWindowsOwnFolder_AndSaysSo()
    {
        var vm = NewVm();
        var sound = vm.Panels.Single(p => p.Name == "Sound");

        vm.OpenCommand.Execute(sound);

        var started = Assert.Single(_started);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "control.exe"), started.FileName, ignoreCase: true);
        Assert.Equal("mmsys.cpl", started.Arguments);
        Assert.True(started.UseShellExecute);
        Assert.Equal("Opened Sound.", vm.StatusMessage);
    }

    [Fact]
    public void EveryPanel_StartsAProgramFromWindowsOwnFolder()
    {
        // A bare name would be looked up through App Paths and PATH, which a program can change without elevation.
        var vm = NewVm();

        foreach (var panel in vm.Panels) vm.OpenCommand.Execute(panel);

        Assert.Equal(vm.Panels.Count, _started.Count);
        Assert.All(_started, s => Assert.StartsWith(Environment.SystemDirectory + Path.DirectorySeparatorChar, s.FileName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void APanelThatDoesNotStart_SaysItCouldNotBeOpened()
    {
        var vm = NewVm(_ => throw new Win32Exception(2, "The system cannot find the file specified."));
        var panel = vm.Panels[0];

        vm.OpenCommand.Execute(panel);

        Assert.Equal($"Couldn't open {panel.Name} — it may not be available on this edition of Windows.", vm.StatusMessage);
    }

    [Fact]
    public void APanelNotInTheCatalog_IsNotStarted()
    {
        var vm = NewVm();
        var outsider = new LegacyPanel("Command Prompt", "Not one of the panels.", "\uE756", "cmd.exe", "/c whoami");

        vm.OpenCommand.Execute(outsider);

        Assert.Empty(_started);
        Assert.StartsWith("Couldn't open Command Prompt", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPanel_StartsNothing_AndLeavesTheLineAlone()
    {
        var vm = NewVm();

        vm.OpenCommand.Execute(null);

        Assert.Empty(_started);
        Assert.Equal(Prompt, vm.StatusMessage);
    }
}
