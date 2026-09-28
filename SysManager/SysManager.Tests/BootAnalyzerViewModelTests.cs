// SysManager · BootAnalyzerViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;
using static SysManager.Tests.ScriptedBootReader;

namespace SysManager.Tests;

/// <summary>
/// What the Boot Analyzer tab says for a history it read, an empty one, and one it could not read (#2500).
/// </summary>
/// <remarks>
/// The tab reads when it is built, so every test awaits that first read before asserting on the status line. The log
/// is a script per query: boot summaries are event 100, slow components events 101 to 110.
/// </remarks>
public class BootAnalyzerViewModelTests
{
    private static BootAnalyzerViewModel NewVm(bool elevated, Func<ScriptedBootReader> boots, Func<ScriptedBootReader> slow)
        => new(new BootAnalyzerService(xpath => xpath.Contains("EventID=100", StringComparison.Ordinal) ? boots() : slow()),
               Substitute.For<INavigationService>(), () => elevated);

    [Fact]
    public async Task AReadThatFailed_WhenElevated_SaysSo_NotThatThereAreNoBootsYet()
    {
        var vm = NewVm(elevated: true, () => new ScriptedBootReader(Fails()), () => new ScriptedBootReader(End()));
        await vm.InitializationComplete;

        Assert.Contains("could not be read", vm.StatusMessage);
        Assert.DoesNotContain("No boot performance events", vm.StatusMessage);
        Assert.False(vm.HasData);
    }

    [Fact]
    public async Task AReadThatWasRefused_WithoutElevation_PointsAtAdministrator()
    {
        var vm = NewVm(elevated: false, () => new ScriptedBootReader(Fails()), () => new ScriptedBootReader(Fails()));
        await vm.InitializationComplete;

        Assert.Contains("requires administrator", vm.StatusMessage);
    }

    [Fact]
    public async Task AnEmptyLog_SaysNoBootsYet()
    {
        var vm = NewVm(elevated: true, () => new ScriptedBootReader(End()), () => new ScriptedBootReader(End()));
        await vm.InitializationComplete;

        Assert.StartsWith("No boot performance events found yet", vm.StatusMessage);
    }

    [Fact]
    public async Task ARefreshThatFails_KeepsTheHistoryAlreadyShown()
    {
        var reads = 0;
        var vm = NewVm(elevated: true,
            () => ++reads == 1
                ? new ScriptedBootReader(Returns(ScriptedBootEvents.Boot(42_000)), End())
                : new ScriptedBootReader(Fails()),
            () => new ScriptedBootReader(End()));
        await vm.InitializationComplete;
        Assert.Single(vm.Boots);   // the premise: the first read filled the tab

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(42_000, Assert.Single(vm.Boots).BootTimeMs);
        Assert.Contains("could not be read", vm.StatusMessage);
    }

    [Fact]
    public async Task SlowComponentsThatCouldNotBeRead_AreNamedAsSuch_WhileTheBootsStillShow()
    {
        var vm = NewVm(elevated: true,
            () => new ScriptedBootReader(Returns(ScriptedBootEvents.Boot(42_000)), End()),
            () => new ScriptedBootReader(Fails()));
        await vm.InitializationComplete;

        Assert.Single(vm.Boots);
        Assert.Contains("slow-component list could not be read", vm.StatusMessage);
    }

    [Fact]
    public async Task AHistoryThatWasRead_IsSummarised()
    {
        var vm = NewVm(elevated: true,
            () => new ScriptedBootReader(Returns(ScriptedBootEvents.Boot(42_000)), End()),
            () => new ScriptedBootReader(Returns(ScriptedBootEvents.SlowApp("slowapp.exe", 5_200)), End()));
        await vm.InitializationComplete;

        Assert.StartsWith("1 boots analyzed; 1 slow-component events.", vm.StatusMessage);
        Assert.Single(vm.Degradations);
    }
}
