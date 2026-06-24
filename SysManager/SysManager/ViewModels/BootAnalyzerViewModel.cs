// SysManager · BootAnalyzerViewModel — boot-time history and slow-component breakdown
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
/// ViewModel for the Boot Analyzer tab. Reads boot-performance history and slow-component
/// events from the Windows Diagnostics-Performance log. Read-only; reading that log needs
/// administrator, so the tab shows the standard elevation banner when not elevated.
/// </summary>
public sealed partial class BootAnalyzerViewModel : ViewModelBase
{
    private readonly BootAnalyzerService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<BootRecord> Boots { get; } = new();
    public BulkObservableCollection<BootDegradation> Degradations { get; } = new();

    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private BootRecord? _latestBoot;
    [ObservableProperty] private string _trend = "";

    public BootAnalyzerViewModel(BootAnalyzerService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        StatusMessage = "Reading boot performance history…";
        PropertyChanged += OnVmPropertyChanged;
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
        IsProgressIndeterminate = true;
        StatusMessage = "Reading boot performance history…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var boots = await _service.ReadBootsAsync(20, _cts.Token).ConfigureAwait(true);
            var degr = await _service.ReadDegradationsAsync(60, _cts.Token).ConfigureAwait(true);

            Boots.ReplaceWith(boots);
            Degradations.ReplaceWith(degr);
            LatestBoot = boots.Count > 0 ? boots[0] : null;
            HasData = boots.Count > 0;
            Trend = ComputeTrend(boots);

            if (boots.Count == 0)
                StatusMessage = IsElevated
                    ? "No boot performance events found yet (a few reboots are needed to build history)."
                    : "Reading boot history requires administrator — use \"Run as administrator\".";
            else
                StatusMessage = $"{boots.Count} boots analyzed; {degr.Count} slow-component events. Latest boot: {boots[0].BootSecondsDisplay}.";
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
            > 15 => $"Last boot was {pct:F0}% slower than your recent average ({avg / 1000.0:F1} s).",
            < -15 => $"Last boot was {-pct:F0}% faster than your recent average ({avg / 1000.0:F1} s).",
            _ => $"Boot time is steady — recent average {avg / 1000.0:F1} s."
        };
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            System.Windows.Application.Current?.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PropertyChanged -= OnVmPropertyChanged;
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
