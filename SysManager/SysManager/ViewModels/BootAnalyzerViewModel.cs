// SysManager · BootAnalyzerViewModel — boot-time history and slow-component breakdown
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Boot Analyzer tab. Reads boot-performance history and slow-component
/// events from the Windows Diagnostics-Performance log. Read-only; reading that log needs
/// administrator, so the tab shows the standard elevation banner when not elevated.
/// </summary>
public sealed partial class BootAnalyzerViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly BootAnalyzerService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<BootRecord> Boots { get; } = new();
    public BulkObservableCollection<BootDegradation> Degradations { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private BootRecord? _latestBoot;
    [ObservableProperty] private string _trend = "";

    private readonly INavigationService _navigation;

    public BootAnalyzerViewModel(BootAnalyzerService service, INavigationService navigation)
        : this(service, navigation, AdminHelper.IsElevated) { }

    /// <summary>Test seam: the same view-model with the elevation probe supplied.</summary>
    /// <param name="service">The boot-history reader.</param>
    /// <param name="navigation">Opens the tab that can switch off a slow component.</param>
    /// <param name="isElevated">
    /// Whether the process is elevated. Injected so a test can drive what a refused read says in both cases:
    /// without elevation the answer is the administrator hint, with it the read genuinely failed.
    /// </param>
    internal BootAnalyzerViewModel(BootAnalyzerService service, INavigationService navigation, Func<bool> isElevated)
    {
        _service = service;
        _navigation = navigation;
        IsElevated = (isElevated ?? throw new ArgumentNullException(nameof(isElevated)))();
        StatusMessage = "Reading boot performance history…";
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(RefreshAsync);
    }

    /// <summary>
    /// Opens the tab that can switch off the component this row blames, pre-filtered to its name.
    /// </summary>
    /// <remarks>
    /// The diagnose-to-fix pair this tab existed half of: it named the exact application or service that
    /// cost the user seconds of boot time, and then offered no route to it (#1504). The destination is the
    /// row's own <c>NavTargetId</c>, so the mapping from component kind to tab lives with the model that
    /// knows the kind.
    /// <para>The name is passed as a filter, which is the difference between arriving at a list of every
    /// service on the machine and arriving at the one just blamed. Startup Manager has no search box, so
    /// it simply opens — the filter is ignored by anything that does not implement <c>IFilterable</c>,
    /// which is why the caller can always pass it.</para>
    /// </remarks>
    [RelayCommand]
    private void OpenFixFor(BootDegradation? degradation)
    {
        if (degradation is null || !degradation.CanNavigate) return;
        _navigation.GoTo(degradation.NavTargetId, degradation.Name);
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
        IsProgressIndeterminate = true;
        StatusMessage = "Reading boot performance history…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var boots = await _service.ReadBootsAsync(20, _cts.Token).ConfigureAwait(true);
            var degr = await _service.ReadDegradationsAsync(60, _cts.Token).ConfigureAwait(true);

            // A read that failed is not an empty history (#2500). Without elevation the log refuses every read, and
            // the banner and this line say why; with it, the read genuinely failed. Either way what is on screen
            // stays, so a refresh that fails does not empty the tab.
            if (boots is null)
            {
                StatusMessage = IsElevated
                    ? "Windows' boot history could not be read. Try Refresh in a moment."
                    : "Reading boot history requires administrator — use \"Run as administrator\".";
                return;
            }

            Boots.ReplaceWith(boots);
            if (degr is not null) Degradations.ReplaceWith(degr);
            LatestBoot = boots.Count > 0 ? boots[0] : null;
            HasData = boots.Count > 0;
            Trend = ComputeTrend(boots);

            StatusMessage = boots.Count == 0
                ? "No boot performance events found yet (a few reboots are needed to build history)."
                : degr is null
                    ? $"{boots.Count} boots analyzed; the slow-component list could not be read. Latest boot: {boots[0].BootSecondsDisplay}."
                    : $"{boots.Count} boots analyzed; {degr.Count} slow-component events. Latest boot: {boots[0].BootSecondsDisplay}.";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Compares the latest boot to the average of the rest to describe a trend.</summary>
    private static string ComputeTrend(IReadOnlyList<BootRecord> boots)
    {
        if (boots.Count < 3) return "";
        var latest = boots[0].BootTimeMs;
        var rest = boots.Skip(1).ToList();
        var avg = rest.Average(b => b.BootTimeMs);
        if (avg <= 0) return "";
        var pct = (latest - avg) / avg * 100.0;
        return pct switch
        {
            > 15 => string.Create(CultureInfo.InvariantCulture, $"Last boot was {pct:F0}% slower than your recent average ({avg / 1000.0:F1} s)."),
            < -15 => string.Create(CultureInfo.InvariantCulture, $"Last boot was {-pct:F0}% faster than your recent average ({avg / 1000.0:F1} s)."),
            _ => string.Create(CultureInfo.InvariantCulture, $"Boot time is steady — recent average {avg / 1000.0:F1} s.")
        };
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            App.RequestShutdown();
    }

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
