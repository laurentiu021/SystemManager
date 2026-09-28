// SysManager · PrivacyMonitorViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyMonitorViewModel"/>: what the tab says for a read that failed, in whole or in part
/// (#2503). The view model is handed its read, so nothing here touches the registry.
/// </summary>
/// <remarks>
/// A capability whose consent-store key could not be read was skipped without a trace. With all three unreadable the
/// tab said "No camera, microphone, or location access has been recorded yet.", and with only the camera unreadable it
/// listed the microphone and location as if the camera had been checked.
/// </remarks>
public class PrivacyMonitorViewModelTests
{
    private static readonly PrivacyAccessEntry Mic = new("Microphone", "SomeChatApp", new DateTime(2024, 5, 1), InUse: false);
    private static readonly PrivacyAccessEntry Cam = new("Camera", "Microsoft.WindowsCamera", new DateTime(2024, 5, 2), InUse: false);

    private static PrivacyAccessReport Report(IReadOnlyList<PrivacyAccessEntry> entries, params string[] unreadable)
        => new(entries, unreadable);

    private static async Task<PrivacyMonitorViewModel> SettledVm(Func<PrivacyAccessReport> read)
    {
        var vm = new PrivacyMonitorViewModel(_ => Task.FromResult(read()));
        await vm.InitializationComplete;
        return vm;
    }

    // ── what a read says ──

    [Fact]
    public void Describe_EverythingRead_KeepsTheWordingItHad()
    {
        Assert.Equal("No camera, microphone, or location access has been recorded yet.",
            PrivacyMonitorViewModel.Describe(Report([])));
        Assert.Equal("1 access record(s) across camera, microphone, and location.",
            PrivacyMonitorViewModel.Describe(Report([Mic])));
    }

    [Fact]
    public void Describe_OneCapabilityUnread_NamesIt_AndClaimsNothingAboutIt()
    {
        var none = PrivacyMonitorViewModel.Describe(Report([], "Camera"));
        Assert.Equal("No microphone or location access has been recorded yet. Could not read the camera history.", none);

        var some = PrivacyMonitorViewModel.Describe(Report([Mic], "Camera"));
        Assert.Equal("1 access record(s). Could not read the camera history.", some);
    }

    [Fact]
    public void Describe_NothingRead_SaysSo()
        => Assert.Equal("Could not read the camera, microphone, or location history.",
            PrivacyMonitorViewModel.Describe(Report([], "Camera", "Microphone", "Location")));

    [Fact]
    public void DescribeEmpty_NamesOnlyWhatWasRead()
    {
        Assert.Equal(("No access recorded", "Windows hasn't logged any camera, microphone, or location access yet."),
            PrivacyMonitorViewModel.DescribeEmpty(Report([])));

        var (title, message) = PrivacyMonitorViewModel.DescribeEmpty(Report([], "Camera", "Location"));
        Assert.Equal("No access recorded", title);
        Assert.Equal("Windows hasn't logged any microphone access yet. SysManager could not read the camera and location history.",
            message);

        Assert.Equal("Access history could not be read",
            PrivacyMonitorViewModel.DescribeEmpty(Report([], "Camera", "Microphone", "Location")).Title);
    }

    // ── what the tab shows ──

    [Fact]
    public async Task AReadThatFailedOutright_SaysSo_NotThatNothingWasUsed()
    {
        var vm = await SettledVm(() => Report([], "Camera", "Microphone", "Location"));

        Assert.Equal("Could not read the camera, microphone, or location history. Press Refresh to try again.", vm.StatusMessage);
        Assert.Equal("Access history could not be read", vm.EmptyTitle);
        Assert.False(vm.HasEntries);
    }

    [Fact]
    public async Task AFailedRefresh_KeepsTheList_AndSaysItIsFromTheLastRead()
    {
        var reads = 0;
        var vm = await SettledVm(() => ++reads == 1 ? Report([Cam, Mic]) : Report([], "Camera", "Microphone", "Location"));
        Assert.Equal(2, vm.Entries.Count);   // the premise: the first read listed them

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.HasEntries);
        Assert.Equal("Could not read the camera, microphone, or location history, so the list below is from the last read.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task APartialRead_ShowsWhatWasRead_AndNamesWhatWasNot()
    {
        var vm = await SettledVm(() => Report([Mic], "Camera"));

        Assert.Equal(Mic, Assert.Single(vm.Entries));
        Assert.Equal("1 access record(s). Could not read the camera history.", vm.StatusMessage);
    }

    [Fact]
    public async Task AReadThatWorksAfterAFailedOne_ClearsTheFailure()
    {
        var reads = 0;
        var vm = await SettledVm(() => ++reads == 1 ? Report([], "Camera", "Microphone", "Location") : Report([]));
        Assert.Equal("Access history could not be read", vm.EmptyTitle);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("No access recorded", vm.EmptyTitle);
        Assert.Equal("No camera, microphone, or location access has been recorded yet.", vm.StatusMessage);
    }
}
