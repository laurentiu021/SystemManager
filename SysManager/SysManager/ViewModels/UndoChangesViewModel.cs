// SysManager · UndoChangesViewModel — the Undo Changes tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Undo Changes: every change SysManager can put back, in one place, one at a time (#1525).
/// </summary>
/// <remarks>
/// Each tab that changes something keeps what it needs to put that change back, and until now that copy could only be
/// used from the tab that wrote it — so undoing a change meant remembering which tab had made it. This lists them all
/// in the same words, asks before each one with what it will change, and points to the tabs whose switches are their
/// own undo and to Restore Points for the whole PC at once.
/// <para>There is deliberately no "undo everything". One button reversing several unrelated changes would itself be a
/// large change, and a restore point already does "everything".</para>
/// </remarks>
public sealed partial class UndoChangesViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private const string LookingForRestorePoint = "Looking for the newest restore point…";

    /// <summary>What the card says when Windows has not answered within <see cref="RestorePointPatience"/>.</summary>
    internal const string RestorePointNotAnswered =
        "Windows did not answer about its restore points within a minute. Refresh asks again.";

    /// <summary>
    /// How long the restore point question may go unanswered. It waits on System Restore and the shadow copy service,
    /// and a minute is far longer than either takes on a PC where they work.
    /// </summary>
    internal static readonly TimeSpan RestorePointPatience = TimeSpan.FromMinutes(1);

    private readonly IUndoChangesService _service;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _clock;

    // The look running now, so a put-back confirmed while a look started under its question can wait for that look
    // rather than run beside it: the look's end would turn Busy off while the put-back was still going.
    private Task _scanning = Task.CompletedTask;

    // A look asked for while another was running, done once that one ends rather than dropped: the change it would find
    // is the one the user came back to see.
    private bool _lookAgain;

    // Cancelled when the tab is disposed, so a restore point question Windows never answers does not outlive it.
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The tabs whose switches are their own undo, in the order the page lists them.</summary>
    /// <remarks>
    /// Links, never rows. A switch that is on may have been set by Windows, by an organisation's policy, or by the user
    /// long ago, and SysManager keeps no record that says which — so it cannot honestly list one as a change it made.
    /// </remarks>
    internal static readonly IReadOnlyList<UndoSwitch> SwitchTabs =
    [
        new("Privacy & Telemetry", "tracking and ad settings", "nav-privacy-settings"),
        new("Context Menu", "hidden right-click entries", "nav-context-menu"),
        new("Startup Manager", "programs kept from starting", "nav-startup"),
        new("App Blocker", "programs blocked from running", "nav-app-blocker"),
        new("Notification Blocker", "muted notifications", "nav-notification-blocker"),
        new("DNS & Hosts", "DNS back to automatic", "nav-dns-hosts"),
    ];

    /// <summary>The changes found when the tab last looked.</summary>
    public BulkObservableCollection<UndoChangeRow> Changes { get; } = new();

    /// <summary>The tabs whose switches are their own undo.</summary>
    public IReadOnlyList<UndoSwitch> Switches => SwitchTabs;

    [ObservableProperty] private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingToPutBack))]
    private bool _hasChanges;

    /// <summary>True once the tab has looked at least once, so "nothing to put back" is never said before it has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingToPutBack))]
    private bool _hasLooked;

    /// <summary>True when the tab has looked and found nothing to list.</summary>
    public bool ShowNothingToPutBack => HasLooked && !HasChanges;

    /// <summary>What the empty state is headed with.</summary>
    [ObservableProperty] private string _emptyTitle = "";

    /// <summary>
    /// What the empty state says: that nothing is waiting to be put back, or — when a copy could not be used — which
    /// one, since then the tab cannot say there is nothing.
    /// </summary>
    [ObservableProperty] private string _emptyMessage = "";

    /// <summary>What the "whole PC at once" card says about the newest restore point.</summary>
    [ObservableProperty] private string _restorePointText = "";

    /// <summary>
    /// True while the tab is on screen. Set by <see cref="MainWindowViewModel.SetActive"/>.
    /// </summary>
    /// <remarks>
    /// Becoming visible looks again. Every other tab can make or put back a change while this one is out of sight —
    /// Apply on Performance Mode records a new original, Stop on Gaming Profile ends a session — so a list read once
    /// would offer a change that is gone and miss one that is new.
    /// </remarks>
    [ObservableProperty] private bool _isActive;

    /// <summary>The look the tab started when it was last shown. Internal so a test can await it.</summary>
    internal Task ShownRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>The look for the newest restore point the tab last started. Internal so a test can await it.</summary>
    internal Task RestorePointLookup { get; private set; } = Task.CompletedTask;

    /// <summary>Builds the tab and starts its first look at the kept copies.</summary>
    /// <param name="service">What the tab lists and puts back.</param>
    /// <param name="navigation">Opens the tab a row is reviewed on.</param>
    /// <param name="clock">What <see cref="RestorePointPatience"/> is measured on. A test holds it.</param>
    public UndoChangesViewModel(IUndoChangesService service, INavigationService navigation, TimeProvider? clock = null)
    {
        _service = service;
        _navigation = navigation;
        _clock = clock ?? TimeProvider.System;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Looking for changes SysManager can put back…";
        RestorePointText = IsElevated
            ? LookingForRestorePoint
            : DescribeRestorePoint(new RestorePointLook(null, Listed: false), isElevated: false);
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(LookAsync);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy))
        {
            PutBackCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value) return;

        // Not while the first look is still running: the tab is shown the moment it is built, and that look is already
        // the fresh one.
        if (InitializationComplete.IsCompleted)
            ShownRefresh = LookAsync();

        // Only on screen: asking Windows for its restore points costs a PowerShell session, and a tab nobody opens has
        // no card to fill.
        LookForRestorePointAgain();
    }

    /// <summary>Looks at every kept copy again, and for the newest restore point.</summary>
    /// <remarks>
    /// Off while a look or a put-back runs. The shell asks before it runs F5, and a held key would otherwise queue a
    /// look and a restore point question for every repeat.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        LookForRestorePointAgain();
        await LookAsync();
    }

    private bool CanRefresh() => !IsBusy;

    // One question to Windows at a time: each queues a PowerShell session on the runner that restore points are made
    // through, and a second one asked while the first is out would only come back with the same answer.
    private void LookForRestorePointAgain()
    {
        if (RestorePointLookup.IsCompleted) RestorePointLookup = LookForRestorePointAsync();
    }

    // Looks at every kept copy again — or, while a look or a put-back is running, once that ends.
    private Task LookAsync()
    {
        if (IsBusy)
        {
            _lookAgain = true;
            return Task.CompletedTask;
        }

        return _scanning = ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Looking for changes SysManager can put back…";
        try
        {
            Show(await Task.Run(() => _service.ScanAsync()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Not expected — each copy's own failures are caught where it is read — so said plainly, rather than the
            // page staying on "Looking…" with nothing under it.
            Log.Warning(ex, "Undo Changes could not look for changes");
            var said = $"SysManager could not look for changes just now: {ex.Message} Look again in a moment.";
            (EmptyTitle, EmptyMessage) = ("Could not look for changes", said);
            HasLooked = true;
            StatusMessage = said;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }

        if (_lookAgain)
        {
            _lookAgain = false;
            await LookAsync();
        }
    }

    // The newest restore point, which Windows names only to an administrator: a standard user is told that without
    // asking. Outside Busy and apart from the look, so the changes never wait for it.
    private async Task LookForRestorePointAsync()
    {
        if (IsDisposed || !IsElevated) return;
        RestorePointText = LookingForRestorePoint;

        var ct = _cts.Token;
        RestorePointLook look;
        try
        {
            // The wait is bounded, not the question (#2606). It waits on System Restore and the shadow copy service, and
            // with no end of its own the card stayed on "Looking…" for as long as they did not answer, and Refresh would
            // not ask again while it was out. An answer that comes after the minute is dropped; the next look asks again.
            // Closing the tab still ends the wait by calling the question off, not by abandoning it.
            look = await Task.Run(() => _service.LookForRestorePointAsync(ct)).WaitAsync(RestorePointPatience, _clock);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The tab was disposed while Windows was asked: there is no card left to fill.
            return;
        }
        catch (TimeoutException)
        {
            if (IsDisposed) return;
            Log.Warning("Undo Changes: Windows did not answer about its restore points within {Patience}", RestorePointPatience);
            RestorePointText = RestorePointNotAnswered;
            return;
        }

        RestorePointText = DescribeRestorePoint(look, isElevated: true);
    }

    /// <summary>
    /// Puts one change back, after asking with what it will change — or, for a row that is reviewed on its own tab,
    /// opens that tab.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPutBack))]
    private async Task PutBackAsync(UndoChangeRow? row)
    {
        if (row is null || IsBusy) return;
        var change = row.Change;

        if (change.OpensTab is { } tab)
        {
            _navigation.GoTo(tab);
            return;
        }

        if (!row.CanAct)
        {
            StatusMessage = $"Putting back {UndoChangesService.Name(change.Kind)} needs administrator rights. Use Run as "
                + "administrator at the top of the page.";
            return;
        }

        if (!DialogService.Instance.Confirm(change.Question, change.QuestionTitle)) return;

        // The question is modal, and a look can start under it: the window shown again after being minimised makes the
        // tab active. Wait for that look to end rather than run beside it.
        if (IsBusy) await Task.WhenAny(_scanning);
        if (IsBusy)
        {
            StatusMessage = "Still looking for changes, so nothing was put back yet. Try again in a moment.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = ProgressText(change.Kind);
        UndoOutcome? outcome = null;
        try
        {
            outcome = await Task.Run(() => _service.PutBackAsync(change));
            if (outcome.ChangedSomething)
                ActivityLogService.Instance.Log("Undo Changes", ActivityText(change.Kind));

            // What is left to put back, read after the change rather than worked out from it.
            Show(await Task.Run(() => _service.ScanAsync()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Not expected, as above. What did happen is still said: the put-back's outcome when it came back, with the
            // list it could not read again, and otherwise the failure itself.
            Log.Warning(ex, "Undo Changes could not finish putting {Kind} back", change.Kind);
            outcome = outcome is null
                ? new UndoOutcome(UndoOutcomeKind.Failed,
                    $"Putting back {UndoChangesService.Name(change.Kind)} failed: {ex.Message}")
                : outcome with { Message = $"{outcome.Message} The list could not be read again just now; look again in a moment." };
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            // A look asked for meanwhile is answered by the list read after the change. When that could not be read,
            // the line below says what happened instead, and a look run now would write over it.
            _lookAgain = false;
            // Last, after the list writes its own count to the status line — and here, so a look that failed after the
            // put-back cannot lose what happened to the change.
            if (outcome is not null) StatusMessage = outcome.Message;
        }
    }

    private bool CanPutBack(UndoChangeRow? row) => row is { CanAct: true } && !IsBusy;

    /// <summary>Opens the tab a link on the page names.</summary>
    [RelayCommand]
    private void OpenTab(string? navId)
    {
        if (!string.IsNullOrWhiteSpace(navId)) _navigation.GoTo(navId);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    private void Show(UndoScan scan)
    {
        Changes.ReplaceWith(scan.Changes.Select(c => new UndoChangeRow(c, canAct: !c.NeedsAdmin || IsElevated)));
        HasChanges = Changes.Count > 0;
        HasLooked = true;
        StatusMessage = Summarize(scan);
        (EmptyTitle, EmptyMessage) = scan.Problems.Count == 0 && !scan.PerformanceWaitsForGameMode
            ? ("Nothing to put back", "Nothing SysManager changed is waiting to be put back. The sections below still apply.")
            : ("Nothing found to put back", Summarize(scan));
        PutBackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the status line says after a look. Pure, so the wording can be pinned.</summary>
    internal static string Summarize(UndoScan scan)
    {
        List<string> said = [];

        // "Nothing to put back" only when nothing stood in the way of looking: a copy that could not be used, or Performance
        // Mode held back for game mode, is not "nothing", so then the sentences below speak alone.
        if (scan.Changes.Count > 0 || (scan.Problems.Count == 0 && !scan.PerformanceWaitsForGameMode))
        {
            said.Add(scan.Changes.Count switch
            {
                0 => "Nothing to put back.",
                1 => "1 change can be put back.",
                var n => $"{n} changes can be put back.",
            });
        }

        if (scan.PerformanceWaitsForGameMode)
        {
            said.Add(scan.GameModeNotKnown
                ? "Performance Mode can be put back once SysManager can read whether game mode was left on."
                : "Performance Mode can be put back once game mode is off.");
        }

        var unreadable = Named(UndoProblemKind.Unreadable);
        if (unreadable.Length > 0)
            said.Add($"SysManager could not read what it kept for {unreadable} just now; look again in a moment.");

        var notCompared = Named(UndoProblemKind.CannotCompare);
        if (notCompared.Length > 0)
            said.Add($"SysManager could not compare {notCompared} with what it kept just now; look again in a moment.");

        var unusable = Named(UndoProblemKind.Unusable);
        if (unusable.Length > 0)
            said.Add($"SysManager could not use what it kept for {unusable}.");

        var damaged = Named(UndoProblemKind.Damaged);
        if (damaged.Length > 0)
            said.Add($"What SysManager kept for {damaged} is damaged, so it cannot be put back.");

        return string.Join(" ", said);

        string Named(UndoProblemKind why) => UndoChangesService.JoinAnd(
            scan.Problems.Where(p => p.Why == why).Select(p => UndoChangesService.Name(p.Kind)).ToList());
    }

    /// <summary>What the "whole PC at once" card says about the newest restore point. Pure, so it can be pinned.</summary>
    /// <remarks>
    /// Windows lists restore points only to an administrator, so for a standard user the card says that rather than
    /// "there are none" — which would tell someone with several that they have no way back (#2476).
    /// </remarks>
    internal static string DescribeRestorePoint(RestorePointLook look, bool isElevated)
    {
        if (!isElevated) return "Windows shows the list of restore points only to an administrator.";
        if (!look.Listed) return "Windows would not list the restore points just now.";
        return look.Newest is { } newest
            ? $"Newest: \"{newest.Description}\", {newest.CreationTime.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}"
            : "Windows lists no restore points on this PC.";
    }

    /// <summary>What the status line says while one change is being put back, in the words its button used.</summary>
    internal static string ProgressText(UndoChangeKind kind) => kind switch
    {
        UndoChangeKind.PerformanceMode => "Putting Performance Mode's original settings back…",
        UndoChangeKind.Services => "Turning the services back on…",
        UndoChangeKind.HostsFile => "Restoring the hosts file from the copy beside it…",
        UndoChangeKind.EnvironmentVariables => "Restoring the environment variables from SysManager's copy…",
        UndoChangeKind.GamingProfile => "Turning game mode off…",
        _ => "Putting the change back…",
    };

    /// <summary>What the activity log records for a change put back, in the Dashboard's "Recent activity" words.</summary>
    internal static string ActivityText(UndoChangeKind kind) => kind switch
    {
        UndoChangeKind.PerformanceMode => "Put Performance Mode's original settings back",
        UndoChangeKind.Services => "Turned services back on",
        UndoChangeKind.HostsFile => "Restored the hosts file from the copy beside it",
        UndoChangeKind.EnvironmentVariables => "Restored the environment variables from SysManager's copy",
        UndoChangeKind.GamingProfile => "Turned game mode off",
        _ => "Put a change back",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            try
            {
                _cts.Cancel();
            }
            catch (AggregateException ex)
            {
                // Calling it off stops the PowerShell session asking, and a session that broke can throw doing so.
                Log.Debug(ex, "Undo Changes: calling off the restore point question threw");
            }
            finally
            {
                _cts.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
