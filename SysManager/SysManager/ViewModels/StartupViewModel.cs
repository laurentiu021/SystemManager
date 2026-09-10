// SysManager · StartupViewModel — manage programs that run at boot
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Startup Manager tab — lists all programs that run at Windows boot
/// and lets the user enable/disable them non-destructively.
/// </summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    private readonly StartupService _service;
    private readonly Func<bool> _isElevatedProbe;

    /// <summary>
    /// Where Windows' boot-delay measurements come from. A delegate rather than the service itself, so a test
    /// can prove the elevation gate by whether this is called at all — the outcome cannot prove it, because
    /// the real reader also returns nothing when the rights are missing.
    /// </summary>
    private readonly Func<Task<IReadOnlyList<BootDegradation>>> _readDegradations;

    private readonly List<StartupEntry> _allEntries = [];

    public BulkObservableCollection<StartupEntry> Entries { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _scanSummary = "Click Scan to discover startup items.";
    [ObservableProperty] private bool _hideWindowsEntries;

    public StartupViewModel(StartupService service, BootAnalyzerService boot)
        : this(service, AdminHelper.IsElevated,
               () => (boot ?? throw new ArgumentNullException(nameof(boot))).ReadDegradationsAsync())
    {
    }

    /// <summary>Test seam: the same view-model with the elevation probe and the boot reader supplied.</summary>
    /// <param name="service">The startup scan.</param>
    /// <param name="isElevated">
    /// How this view-model asks whether the process is elevated. Injected rather than called inline, because
    /// the boot-impact read below happens only when elevated and a test has to be able to drive both sides of
    /// that branch. Same seam and same two-constructor shape as <c>WindowsUpdateViewModel</c>.
    /// </param>
    /// <param name="readDegradations">Windows' boot-delay measurements. See <see cref="_readDegradations"/>.</param>
    internal StartupViewModel(StartupService service, Func<bool> isElevated,
                              Func<Task<IReadOnlyList<BootDegradation>>> readDegradations)
    {
        _service = service;
        _isElevatedProbe = isElevated ?? throw new ArgumentNullException(nameof(isElevated));
        _readDegradations = readDegradations ?? throw new ArgumentNullException(nameof(readDegradations));
        IsElevated = _isElevatedProbe();
        // Scan, EnableAll and ToggleEntry all read or write the same startup registry/task
        // state; running them concurrently could interleave registry writes and produce
        // inconsistent counts. Re-evaluate their CanExecute when IsBusy flips so only one
        // runs at a time. The startup auto-scan calls ScanAsync directly (not via the
        // command), so it is unaffected by the gate.
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(InitAsync);
    }

    /// <summary>Gate so Scan / EnableAll / ToggleEntry can't overlap and interleave their
    /// registry writes.</summary>
    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        EnableAllCommand.NotifyCanExecuteChanged();
        ToggleEntryCommand.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            PropertyChanged -= OnVmPropertyChanged;
        base.Dispose(disposing);
    }

    partial void OnHideWindowsEntriesChanged(bool value) => ApplyFilter();

    private async Task InitAsync()
    {
        try { await ScanAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Startup auto-scan failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Startup auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning startup items…";
        try
        {
            var items = await _service.ScanAsync().ConfigureAwait(false);
            var sorted = items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in sorted)
            {
                var exePath = ExtractExecutablePath(item.Command);
                item.Icon = IconExtractorService.GetIcon(exePath ?? item.Command);
            }

            // Only when elevated. Reading Diagnostics-Performance needs administrator rights, and without
            // them the reader catches the access denial and returns nothing — so an unelevated run would pay
            // for opening an event log on every scan to fill in no figures at all. The banner at the top of
            // the tab is what tells the user why the column is empty.
            if (_isElevatedProbe())
                ApplyBootImpact(sorted, await _readDegradations().ConfigureAwait(false));

            void Publish()
            {
                _allEntries.Clear();
                _allEntries.AddRange(sorted);
                ApplyFilter();
            }

            // Awaited rather than posted, because the next line reads _allEntries.Count. Awaiting a
            // DispatcherOperation keeps that ordering without parking this thread the way the previous
            // synchronous Invoke did: if nothing ever pumps the dispatcher, an un-resumed continuation
            // costs nothing while a blocked thread costs a thread (#2152).
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
                await dispatcher.InvokeAsync(Publish);
            else
                Publish();

            StatusMessage = $"Found {_allEntries.Count} startup items.";
            ToastService.Instance.Show("Scan complete", $"{_allEntries.Count} startup items found");
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ToggleEntryAsync(object? parameter)
    {
        if (parameter is not StartupEntry entry) return;

        // The CheckBox two-way binding has already flipped IsEnabled before
        // this command runs. We use the current (already-flipped) value as
        // the desired new state.
        var desiredState = entry.IsEnabled;
        // Resume on the UI thread (no ConfigureAwait(false)): the continuation reads/writes
        // bound state (UpdateCounts enumerates the DataGrid-bound Entries, entry.IsEnabled,
        // StatusMessage). SetEnabledAsync keeps its own internal ConfigureAwait(false).
        // Matches the documented rule in ServicesViewModel.StartServiceAsync.
        var success = await StartupService.SetEnabledAsync(entry, desiredState);
        if (success)
        {
            UpdateCounts();
            StatusMessage = $"{entry.Name} {(desiredState ? "enabled" : "disabled")}.";
            Log.Information("Startup entry toggled: {Action}", desiredState ? "enabled" : "disabled");
        }
        else
        {
            // Revert the CheckBox state since the operation failed
            entry.IsEnabled = !desiredState;
            StatusMessage = $"Could not toggle {entry.Name} — {entry.StatusText}";
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task EnableAllAsync()
    {
        var toEnable = Entries.Where(e => !e.IsEnabled).ToList();
        if (toEnable.Count == 0)
        {
            StatusMessage = "All startup items are already enabled.";
            return;
        }

        // Enabling every disabled item at once re-arms programs the user (or another tool)
        // deliberately turned off, and each one adds boot time. Confirm the bulk change first —
        // matching the confirm-before-bulk-write pattern used across the app (AppBlocker, Cleanup).
        if (!DialogService.Instance.Confirm(
                $"Re-enable {toEnable.Count} disabled startup item(s)?\n\n" +
                "Each will run again at the next boot. You can disable any of them again individually.",
                "Enable All Startup Items — Confirm"))
            return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            // Report the outcome honestly: SetEnabledAsync returns false (and sets the
            // entry's StatusText) when a registry/task write fails, e.g. an item that
            // needs elevation. Previously every failure was swallowed and the status
            // still claimed "All items enabled."
            var enabled = 0;
            var failed = 0;
            foreach (var entry in toEnable)
            {
                // Resume on the UI thread (no ConfigureAwait(false)): the loop body and the
                // trailing UpdateCounts() enumerate the DataGrid-bound Entries and touch bound
                // state. Off-thread (as before) a concurrent ApplyFilter — e.g. the user flipping
                // "Hide Windows entries" — mutates Entries mid-enumeration and throws
                // "Collection was modified". SetEnabledAsync keeps its own ConfigureAwait(false).
                if (await StartupService.SetEnabledAsync(entry, true))
                    enabled++;
                else
                    failed++;
            }

            UpdateCounts();
            StatusMessage = failed == 0
                ? $"Enabled {enabled} startup item(s)."
                : $"Enabled {enabled} of {toEnable.Count} — {failed} could not be enabled (may require administrator).";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void OpenFileLocation(object? parameter)
    {
        if (parameter is not StartupEntry entry) return;
        try
        {
            var path = ExtractExecutablePath(entry.Command);
            if (path is not null && System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SysManager.Helpers.SystemPaths.ResolveSystemTool("explorer.exe"),
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                })?.Dispose();
            }
            else
            {
                StatusMessage = "File not found — the application may have been moved or uninstalled.";
            }
        }
        catch (InvalidOperationException) { StatusMessage = "Could not open file location."; }
        catch (System.ComponentModel.Win32Exception) { StatusMessage = "Could not open file location."; }
    }

    /// <summary>
    /// Attaches Windows' own boot-delay measurement to any entry it can be attributed to with certainty.
    /// </summary>
    /// <remarks>
    /// Attribution fails closed. A wrong match is worse than no match here: it tells someone a program they
    /// depend on cost them three seconds, and the action this tab offers is to switch that program off. So
    /// only a whole-string, case-insensitive equality counts — against the entry's own name, or against the
    /// executable file name in its command line. Windows reports a component either way depending on the
    /// event ("slowdriver.sys" on one, "Some App" on another), and neither comparison is a substring or a
    /// prefix. Anything less certain leaves the column blank.
    /// <para>Newest measurement wins, because the column claims to describe the last start-up. Windows keeps
    /// several boots of history and an entry that was slow once and is not any more should not keep saying so.</para>
    /// </remarks>
    internal static void ApplyBootImpact(IEnumerable<StartupEntry> entries,
                                         IReadOnlyList<BootDegradation> degradations)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(degradations);
        if (degradations.Count == 0) return;

        foreach (var entry in entries)
        {
            var match = MatchDegradation(entry, degradations);
            if (match is null) continue;

            entry.StartupImpact = match.DurationDisplay;
            entry.StartupImpactMs = match.DurationMs;
            entry.StartupImpactDetail =
                $"Windows measured this delaying start-up by {match.DurationDisplay} on {match.WhenDisplay}. "
                + $"Windows classified it as: {match.Kind}.";
        }
    }

    /// <summary>The newest degradation that is certainly this entry, or null.</summary>
    internal static BootDegradation? MatchDegradation(StartupEntry entry,
                                                      IReadOnlyList<BootDegradation> degradations)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(degradations);

        var exe = ExecutableFileName(entry.Command);
        return degradations
            .Where(d => Identifies(d.Name, entry.Name) || Identifies(d.Name, exe)
                     || Identifies(ExecutableFileName(d.Name), exe))
            .OrderByDescending(d => d.When)
            .FirstOrDefault();

        // Empty never identifies anything — an entry with no name, or a command with no executable in it,
        // would otherwise match every degradation that also came out empty.
        static bool Identifies(string reported, string candidate)
            => candidate.Length > 0 && string.Equals(reported, candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The executable file name in a command line — <c>chrome.exe</c> — or empty when there is none.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ExtractExecutablePath"/>, which probes the filesystem with
    /// <c>File.Exists</c> to decide where an unquoted path ends. That is right for opening a folder in
    /// Explorer and wrong here: matching has to give the same answer on any machine, including a test one
    /// where the path does not exist. Keyed on the extension instead, which is what makes it pure.
    /// </remarks>
    internal static string ExecutableFileName(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";

        string[] extensions = [".exe", ".bat", ".cmd", ".com", ".sys", ".dll"];
        var best = -1;
        var length = 0;
        foreach (var extension in extensions)
        {
            var at = command.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            // Earliest extension in the string, so arguments that name another file are ignored:
            // rundll32.exe C:\thing.dll,Entry is rundll32.exe, not thing.dll.
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                length = extension.Length;
            }
        }
        if (best < 0) return "";

        var end = best + length;
        var start = command.LastIndexOfAny(['\\', '/', '"', ' '], best) + 1;
        return command[start..end];
    }

    private static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var cmd = command.Trim();

        if (cmd.StartsWith('"'))
        {
            var endQuote = cmd.IndexOf('"', 1);
            if (endQuote > 1)
                return cmd[1..endQuote];
        }

        if (System.IO.File.Exists(cmd)) return cmd;

        var extensions = new[] { ".exe", ".bat", ".cmd", ".com" };
        for (var i = 0; i < cmd.Length; i++)
        {
            if (cmd[i] != ' ') continue;
            var candidate = cmd[..i];
            if (System.IO.File.Exists(candidate)) return candidate;
            foreach (var ext in extensions)
            {
                if (candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    break;
                if (System.IO.File.Exists(candidate + ext))
                    return candidate + ext;
            }
        }

        return null;
    }

    private void UpdateCounts()
    {
        EnabledCount = Entries.Count(e => e.IsEnabled);
        DisabledCount = Entries.Count(e => !e.IsEnabled);
        TotalCount = Entries.Count;
        ScanSummary = $"{EnabledCount} enabled · {DisabledCount} disabled · {TotalCount} total";
    }

    private void ApplyFilter()
    {
        var filtered = HideWindowsEntries
            ? _allEntries.Where(e => !IsWindowsEntry(e))
            : _allEntries;

        Entries.ReplaceWith(filtered);

        UpdateCounts();
    }

    private static bool IsWindowsEntry(StartupEntry entry)
        => entry.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
           entry.Command.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) ||
           entry.Command.Contains(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
}
