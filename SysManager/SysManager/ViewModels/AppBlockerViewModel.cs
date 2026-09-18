// SysManager · AppBlockerViewModel — block/unblock applications from running
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// App Blocker tab — prevents selected applications from executing using
/// Image File Execution Options (IFEO) registry mechanism.
/// </summary>
public sealed partial class AppBlockerViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshListCommand;

    public BulkObservableCollection<BlockedApp> BlockedApps { get; } = new();

    [ObservableProperty] private string _newExeName = "";
    [ObservableProperty] private string _blockStatus = "Enter an executable name and click Block to prevent it from running.";
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private bool _isElevated;

    /// <summary>
    /// True when at least one existing block cannot be lifted from inside the app, so the tab shows the
    /// warning banner instead of listing it as an ordinary entry.
    /// </summary>
    [ObservableProperty] private bool _hasUnrecoverableBlock;

    /// <summary>
    /// What that block cost and what to do about it, named entry by entry.
    /// </summary>
    /// <remarks>
    /// Built here rather than written into the view because the recovery step DIFFERS by elevation: an
    /// elevated app can undo it with the Unblock button, an unelevated one cannot ask for elevation when
    /// <c>consent.exe</c> is the thing blocked. A single static sentence would be wrong in one of the two
    /// states, and the state it would be wrong in is the stranded one.
    /// </remarks>
    [ObservableProperty] private string _unrecoverableWarning = "";

    private readonly IAppBlockerService _blocker;

    public AppBlockerViewModel(IAppBlockerService blocker)
    {
        _blocker = blocker;
        IsElevated = AdminHelper.IsElevated();
        // Walk the IFEO registry tree off the UI thread so the eagerly-built VM doesn't
        // block startup; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(RefreshListAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    private async Task RefreshListAsync()
    {
        var apps = await Task.Run(_blocker.GetBlockedApps).ConfigureAwait(true);
        ApplyBlockedApps(apps);
    }

    [RelayCommand]
    private void RefreshList() => ApplyBlockedApps(_blocker.GetBlockedApps());

    private void ApplyBlockedApps(IReadOnlyList<BlockedApp> apps)
    {
        // Keep the user's ticks across the refresh. These rows arrive unselected, so a refresh cleared the
        // selection rather than reversing it — "Unblock selected" simply stopped doing anything. Same defect
        // as the tabs whose rows arrive pre-selected, and RefreshOnF5 is RefreshCommand (#2304).
        //
        // Keyed on the executable name, which IS the identity here: an IFEO block is per executable name,
        // and the registry records nothing else. Compared case-insensitively, as Windows compares them.
        Helpers.SelectionCarry.Apply(BlockedApps, apps, a => a.ExecutableName, StringComparer.OrdinalIgnoreCase);
        BlockedApps.ReplaceWith(apps);
        BlockedCount = BlockedApps.Count;
        BlockStatus = BlockedCount == 0
            ? "No applications are currently blocked."
            : $"{BlockedCount} application{(BlockedCount == 1 ? "" : "s")} blocked.";

        UpdateUnrecoverableWarning();
    }

    /// <summary>
    /// The IFEO registry location a stranded machine has to be repaired at, named in full because the
    /// person reading it cannot get SysManager elevated to do it for them.
    /// </summary>
    private const string IfeoKeyPath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>
    /// Builds the rescue banner for blocks the app would refuse to create today.
    /// </summary>
    /// <remarks>
    /// Three consequences, stated separately because they are not the same emergency and a single
    /// sentence covering all three would be wrong about at least one. <c>consent.exe</c> means elevation
    /// is already gone; SysManager's own name means it will not start next time but can still be fixed
    /// right now; anything else on the list is boot-critical, which is why nobody sees this banner for
    /// those — the machine would not have started (#2357).
    /// <para>The recovery step branches on elevation, and the unelevated branch spells out the registry
    /// path rather than pointing at a button, because on that machine the button cannot work.</para>
    /// </remarks>
    private void UpdateUnrecoverableWarning()
    {
        var names = BlockedApps
            .Where(a => a.IsUnrecoverable)
            .Select(a => a.ExecutableName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HasUnrecoverableBlock = names.Count > 0;
        if (!HasUnrecoverableBlock)
        {
            UnrecoverableWarning = "";
            return;
        }

        var listed = string.Join(", ", names);
        var text = names.Count == 1
            ? $"{listed} is blocked, and this is a block SysManager would refuse to create today."
            : $"{listed} are blocked, and these are blocks SysManager would refuse to create today.";

        if (names.Any(n => n.Equals("consent.exe", StringComparison.OrdinalIgnoreCase)))
            text += " consent.exe is the Windows permission prompt, so while it stays blocked nothing on "
                  + "this PC can ask for administrator rights.";

        if (names.Any(n => n.Equals(SelfExecutableName, StringComparison.OrdinalIgnoreCase)))
            text += " One of them is SysManager itself, which means it will not start again after you "
                  + "close it — so undo that one now, while it is still running.";

        text += IsElevated
            ? " SysManager is running as administrator, so tick the entry below and choose Unblock Selected."
            : " Unblocking needs administrator rights this PC may no longer be able to grant. If the "
            + $"prompt never appears, delete the value named Debugger under {IfeoKeyPath}\\<name> from an "
            + "already-elevated Command Prompt, or from Windows' recovery environment.";

        UnrecoverableWarning = text;
    }

    /// <summary>
    /// The app's own executable file name, or empty when it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Read here rather than asked of the service because the service keeps it private for its self-block
    /// guard; this is only used to word the banner, and an empty value simply drops that sentence.
    /// </remarks>
    private static string SelfExecutableName =>
        System.IO.Path.GetFileName(Environment.ProcessPath ?? "");

    [RelayCommand]
    private void BlockApp()
    {
        if (string.IsNullOrWhiteSpace(NewExeName))
        {
            BlockStatus = "Enter an executable name (e.g., notepad.exe).";
            return;
        }

        if (!IsElevated)
        {
            BlockStatus = "Blocking requires administrator privileges.";
            return;
        }

        var exeName = NewExeName.Trim();
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        if (_blocker.IsBlocked(exeName))
        {
            BlockStatus = $"{exeName} is already blocked.";
            return;
        }

        // Says what the person will actually SEE, not just what will happen. A block works by pointing
        // Windows at a debugger that does not exist, so launching the app produces a Windows error naming
        // a missing SysManager_Blocked.exe — which reads as SysManager having broken the computer rather
        // than as a block doing its job. The mechanism is deliberate and stays: shipping a real stub into
        // System32 would be a worse trade for an unsigned app, and a stub on a writable path invoked by an
        // elevated target would be a privilege problem. So the dialog sets the expectation instead (#2357).
        if (!DialogService.Instance.Confirm(
            $"Block \"{exeName}\" from running?\n\nThis will prevent the application from launching until "
            + "you unblock it. Nothing is deleted and the program stays installed.\n\nWhen someone tries "
            + "to open it, Windows shows an error saying it cannot find SysManager_Blocked.exe. That is "
            + "what a block looks like — it is not a fault, and it goes away when you unblock.",
            "Block Application — Confirm")) return;

        var result = _blocker.TryBlockApp(exeName);
        if (result == AppBlockerService.BlockResult.Success)
        {
            NewExeName = "";
            RefreshList();
            BlockStatus = $"Blocked {exeName}.";
            Log.Information("User blocked application: {ExeName}", exeName);
            return;
        }

        // Say which refusal it was. Every failure used to be reported as "check admin
        // privileges", so a user blocked by a deliberate safety guard was sent to relaunch
        // elevated — where the same guard refuses again, still without explaining itself.
        BlockStatus = result switch
        {
            // Covers both denylist classes without claiming the wrong one. "Required to start" was true of
            // winlogon.exe and false of consent.exe: Windows boots fine without the consent UI, and the
            // damage only shows up the first time something asks for administrator rights. What both share
            // is that blocking them removes the means of unblocking them.
            AppBlockerService.BlockResult.BootCritical =>
                $"{exeName} is a part of Windows that has to keep working, so SysManager will not block it. "
                + "Blocking it could stop the computer starting, or stop Windows being able to ask for "
                + "permission — and neither could be undone from here.",
            AppBlockerService.BlockResult.OwnExecutable =>
                $"{exeName} is SysManager itself. Blocking it would stop SysManager from launching, "
                + "and unblocking has to be done from inside the app — so this one is refused.",
            AppBlockerService.BlockResult.ExternalDebuggerPresent =>
                $"Another program has already registered a debugger for {exeName}. SysManager will not "
                + "overwrite it, because doing so would break that program's setup and could not be undone here.",
            AppBlockerService.BlockResult.InvalidName =>
                $"\"{exeName}\" is not a valid executable name. Enter just the file name, "
                + "for example notepad.exe, without a folder path.",
            AppBlockerService.BlockResult.EmptyName =>
                "Enter an executable name (e.g., notepad.exe).",
            AppBlockerService.BlockResult.AccessDenied =>
                $"Windows denied the change needed to block {exeName}. This step needs administrator "
                + "rights — restart SysManager as administrator and try again.",
            _ =>
                $"Could not block {exeName}: the registry change failed. The app log has the details."
        };
    }

    [RelayCommand]
    private void UnblockSelected()
    {
        var selected = BlockedApps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            BlockStatus = "Select applications to unblock.";
            return;
        }

        // Before the confirmation, exactly like BlockApp. Unblocking writes the same HKLM key that
        // blocking does, so without administrator rights every write fails — and asking the user to
        // approve "they will be allowed to run again" and then reporting "Unblocked 0 applications"
        // told them nothing about why. Placing the check after the dialog would still produce the
        // right words while having asked permission for something that cannot happen.
        if (!IsElevated)
        {
            BlockStatus = "Unblocking requires administrator privileges.";
            return;
        }

        if (!DialogService.Instance.Confirm(
            $"Unblock {selected.Count} application{(selected.Count == 1 ? "" : "s")}?\n\nThey will be allowed to run again.",
            "Unblock Applications — Confirm")) return;

        int unblocked = 0;
        foreach (var app in selected)
        {
            if (_blocker.UnblockApp(app.ExecutableName))
                unblocked++;
        }

        RefreshList();
        // Say so when some of them did not work. Elevated, a single write can still fail — the key
        // changed underneath, or the file is locked — and "Unblocked 2 applications" after selecting
        // three reads as complete success.
        BlockStatus = unblocked == selected.Count
            ? $"Unblocked {unblocked} application{(unblocked == 1 ? "" : "s")}."
            : $"Unblocked {unblocked} of {selected.Count}. "
              + $"{selected.Count - unblocked} could not be changed — try again, or restart and retry.";
        Log.Information("User unblocked {Count} of {Selected} applications", unblocked, selected.Count);
    }

    [RelayCommand]
    private void BrowseForExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select application to block",
            Filter = "Executables (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            NewExeName = System.IO.Path.GetFileName(dialog.FileName);
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var a in BlockedApps) a.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var a in BlockedApps) a.IsSelected = false;
    }
}
