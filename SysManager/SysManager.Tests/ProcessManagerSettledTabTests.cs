// SysManager · ProcessManagerSettledTabTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// One Process Manager tab whose first load has finished and whose once-a-second refresh is parked, shared by the
/// tests that need its list to hold still.
/// </summary>
/// <remarks>
/// The tab loads the real process list when it is built and then reconciles it into <c>Processes</c> every second.
/// A test that adds a row and looks for it again must not share that collection with the loop, so the first load
/// is awaited and the loop parked through <c>IsActive</c>. That first load reads every running process and its
/// icon, about six seconds on the machine that measured it, which is why it happens once for the class rather than
/// once per test.
/// <para>The call that ends a process is injected and forwards to <see cref="Kill"/>, which each test sets, so
/// nothing real is ever ended.</para>
/// </remarks>
public sealed class SettledProcessTab : IAsyncLifetime
{
    /// <summary>What the tab's kill call does. Each test that confirms a kill sets it first.</summary>
    public Func<int, DateTime, string?, ProcessManagerService.KillOutcome> Kill { get; set; } =
        (_, _, _) => throw new InvalidOperationException("a test confirmed a kill without saying what the kill does");

    /// <summary>The tab. Its first load has finished and its automatic refresh is parked.</summary>
    public ProcessManagerViewModel Vm { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Vm = new ProcessManagerViewModel(new ProcessManagerService(), (pid, start, name) => Kill(pid, start, name));
        await Vm.InitializationComplete;
        Vm.IsActive = false;
    }

    public ValueTask DisposeAsync()
    {
        Vm.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// What Process Manager does with its list and its status line once a kill is confirmed, and when it refreshes
/// (#2498, #2507).
/// </summary>
// Serialized: the kill tests swap the static DialogService.Instance, which is process-wide shared state.
[Collection("ProcessWideStatics")]
public class ProcessManagerSettledTabTests(SettledProcessTab tab) : IClassFixture<SettledProcessTab>
{
    private ProcessEntry Listed()
    {
        var entry = new ProcessEntry
        {
            Pid = 4242,
            Name = "SomeApp.exe",
            SafetyLevel = nameof(ProcessSafety.Unknown),
            StartTime = new DateTime(2026, 9, 28, 9, 30, 0),
        };
        tab.Vm.Processes.Add(entry);
        tab.Vm.FilteredProcesses.Add(entry);
        return entry;
    }

    [Fact]
    public void ConfirmedKill_EndsTheListedProcessByIdStartTimeAndName_AndDropsItsRow()
    {
        // The start time and the name travel with the ID: the prompt can stay open while the process exits and Windows
        // gives its ID to another one, and the ID alone would then end that one. The name is what still tells them
        // apart where Windows gives no start time (#2719).
        (int Pid, DateTime Start, string? Name)? asked = null;
        tab.Kill = (pid, start, name) =>
        {
            asked = (pid, start, name);
            return ProcessManagerService.KillOutcome.Ended;
        };
        var entry = Listed();
        using var dialog = new DialogAnswer(confirm: true);

        tab.Vm.KillProcessCommand.Execute(entry);

        Assert.Equal((entry.Pid, entry.StartTime, entry.Name), asked);
        Assert.DoesNotContain(entry, tab.Vm.Processes);
        Assert.DoesNotContain(entry, tab.Vm.FilteredProcesses);
        Assert.Equal("Killed SomeApp.exe (PID 4242).", tab.Vm.StatusMessage);
    }

    [Fact]
    public void RefusedKill_KeepsTheRow()
    {
        tab.Kill = (_, _, _) => ProcessManagerService.KillOutcome.Refused;
        var entry = Listed();
        using var dialog = new DialogAnswer(confirm: true);

        try
        {
            tab.Vm.KillProcessCommand.Execute(entry);

            Assert.Contains(entry, tab.Vm.Processes);
            Assert.StartsWith("Could not kill SomeApp.exe", tab.Vm.StatusMessage);
        }
        finally
        {
            tab.Vm.Processes.Remove(entry);
            tab.Vm.FilteredProcesses.Remove(entry);
        }
    }

    [Fact]
    public void KillOfSysManagersOwnRow_IsRefusedBeforeTheConfirmation()
    {
        // Ending only the process would now succeed on SysManager's own row, and stop it mid-step. The refusal
        // comes before the prompt, so the user is never asked to approve something that is then declined. Here
        // rather than with the other refusals because it asserts on the status line, which the tab's first load
        // also writes: this fixture has already settled that load.
        var called = false;
        tab.Kill = (_, _, _) =>
        {
            called = true;
            return ProcessManagerService.KillOutcome.Ended;
        };
        var self = new ProcessEntry
        {
            Pid = Environment.ProcessId,
            Name = "SysManager.exe",
            SafetyLevel = nameof(ProcessSafety.Unknown),
        };
        using var dialog = new DialogAnswer(confirm: true);

        tab.Vm.KillProcessCommand.Execute(self);

        Assert.Equal(0, dialog.Calls);
        Assert.False(called);
        Assert.Contains("SysManager itself", tab.Vm.StatusMessage);
    }

    [Fact]
    public async Task BackgroundRefresh_LeavesTheStatusLineAndTheBusyStateAlone()
    {
        // The background refresh used to be the Refresh button's own method. It wrote the announced status line
        // twice a second and flipped IsBusy, so a screen reader heard two sentences a second, and whatever the tab
        // had just reported was gone within one.
        tab.Vm.StatusMessage = "Killed SomeApp.exe (PID 4242).";

        // A row for a process that is not running. The refresh removing it is what shows the refresh ran.
        var gone = new ProcessEntry { Pid = int.MaxValue, Name = "Gone.exe" };
        tab.Vm.Processes.Add(gone);

        ConcurrentQueue<string?> raised = new();
        void Record(object? sender, PropertyChangedEventArgs e) => raised.Enqueue(e.PropertyName);
        tab.Vm.PropertyChanged += Record;
        try
        {
            await tab.Vm.RefreshListAsync(announce: false);
        }
        finally
        {
            tab.Vm.PropertyChanged -= Record;
        }

        Assert.DoesNotContain(gone, tab.Vm.Processes);
        Assert.Equal("Killed SomeApp.exe (PID 4242).", tab.Vm.StatusMessage);
        Assert.DoesNotContain(nameof(tab.Vm.StatusMessage), raised);
        Assert.DoesNotContain(nameof(tab.Vm.IsBusy), raised);
        Assert.DoesNotContain(nameof(tab.Vm.IsProgressIndeterminate), raised);
    }

    [Fact]
    public async Task AProcessAtTheIdOfOneShown_GetsItsDetails()
    {
        // Windows gives a closed process's ID to the next one. What was shown was a set of IDs, so the new process
        // had its path, description and icon skipped as the old one's, and its row stayed bare for its whole
        // lifetime (#2719). This test process stands in for it: its row is made to read as an earlier process.
        using var self = Process.GetCurrentProcess();
        var shown = tab.Vm.Processes.Single(p => p.Pid == self.Id);
        shown.StartTime = self.StartTime.AddMinutes(-5);

        await tab.Vm.RefreshListAsync(announce: false);

        var row = tab.Vm.Processes.Single(p => p.Pid == self.Id);
        Assert.NotSame(shown, row);
        Assert.Equal(self.StartTime, row.StartTime);
        Assert.NotEqual("", row.FilePath);    // the service read it
        Assert.NotNull(row.Icon);              // and the tab filled it in
    }

    [Fact]
    public async Task RefreshButton_StillSaysWhatItLoaded()
    {
        tab.Vm.StatusMessage = "";

        await tab.Vm.RefreshCommand.ExecuteAsync(null);

        Assert.StartsWith("Loaded ", tab.Vm.StatusMessage);
        Assert.False(tab.Vm.IsBusy);
    }
}
