// SysManager · EnvironmentVariablesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EnvironmentVariablesViewModel"/>'s no-results state.
/// </summary>
/// <remarks>
/// The view-model reads the real environment hives on construction, which is a read and needs no elevation.
/// A throwaway backup directory is passed even though nothing here writes one, for the same reason
/// <c>BulkInstallerViewModelTests</c> passes a temp icon cache: a test never builds a service against the
/// developer's real profile, whether or not this particular test would touch it.
/// </remarks>
public class EnvironmentVariablesViewModelTests : IDisposable
{
    private readonly string _backupDir =
        Path.Combine(Path.GetTempPath(), "SysManagerEnvVmTests", Guid.NewGuid().ToString("N"));

    private EnvironmentVariablesViewModel NewVm()
    {
        var vm = new EnvironmentVariablesViewModel(new EnvironmentVariableService(_backupDir));
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    /// <summary>
    /// A search term that matches no name and no value is reported as a search with no matches.
    /// </summary>
    /// <remarks>
    /// The grid kept its column headers over empty space, which reads as "this machine has no environment
    /// variables" rather than "none of them match what you typed".
    /// </remarks>
    [Fact]
    public void SearchMatchingNothing_ReportsNoMatches()
    {
        var vm = NewVm();
        Assert.NotEmpty(vm.Variables);           // the machine always has some
        Assert.False(vm.HasNoMatches);

        vm.SearchText = "zzz_no_such_variable_" + Guid.NewGuid().ToString("N");

        Assert.Empty(vm.FilteredVariables);
        Assert.True(vm.HasNoMatches);
    }

    /// <summary>
    /// Clearing the search puts the state back, so the message is not left behind over a full list.
    /// </summary>
    [Fact]
    public void ClearingTheSearch_ClearsTheNoMatchesState()
    {
        var vm = NewVm();
        vm.SearchText = "zzz_no_such_variable_" + Guid.NewGuid().ToString("N");
        Assert.True(vm.HasNoMatches);

        vm.SearchText = "";

        Assert.NotEmpty(vm.FilteredVariables);
        Assert.False(vm.HasNoMatches);
    }

    /// <summary>
    /// With nothing read at all, the tab does NOT blame the search.
    /// </summary>
    /// <remarks>
    /// This is the half a bare <c>FilteredVariables.Count == 0</c> would get wrong. An empty grid because
    /// the hives could not be read is a different problem from a search that matched nothing, and telling
    /// the user to clear a search box would send them to fix something that is not broken. Same distinction
    /// <c>LogsViewModel.HasNoResults</c> draws between "the filters hid everything" and "the log could not
    /// be read".
    /// </remarks>
    [Fact]
    public void WithNoVariablesRead_DoesNotBlameTheSearch()
    {
        var vm = NewVm();
        vm.Variables.Clear();

        vm.SearchText = "anything";

        Assert.Empty(vm.FilteredVariables);
        Assert.False(vm.HasNoMatches);
    }
}
