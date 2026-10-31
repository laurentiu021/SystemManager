// SysManager · WindowsFeaturesViewModel — toggle Windows optional features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Windows Features tab — lists optional features, allows enable/disable toggle.
/// Requires administrator privileges for modifications.
/// </summary>
public sealed partial class WindowsFeaturesViewModel : ElevatedViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ScanCommand;

    /// <inheritdoc/>
    /// <remarks>
    /// Only while the list is read. Escape stopped a change too, and that ended powershell.exe and its DismHost.exe
    /// while Windows was applying it, after which nothing looked at what was left (#2834). A change cannot be stopped
    /// here.
    /// </remarks>
    protected internal override IRelayCommand? EscapeCancel =>
        IsScanning ? CancelCommand : null;

    private readonly WindowsFeaturesService _service;
    private readonly ISessionRestorePoint _restorePoint;
    private readonly IFeatureParentsService _parents;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _toggleCts;

    public BulkObservableCollection<WindowsFeature> AllFeatures { get; } = new();
    public BulkObservableCollection<WindowsFeature> FilteredFeatures { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _featureCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private string _summary = "Click Scan to list Windows optional features.";
    [ObservableProperty] private bool _pendingReboot;

    /// <summary>True while the list is read: the one run on this tab that Cancel and Escape stop.</summary>
    [ObservableProperty] private bool _isScanning;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public WindowsFeaturesViewModel(
        WindowsFeaturesService service, ISessionRestorePoint restorePoint, IFeatureParentsService parents)
    {
        _service = service;
        _restorePoint = restorePoint;
        _parents = parents;
        // Scan and ToggleFeature both drive the shared WindowsFeaturesService PowerShell
        // runner (each subscribes its own LineReceived handler). Running them concurrently
        // would cross-contaminate the captured output (the SFC/DISM bug class). Re-evaluate
        // both commands' CanExecute when IsBusy flips so they are mutually exclusive.
        PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ScanCommand.NotifyCanExecuteChanged();
        ToggleFeatureCommand.NotifyCanExecuteChanged();
    }


    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        IsScanning = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Querying Windows optional features…";
        // The list stays until the new one arrives. It was cleared here, so a scan cancelled or refused left an empty
        // grid while the counts still described the features before it (#2729).
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        try
        {
            var list = await _service.ListFeaturesAsync(_scanCts.Token);
            AllFeatures.ReplaceWith(list);

            ApplyFilter();
            EnabledCount = AllFeatures.Count(f => f.IsEnabled);
            if (AllFeatures.Any(f => f.RequiresReboot)) PendingReboot = true;
            StatusMessage = $"Found {AllFeatures.Count} features ({EnabledCount} enabled).";
            ToastService.Instance.Show("Windows Features scan complete", $"Found {AllFeatures.Count} features");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            // Unelevated, the query always fails, and a normal launch is unelevated — so that case gets the
            // reason and the way out rather than an exit code (#2451).
            StatusMessage = IsElevated ? $"Error: {ex.Message}" : ListNeedsAdminMessage;
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>What the tab says when an unelevated scan is refused.</summary>
    internal const string ListNeedsAdminMessage =
        "Windows only lists its optional features to an administrator. Use \"Run as administrator\" above to see them.";

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleFeatureAsync(WindowsFeature? feature)
    {
        if (feature is null) return;

        try
        {
            if (!IsElevated)
            {
                StatusMessage = "Administrator privileges required to modify features.";
                return;
            }

            var wasEnabled = feature.IsEnabled;
            var action = wasEnabled ? "disable" : "enable";

            // Enabling passes -All, which turns on every feature this one needs, and the confirmation named only the
            // clicked one: an IIS sub-feature brought the IIS web server with it, unannounced (#2834).
            var parents = await ReadParentsAsync(feature);
            if (!DialogService.Instance.Confirm(
                $"Are you sure you want to {action} '{feature.DisplayName}'?\n\n" +
                DescribeParents(wasEnabled, parents) +
                "This may require a system reboot to take effect." +
                _restorePoint.ConfirmationNotice,
                $"Confirm {action} feature"))
                return;

            // Enabling or disabling a feature is DISM servicing of the running image, the same image System Fixes'
            // SFC and DISM repairs and Cleanup's component-store cleanup work on, so it takes the lock they take and
            // cannot run in the middle of one (#2484). Before the restore point, which is a system change too.
            using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Windows feature change");
            if (opLock is null)
            {
                StatusMessage = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification)} is already running.";
                return;
            }

            IsBusy = true;
            // The footer's bar shows while busy and moves only when indeterminate, and this set busy alone, so the bar
            // sat empty and still through a DISM run that can take minutes (#2812). The scan above sets both.
            IsProgressIndeterminate = true;
            feature.Status = wasEnabled ? "Disabling…" : "Enabling…";
            StatusMessage = $"{(wasEnabled ? "Disabling" : "Enabling")} {feature.DisplayName}…";
            _toggleCts?.Cancel();
            _toggleCts?.Dispose();
            _toggleCts = new CancellationTokenSource();

            try
            {
                // Before the change, never after. This is the tab where the snapshot matters most and is
                // also genuinely useful: unlike removing a Store app, an optional feature is registry and
                // system-file state, which is exactly what System Restore captures. Taken after the
                // confirmation above, so declining costs the user nothing. Best-effort — the toggle is
                // never gated on it, because Windows refuses a second point within 24h and System Restore
                // is switched off entirely on many consumer machines.
                var snapshotTaken = await _restorePoint
                    .EnsureAsync("SysManager Windows Features", _toggleCts.Token).ConfigureAwait(true);

                var result = wasEnabled
                    ? await _service.DisableFeatureAsync(feature.Name, _toggleCts.Token)
                    : await _service.EnableFeatureAsync(feature.Name, _toggleCts.Token);

                // What Windows says the feature is now, not what was asked for: a change that failed can still have
                // done part of its work, and one that went through can be waiting for a restart (#2834).
                var now = await ReadBackAsync(feature.Name, _toggleCts.Token);

                // And the features it turned on with this one, whose rows still read off.
                var turnedOn = wasEnabled ? 0 : await ShowParentsAsync(feature, parents);

                if (result.Success)
                {
                    feature.IsEnabled = now?.IsEnabled ?? !wasEnabled;
                    feature.RequiresReboot = result.RebootRequired || now?.RequiresReboot == true;
                    feature.Status = feature.RequiresReboot ? "Reboot required" : "Done";

                    if (feature.IsEnabled == wasEnabled && !feature.RequiresReboot)
                    {
                        StatusMessage = $"Windows reported {feature.DisplayName} as {action}d, but it still shows as "
                            + $"{(wasEnabled ? "enabled" : "disabled")}. Scan again to check.";
                    }
                    else
                    {
                        // Only when Windows really made one. Not mentioned on the failure path below: Windows
                        // refused the change, and a snapshot beside "Failed" reads as something to roll back.
                        var rp = snapshotTaken ? " Restore point created." : "";
                        StatusMessage = $"{feature.DisplayName} {action}d" +
                            (feature.RequiresReboot ? " — reboot required." : ".") +
                            (turnedOn switch
                            {
                                0 => "",
                                1 => " Windows also turned on 1 feature it needs.",
                                _ => $" Windows also turned on {turnedOn} features it needs.",
                            }) + rp;
                    }
                    Log.Information("Feature {Feature} toggled to {State}, reboot={Reboot}",
                        feature.Name, feature.IsEnabled ? "Enabled" : "Disabled", feature.RequiresReboot);
                }
                else
                {
                    if (now is not null)
                    {
                        feature.IsEnabled = now.IsEnabled;
                        feature.RequiresReboot = now.RequiresReboot;
                    }
                    feature.Status = "Failed";
                    StatusMessage = DescribeFailure(action, feature.DisplayName, result);
                }
                if (feature.RequiresReboot) PendingReboot = true;
                EnabledCount = AllFeatures.Count(f => f.IsEnabled);
            }
            catch (OperationCanceledException)
            {
                feature.Status = "Cancelled";
                StatusMessage = "Operation cancelled.";
            }
            catch (InvalidOperationException ex)
            {
                feature.Status = "Error";
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                IsProgressIndeterminate = false;
            }
        }
        finally
        {
            // The switch turns itself over when clicked, and its binding is one-way, so only this turns it back to the
            // feature's state: after a change declined, refused or failed it showed the opposite of the State column,
            // and the next click asked about the wrong change (#2739).
            feature.ResetSwitch();
        }
    }

    /// <summary>The feature's state as Windows reports it after a change, or null when it could not be read.</summary>
    private async Task<WindowsFeature?> ReadBackAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _service.ReadFeatureAsync(name, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Windows feature {Feature} could not be read back after a change: {Error}", name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// What the feature needs, as DISM reports it, or null when DISM could not be asked. The bar moves while it is
    /// read, because opening a DISM session takes a moment, and the status line goes back to what it said.
    /// </summary>
    private async Task<IReadOnlyList<FeatureParent>?> ReadParentsAsync(WindowsFeature feature)
    {
        var said = StatusMessage;
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = $"Checking what {feature.DisplayName} needs…";
        try
        {
            return await _parents.ReadAsync(feature.Name).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            StatusMessage = said;
        }
    }

    /// <summary>
    /// The confirmation's sentence about the features this one needs: the ones turning it on turns on too, or the ones
    /// that stay on when it is turned off. Ends in a blank line, or is empty when there is nothing to say.
    /// </summary>
    internal static string DescribeParents(bool disabling, IReadOnlyList<FeatureParent>? parents)
    {
        if (parents is null)
            return disabling ? "" : "Windows also turns on any other features it needs.\n\n";

        var names = parents.Where(p => p.IsEnabled == disabling)
            .Select(p => WindowsFeaturesService.HumanizeName(p.Name))
            .ToList();
        if (names.Count == 0) return "";

        var list = names.Count <= MaxNamed
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxNamed))} and {names.Count - MaxNamed} more";
        return disabling
            ? $"The features it needs stay on: {list}.\n\n"
            : $"Windows turns these on with it, because it needs them: {list}.\n\n";
    }

    /// <summary>How many features a confirmation names before it counts the rest.</summary>
    private const int MaxNamed = 10;

    /// <summary>
    /// Reads again the features that were off before turning this one on, and writes their rows from what DISM reports.
    /// Returns how many of them are on now.
    /// </summary>
    private async Task<int> ShowParentsAsync(WindowsFeature feature, IReadOnlyList<FeatureParent>? before)
    {
        var off = before?.Where(p => !p.IsEnabled).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (off is not { Count: > 0 }) return 0;
        if (await _parents.ReadAsync(feature.Name).ConfigureAwait(true) is not { } after) return 0;

        var turnedOn = 0;
        foreach (var parent in after.Where(p => off.Contains(p.Name)))
        {
            if (parent.IsEnabled) turnedOn++;
            if (parent.Pending) PendingReboot = true;
            if (AllFeatures.FirstOrDefault(f => f.Name.Equals(parent.Name, StringComparison.OrdinalIgnoreCase))
                is not { } row)
                continue;
            row.IsEnabled = parent.IsEnabled;
            row.RequiresReboot = parent.Pending;
            row.Status = parent.Pending ? "Reboot required" : parent.IsEnabled ? "Enabled" : "Disabled";
        }
        return turnedOn;
    }

    // DISM's codes for a feature whose files Windows cannot get, as Microsoft documents them for .NET Framework 3.5
    // (KB 2734782), and the Win32 code for a service that is turned off.
    private const int DownloadFailed = unchecked((int)0x800F0906);    // CBS_E_DOWNLOAD_FAILURE
    private const int SourceMissing = unchecked((int)0x800F081F);     // CBS_E_SOURCE_MISSING
    private const int DownloadForbidden = unchecked((int)0x800F0907); // CBS_E_GROUPPOLICY_DISALLOWED
    private const int ServiceDisabled = unchecked((int)0x80070422);   // ERROR_SERVICE_DISABLED

    /// <summary>
    /// The status line for a change Windows refused: DISM's code, and a plain sentence for the codes with a known way
    /// out, or else DISM's own reason in Windows' language.
    /// </summary>
    /// <remarks>
    /// Every failure said "Check permissions", on a tab that offers a change only to an administrator (#2834).
    /// </remarks>
    internal static string DescribeFailure(string action, string feature, FeatureChangeResult result)
    {
        if (result.ErrorCode is not { } code)
            return $"Failed to {action} {feature}. Windows did not say why.";

        var reason = code switch
        {
            DownloadFailed => "Windows could not download the files it needs from Windows Update. Check that this PC "
                              + "is online, then try again.",
            SourceMissing => "Windows could not find the files it needs. It gets them from Windows Update, so check "
                             + "that this PC is online and that Windows Update works, then try again.",
            DownloadForbidden => "A Windows policy on this PC stops it from downloading the files it needs from "
                                 + "Windows Update.",
            ServiceDisabled => "A Windows service it needs is turned off, such as Windows Update. Turn it back on, "
                               + "then try again.",
            _ => result.ErrorMessage,
        };
        var head = $"Failed to {action} {feature} (0x{code:X8}).";
        return reason.Length == 0 ? head : $"{head} {reason}";
    }

    /// <summary>Stops a scan, and only a scan: see <see cref="EscapeCancel"/>.</summary>
    [RelayCommand]
    private void Cancel() => _scanCts?.Cancel();

    private bool CanToggle(WindowsFeature? _) => !IsBusy;

    /// <summary>Gate for Scan so it can't run while a feature toggle is in flight (and vice
    /// versa) — both share one PowerShell runner whose LineReceived output would otherwise
    /// be captured by both operations at once.</summary>
    private bool NotBusy => !IsBusy;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _scanCts?.Cancel(); _scanCts?.Dispose();
            _toggleCts?.Cancel(); _toggleCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyFilter()
    {
        IEnumerable<WindowsFeature> source = AllFeatures;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            source = source.Where(feat =>
                feat.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                feat.Category.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        FilteredFeatures.ReplaceWith(source);
        FeatureCount = FilteredFeatures.Count;
        Summary = $"{FeatureCount} features{(AllFeatures.Count != FeatureCount ? $" (of {AllFeatures.Count} total)" : "")}";
    }
}
