// SysManager · PrivacyMonitorViewModel — webcam/mic/location access history
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Privacy Monitor tab. Shows which apps recently used the camera,
/// microphone, or location, read from the Windows consent store. Read-only history — the
/// "Open privacy settings" action hands off to Windows for granting/revoking permissions,
/// which SysManager never changes itself.
/// </summary>
public sealed partial class PrivacyMonitorViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly Func<CancellationToken, Task<PrivacyAccessReport>> _read;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<PrivacyAccessEntry> Entries { get; } = new();

    [ObservableProperty] private bool _hasEntries;

    // Set from each read, so the empty state never claims a capability was checked when it could not be read (#2503).
    [ObservableProperty] private string _emptyTitle = "No access recorded";
    [ObservableProperty] private string _emptyMessage = "Windows hasn't logged any camera, microphone, or location access yet.";

    public PrivacyMonitorViewModel(PrivacyMonitorService service) : this(service.ReadAsync) { }

    /// <summary>Test seam: the consent-store read, so a failed one can be supplied without the registry.</summary>
    internal PrivacyMonitorViewModel(Func<CancellationToken, Task<PrivacyAccessReport>> read)
    {
        _read = read;
        StatusMessage = "Reading access history…";
        PropertyChanged += OnVmPropertyChanged;
        // Read off the UI thread so a registry walk (or a corrupt-hive failure) can never block or
        // crash the UI. This tab is LAZY — NavItem.ContentFactory builds it on first open, not at
        // startup (the eager set is Dashboard, DarkMode and About; see the list above NavItems) — so
        // what this protects is the first navigation into the tab, not app launch. The Task.Run is
        // still required: without it the walk would run on the dispatcher and freeze the window while
        // the tab opens.
        InitializeAsync(RefreshAsync);
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy)) RefreshCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading access history…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var report = await _read(_cts.Token).ConfigureAwait(true);
            if (report.Entries.Count == 0 && report.Unreadable.Count == PrivacyMonitorService.CapabilityLabels.Count)
            {
                // Nothing could be read. A failed read changes nothing on screen: what was listed stays listed,
                // and the status line and empty state say the read failed rather than that nothing was used (#2503).
                (EmptyTitle, EmptyMessage) = DescribeEmpty(report);
                StatusMessage = Entries.Count == 0
                    ? "Could not read the camera, microphone, or location history. Press Refresh to try again."
                    : "Could not read the camera, microphone, or location history, so the list below is from the last read.";
                return;
            }

            Entries.ReplaceWith(report.Entries);
            HasEntries = Entries.Count > 0;
            (EmptyTitle, EmptyMessage) = DescribeEmpty(report);
            StatusMessage = Describe(report);
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally { IsBusy = false; }
    }

    /// <summary>The status line for a read. Pure, so every combination is testable without the registry.</summary>
    /// <remarks>
    /// Names only what was read: "No camera, microphone, or location access has been recorded yet." is a claim about
    /// all three, and was made when none of them could be read (#2503).
    /// </remarks>
    internal static string Describe(PrivacyAccessReport report)
    {
        var entries = report.Entries;
        var read = ReadCapabilities(report);
        if (entries.Count == 0 && read.Count == 0)
            return "Could not read the camera, microphone, or location history.";

        var inUse = entries.Count(e => e.InUse);
        var text = entries.Count == 0
            ? $"No {Join(read, "or")} access has been recorded yet."
            : inUse > 0
                ? $"{entries.Count} access record(s) — {inUse} device(s) in use right now."
                : report.Unreadable.Count == 0
                    ? $"{entries.Count} access record(s) across {Join(read, "and")}."
                    : $"{entries.Count} access record(s).";

        return report.Unreadable.Count == 0
            ? text
            : $"{text} Could not read the {Join(Lower(report.Unreadable), "and")} history.";
    }

    /// <summary>The empty state's title and message for a read, naming only what was read.</summary>
    internal static (string Title, string Message) DescribeEmpty(PrivacyAccessReport report)
    {
        var read = ReadCapabilities(report);
        if (read.Count == 0)
            return ("Access history could not be read",
                "Windows did not let SysManager read which apps used the camera, microphone, or location. "
                + "Press Refresh to try again.");

        var message = $"Windows hasn't logged any {Join(read, "or")} access yet.";
        if (report.Unreadable.Count > 0)
            message += $" SysManager could not read the {Join(Lower(report.Unreadable), "and")} history.";
        return ("No access recorded", message);
    }

    private static List<string> ReadCapabilities(PrivacyAccessReport report) =>
        Lower([.. PrivacyMonitorService.CapabilityLabels.Where(label => !report.Unreadable.Contains(label))]);

    private static List<string> Lower(IReadOnlyList<string> labels) =>
        [.. labels.Select(label => label.ToLowerInvariant())];

    /// <summary>"a", "a or b", "a, b, or c".</summary>
    private static string Join(IReadOnlyList<string> items, string conjunction) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} {conjunction} {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, {conjunction} {items[^1]}",
    };

    [RelayCommand]
    private void OpenPrivacySettings()
    {
        // Hand off to Windows for actually granting/revoking — SysManager never changes
        // capability permissions itself.
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy") { UseShellExecute = true })?.Dispose();
            StatusMessage = "Opened Windows privacy settings.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warning("Could not open privacy settings: {Error}", ex.Message);
            StatusMessage = "Couldn't open Windows privacy settings.";
        }
    }

    /// <summary>
    /// Writes the access history to a CSV the user picks a location for.
    /// </summary>
    /// <remarks>
    /// The tab's whole output is a list that could only be read on screen, so the realistic move — showing
    /// someone which app used the camera — meant photographing the monitor. The file goes only where the
    /// dialog is pointed; nothing is written to a default location and nothing leaves the machine.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasEntries))]
    private async Task ExportCsvAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-Privacy-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture)}.csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var csv = PrivacyMonitorService.ToCsv(Entries);
            await File.WriteAllTextAsync(dlg.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusMessage = $"Exported {Entries.Count} entry(ies) to {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("Access history exported", Path.GetFileName(dlg.FileName));
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
    }

    partial void OnHasEntriesChanged(bool value) => ExportCsvCommand.NotifyCanExecuteChanged();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
