// SysManager · EnvironmentVariablesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SysManager.Helpers;
using SysManager.Models;
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
// Serialized: the restore test answers through DialogService.Instance and takes OperationLockService.Instance's lock.
// Required by ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
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
    /// A copy that could not be read just now is said to be that, not invalid (#1525).
    /// </summary>
    /// <remarks>
    /// Over throwaway registry keys and a temp folder: the restore runs, and refuses, against nothing of this PC's.
    /// </remarks>
    [Fact]
    public async Task RestoringACopyThatCannotBeReadJustNow_SaysSo_NotThatItIsInvalid()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "changed");
        env.WriteLegacyUserBackup("""{"User":{"SAFE_USER":"original"}}""");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        using (File.Open(env.Service.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal("The environment backup could not be read just now; no changes were made. Try again in a moment.",
            vm.StatusMessage);
        Assert.Equal("changed", env.GetUser("SAFE_USER"));
    }

    /// <summary>
    /// Applying over a copy that could not be read just now says that too, not that the copy is invalid (#1525).
    /// </summary>
    /// <remarks>
    /// Over throwaway registry keys and a temp folder. The refusal comes before the first write, so nothing is applied
    /// and no other window is told the variables changed.
    /// </remarks>
    [Fact]
    public async Task ApplyingOverACopyThatCannotBeReadJustNow_SaysSo_NotThatItIsInvalid()
    {
        using var env = new RedirectedEnvironment();
        env.WriteLegacyUserBackup("""{"User":{"SAFE_USER":"original"}}""");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        vm.NewName = "SAFE_ADDED";
        vm.NewValue = "added";
        vm.AddVariableCommand.Execute(null);
        Assert.Equal(1, vm.PendingChangeCount);
        using var dialog = new DialogAnswer(confirm: true);

        using (File.Open(env.Service.BackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal("The environment backup could not be read just now; no changes were made. Try again in a moment.",
            vm.StatusMessage);
        Assert.Null(env.GetUser("SAFE_ADDED"));
    }

    /// <summary>
    /// A variable the copy cannot keep blocks Apply by name, not as a damaged copy that does not exist (#2775).
    /// </summary>
    [Fact]
    public async Task ApplyingWithANumberAmongYourVariables_SaysWhichBlocksTheCopy()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("COUNTER", 7, RegistryValueKind.DWord);
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        vm.NewName = "SAFE_ADDED";
        vm.NewValue = "added";
        vm.AddVariableCommand.Execute(null);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal("No changes were made: SysManager could not take its safety copy, because COUNTER, one of your "
                     + "variables, holds a number rather than text, and the copy keeps text only.", vm.StatusMessage);
        Assert.Null(env.GetUser("SAFE_ADDED"));
    }

    /// <summary>
    /// Restore asks with each variable it would change, as Undo Changes does (#2775). It said only that it "removes
    /// variables added since", with no names, though it also rewrites PATH without folders installers added since.
    /// </summary>
    [Fact]
    public async Task Restoring_AsksWithEachVariableItWouldChange()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "changed");
        env.SetUser("ADDED_SINCE", "new");
        env.WriteUserBackup("""{"User":{"SAFE_USER":"original","GONE_NOW":"was here"}}""");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.Contains("• ADDED_SINCE (yours): removed, added since", shown, StringComparison.Ordinal);
        Assert.Contains("• GONE_NOW (yours): put back", shown, StringComparison.Ordinal);
        Assert.Contains("• SAFE_USER (yours): back to its earlier value", shown, StringComparison.Ordinal);
        Assert.Contains("including changes other programs made since", shown, StringComparison.Ordinal);
        Assert.Equal("Restore cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task Restoring_WhenNothingDiffers_SaysSo_WithoutAsking()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "original");
        env.WriteUserBackup("""{"User":{"SAFE_USER":"original"}}""");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Empty(dialog.Messages);
        Assert.Equal("Your variables already match the backup, so there is nothing to restore.", vm.StatusMessage);
    }

    /// <summary>
    /// The status after a restore counts what changed, says "administrator" only for machine-wide refusals while not
    /// elevated, and names the variables it left alone (#2775).
    /// </summary>
    [Theory]
    [InlineData(2, 1, 0, 0, false, "", "Restored 2 variables and removed 1 added since.")]
    [InlineData(1, 0, 0, 0, false, "", "Restored 1 variable.")]
    [InlineData(1, 0, 2, 2, false, "", "Restored 1, removed 0; 2 need administrator rights.")]
    [InlineData(1, 0, 2, 2, true, "", "Restored 1, removed 0; Windows refused 2.")]
    [InlineData(1, 0, 3, 1, false, "", "Restored 1, removed 0; 1 needs administrator rights; Windows refused 2.")]
    [InlineData(1, 0, 0, 0, false, "MY VAR",
        "Restored 1 variable. MY VAR was left as it is: SysManager does not write a name with a space or that kind of "
        + "character in it.")]
    [InlineData(0, 0, 0, 0, true, "A B|C D",
        "Restored 0 variables. A B and C D were left as they are: SysManager does not write a name with a space or "
        + "that kind of character in it.")]
    public void TheRestoredStatus_SaysWhatChanged(int restored, int removed, int failed, int failedMachine,
                                                  bool elevated, string skipped, string said)
    {
        var result = new EnvironmentVariableService.RestoreResult(true, restored, removed, failed)
        {
            FailedMachine = failedMachine,
            Skipped = skipped.Length == 0 ? [] : skipped.Split('|'),
        };

        Assert.Equal(said, EnvironmentVariablesViewModel.RestoredStatus(result, elevated));
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

    // ---------- PATH folders checked off the UI thread, each once a look (#2873) ----------
    // Directory.Exists ran on the UI thread for every entry, at each selection, keystroke and edit, and one folder on a
    // share that could not be reached held the window for the whole connection attempt. These run over a redirected
    // environment and a folder check the test supplies, so no real folder or share is asked about.

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static async Task<EnvironmentVariablesViewModel> WithUserPath(RedirectedEnvironment env, string path,
                                                                          Func<string, bool> folderExists)
    {
        env.SetUser("PATH", path);
        var vm = new EnvironmentVariablesViewModel(env.Service, null, folderExists);
        await vm.InitializationComplete.WaitAsync(Bound);
        return vm;
    }

    private static EnvVariable UserPath(EnvironmentVariablesViewModel vm) =>
        vm.Variables.Single(v => v.Scope == EnvVarScope.User && v.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task SelectingPath_DoesNotWaitForTheFolderCheck()
    {
        using var env = new RedirectedEnvironment();
        using var slow = new ManualResetEventSlim();
        var vm = await WithUserPath(env, @"C:\Contoso\bin;C:\Contoso\gone", folder =>
        {
            slow.Wait(Bound);
            return !folder.EndsWith("gone", StringComparison.Ordinal);
        });

        vm.SelectedVariable = UserPath(vm);

        // Back on the test's thread with the check still waiting: the selection did not wait for it.
        Assert.False(vm.FolderCheck.IsCompleted);
        Assert.Equal([@"C:\Contoso\bin", @"C:\Contoso\gone"], vm.PathEntries.Select(e => e.Directory));
        Assert.All(vm.PathEntries, e => Assert.False(e.IsMissing));

        slow.Set();
        await vm.FolderCheck.WaitAsync(Bound);
        Assert.Equal([false, true], vm.PathEntries.Select(e => e.IsMissing));
    }

    [Fact]
    public async Task AFolderOnAShare_IsNotChecked_AndNotCalledMissing()
    {
        using var env = new RedirectedEnvironment();
        var asked = new ConcurrentQueue<string>();
        var vm = await WithUserPath(env, @"\\nas\tools\bin;C:\Contoso\bin", folder =>
        {
            asked.Enqueue(folder);
            return false;
        });

        vm.SelectedVariable = UserPath(vm);
        await vm.FolderCheck.WaitAsync(Bound);

        Assert.Equal([@"C:\Contoso\bin"], asked);
        Assert.Equal([false, true], vm.PathEntries.Select(e => e.IsMissing));
    }

    [Fact]
    public async Task EditingTheList_ChecksOnlyNewFolders_AndANewLookChecksThemAllAgain()
    {
        using var env = new RedirectedEnvironment();
        var asked = new ConcurrentQueue<string>();
        var vm = await WithUserPath(env, @"C:\Contoso\a;C:\Contoso\b", folder =>
        {
            asked.Enqueue(folder);
            return true;
        });
        var path = UserPath(vm);
        vm.SelectedVariable = path;
        await vm.FolderCheck.WaitAsync(Bound);

        vm.MoveDirectoryDownCommand.Execute(vm.PathEntries[0]);   // the same folders, in another order
        path.Value += @";C:\Contoso\c";                           // typed into the Value cell
        await vm.FolderCheck.WaitAsync(Bound);
        Assert.Equal([@"C:\Contoso\a", @"C:\Contoso\b", @"C:\Contoso\c"], asked.Order());   // each once

        vm.SelectedVariable = null;
        vm.SelectedVariable = path;
        await vm.FolderCheck.WaitAsync(Bound);
        Assert.Equal([@"C:\Contoso\a", @"C:\Contoso\b", @"C:\Contoso\c"], asked.Skip(3).Order());
    }

    [Theory]
    [InlineData(@"\\nas\tools\bin", true)]
    [InlineData("//nas/tools", true)]
    [InlineData(@"""\\nas\tools""", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData("bin", false)]
    [InlineData(@"%SystemRoot%\system32", false)]
    public void IsOnTheNetwork_FindsAShareByItsPath(string folder, bool onTheNetwork)
    {
        Assert.Equal(onTheNetwork, EnvironmentVariablesViewModel.IsOnTheNetwork(folder));
    }

    // ---------- Apply keeps what changed outside the tab, and a new variable has a scope (#2798) ----------
    // Over a redirected environment, so the user's and the machine's variables are never read or written.

    private const string ChangedOutsideOne =
        "PATH (User) was changed outside SysManager after this tab read it, so nothing was applied. "
        + "Press Refresh to see the change, then make yours again.";

    [Fact]
    public async Task Apply_AfterAnInstallerAddedItselfToPath_KeepsItsFolder_AndWritesNothing()
    {
        // Python, Node.js, Git and JDK installers add themselves to PATH. With the tab open, moving one PATH row and
        // pressing Apply wrote back the PATH the tab had read, and the installer's folder was gone.
        using var env = new RedirectedEnvironment();
        var vm = await WithUserPath(env, @"C:\Tools;C:\Git\cmd", _ => true);
        env.SetUser("PATH", @"C:\Tools;C:\Git\cmd;C:\Program Files\nodejs");
        UserPath(vm).Value = @"C:\Git\cmd;C:\Tools";
        Assert.Equal(1, vm.PendingChangeCount);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Tools;C:\Git\cmd;C:\Program Files\nodejs", env.GetUser("PATH"));
        Assert.Equal(ChangedOutsideOne, vm.StatusMessage);
        Assert.Equal(1, vm.PendingChangeCount);   // the edit is still there to make again
        Assert.False(env.Service.HasBackup);      // and nothing was written, the safety copy included
    }

    [Fact]
    public async Task Apply_WithNothingChangedOutside_StillWrites()
    {
        using var env = new RedirectedEnvironment();
        var vm = await WithUserPath(env, @"C:\Tools;C:\Git\cmd", _ => true);
        UserPath(vm).Value = @"C:\Git\cmd;C:\Tools";
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Git\cmd;C:\Tools", env.GetUser("PATH"));
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public async Task Apply_OfANewVariable_ThatAppearedOutsideMeanwhile_WritesNothing()
    {
        using var env = new RedirectedEnvironment();
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete.WaitAsync(Bound);
        vm.NewName = "JAVA_HOME";
        vm.NewValue = @"C:\Java\17";
        vm.AddVariableCommand.Execute(null);
        env.SetUser("JAVA_HOME", @"C:\Java\21");
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Java\21", env.GetUser("JAVA_HOME"));
        Assert.StartsWith("JAVA_HOME (User) was changed outside SysManager", vm.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_OfADeletion_OfAVariableChangedOutsideMeanwhile_DeletesNothing()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("TOOL_HOME", @"C:\Tool\1");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete.WaitAsync(Bound);
        var tool = vm.Variables.Single(v => v.Name == "TOOL_HOME");
        using var dialog = new DialogAnswer(confirm: true);
        vm.DeleteVariableCommand.Execute(tool);
        env.SetUser("TOOL_HOME", @"C:\Tool\2");

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Tool\2", env.GetUser("TOOL_HOME"));
        Assert.StartsWith("TOOL_HOME (User) was changed outside SysManager", vm.StatusMessage,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "PATH (User) was changed outside SysManager after this tab read it")]
    [InlineData(2, "PATH (User) and TEMP (User) were changed outside SysManager after this tab read them")]
    [InlineData(3,
        "PATH (User), TEMP (User) and Path (System) were changed outside SysManager after this tab read them")]
    [InlineData(5, "PATH (User), TEMP (User) and 3 more were changed outside SysManager after this tab read them")]
    public void TheApplyRefusal_NamesWhatChanged(int count, string start)
    {
        string[] names = ["PATH (User)", "TEMP (User)", "Path (System)", "TMP (System)", "JAVA_HOME (User)"];

        var message = EnvironmentVariablesViewModel.DescribeChangedOutside(names[..count]);

        Assert.StartsWith(start + ", so nothing was applied. ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewVariable_IsUserOrSystem_NeverAll()
    {
        // The picker listed the scope filter's own choices, so "All" made one User variable and the status line said
        // "Added All variable".
        var vm = NewVm();
        Assert.Equal(["User", "System"], vm.NewScopes);

        var view = System.Xml.Linq.XDocument.Load(
            Path.Combine(TestPaths.AppProject(), "Views", "EnvironmentVariablesView.xaml"));
        var picker = Assert.Single(view.Descendants(), e => e.Name.LocalName == "ComboBox"
            && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"
                                       && a.Value == "New variable scope"));
        Assert.Equal("{Binding NewScopes}", (string?)picker.Attribute("ItemsSource"));
    }

    // ---------- F5 and Refresh over changes not applied yet (#2734) ----------
    // A read replaced the variables and their baseline, so an added variable, a deletion already confirmed or a PATH
    // reorder not applied yet went with no word, and F5 runs Refresh, even from the value box. Over throwaway
    // registry keys, changed after the first read so a second read is seen.

    private static async Task<EnvironmentVariablesViewModel> WithAVariableAdded(RedirectedEnvironment env)
    {
        env.SetUser("SAFE_USER", "before");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        vm.NewName = "SAFE_ADDED";
        vm.NewValue = "added";
        vm.AddVariableCommand.Execute(null);
        Assert.Equal(1, vm.PendingChangeCount);
        env.SetUser("SAFE_USER", "changed since");
        return vm;
    }

    private static string ValueOf(EnvironmentVariablesViewModel vm, string name) =>
        vm.Variables.Single(v => v.Name == name).Value;

    [Fact]
    public async Task F5_OverAChangeNotAppliedYet_AsksFirst_AndNoKeepsIt()
    {
        using var env = new RedirectedEnvironment();
        var vm = await WithAVariableAdded(env);
        using var dialog = new DialogAnswer(confirm: false);

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5).ExecuteAsync(null);

        Assert.Equal([$"{UnappliedChanges.Title}\n{UnappliedChanges.Question("Apply")}"], dialog.Messages);
        Assert.Equal("added", ValueOf(vm, "SAFE_ADDED"));
        Assert.Equal("before", ValueOf(vm, "SAFE_USER"));   // not read again
        Assert.Equal(1, vm.PendingChangeCount);
        Assert.Equal(UnappliedChanges.Kept("Apply"), vm.StatusMessage);
    }

    [Fact]
    public async Task F5_OverAChangeNotAppliedYet_ReadsAgain_WhenTheUserSaysYes()
    {
        using var env = new RedirectedEnvironment();
        var vm = await WithAVariableAdded(env);
        using var dialog = new DialogAnswer(confirm: true);

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5).ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.DoesNotContain(vm.Variables, v => v.Name == "SAFE_ADDED");
        Assert.Equal("changed since", ValueOf(vm, "SAFE_USER"));
        Assert.Equal(0, vm.PendingChangeCount);
        Assert.Equal("Variables refreshed.", vm.StatusMessage);
        Assert.Null(env.GetUser("SAFE_ADDED"));   // nothing was applied on the way
    }

    [Fact]
    public async Task F5_WithNothingPending_ReadsAgain_WithoutAsking()
    {
        using var env = new RedirectedEnvironment();
        env.SetUser("SAFE_USER", "before");
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        env.SetUser("SAFE_USER", "changed since");
        using var dialog = new DialogAnswer(confirm: false);

        await Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshOnF5).ExecuteAsync(null);

        Assert.Equal(0, dialog.Calls);
        Assert.Equal("changed since", ValueOf(vm, "SAFE_USER"));
    }

    // ---------- what only an administrator can change (#2742) ----------
    // Both decide on the elevation answer inside a compound condition, and no test decided that answer, so each test
    // that reached them asserted whichever side the PC running it was on. Over throwaway registry keys, and each one
    // declines before anything is written.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingASystemVariable_IsRefusedBeforeAnyQuestion_UnlessElevated(bool elevated)
    {
        using var env = new RedirectedEnvironment();
        using var elevation = AdminHelper.ForceElevation(elevated);
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        vm.NewName = "SAFE_SYSTEM";
        vm.NewValue = "added";
        vm.NewScope = "System";
        vm.AddVariableCommand.Execute(null);
        Assert.Equal(1, vm.PendingChangeCount);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(elevated ? 1 : 0, dialog.Calls);
        Assert.Equal(elevated ? "Apply cancelled." : "Some changes affect System variables — relaunch as administrator first.",
            vm.StatusMessage);
        Assert.Null(env.GetMachine("SAFE_SYSTEM"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoringACopyWithSystemVariables_SaysTheyAreSkipped_UnlessElevated(bool elevated)
    {
        using var env = new RedirectedEnvironment();
        env.SetMachine("SAFE_MACHINE", "changed");
        env.WriteMachineBackup("""{"Machine":{"SAFE_MACHINE":"original"}}""", RegistryValueKind.String);
        using var elevation = AdminHelper.ForceElevation(elevated);
        var vm = new EnvironmentVariablesViewModel(env.Service);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialog.Messages);
        Assert.Equal(!elevated,
            shown.Contains("System variables can only be restored when running as administrator", StringComparison.Ordinal));
        Assert.Equal("Restore cancelled.", vm.StatusMessage);
        Assert.Equal("changed", env.GetMachine("SAFE_MACHINE"));
    }
}
