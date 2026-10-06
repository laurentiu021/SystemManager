// SysManager · BatteryHealthViewModel — battery health tab
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Battery Health tab — shows charge, health %, wear, cycles, runtime, and how the capacity has changed over time.
/// </summary>
public sealed partial class BatteryHealthViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    /// <summary>Shown on the capacity card while the first read of the history runs.</summary>
    internal const string HistoryReading = "Reading the history Windows keeps for this battery…";

    /// <summary>Shown on the capacity card when Windows keeps no capacity history for the battery.</summary>
    internal const string NoHistoryKept = "Windows keeps no capacity history for this battery, so there is no trend to show.";

    /// <summary>Shown on the capacity card when the battery report could not be produced or read.</summary>
    internal const string HistoryUnreadable = "The capacity history could not be read. Press Refresh to try again.";

    private readonly Func<Task<BatteryInfo?>> _read;
    private readonly Func<CancellationToken, Task<BatteryReportRead>> _readHistory;

    // Cancelled by Dispose, so a battery report still being written when the window closes is abandoned.
    private readonly CancellationTokenSource _cts = new();

    // Whether a read of the history has worked this session. A failed read after one keeps what it showed: the
    // history only grows, so what is on the card is still true.
    private bool _historyShown;

    [ObservableProperty] private BatteryInfo _battery = new();
    [ObservableProperty] private string _summary = "Click Refresh to read battery data.";

    // Distinguishes "Windows answered that there is no battery" from "the read failed". The tab said "No battery
    // detected — this device runs on AC power only." either way, because the service reported a failed read as
    // no battery (#2503).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoBatteryText))]
    private bool _readFailed;

    /// <summary>Whether the battery is wearing at the usual rate; null until Windows has kept a month of history.</summary>
    [ObservableProperty] private BatteryWearVerdict? _wearVerdict;

    /// <summary>What the card says while the history is too short for a verdict; empty otherwise.</summary>
    [ObservableProperty] private string _historyTooShort = "";

    /// <summary>One line for a history that is being read, is not kept, or could not be read; empty otherwise.</summary>
    [ObservableProperty] private string _historyNote = "";

    /// <summary>The capacity history as a line, drawn alongside <see cref="WearVerdict"/> (#1513).</summary>
    public BatteryCapacityChart CapacityChart { get; } = new();

    /// <summary>The line shown in place of the battery cards when there is no reading to show.</summary>
    public string NoBatteryText => ReadFailed
        ? "The battery could not be read, so SysManager cannot tell whether this device has one."
        : "No battery detected on this device.";

    public BatteryHealthViewModel(BatteryService service, IBatteryReportService report)
        : this(() => service.GetBatteryInfoAsync(), report.ReadHistoryAsync) { }

    /// <summary>
    /// Test seam: the battery read and the history read, so either can be supplied without WMI or <c>powercfg</c>.
    /// </summary>
    internal BatteryHealthViewModel(Func<Task<BatteryInfo?>> read, Func<CancellationToken, Task<BatteryReportRead>> readHistory)
    {
        _read = read;
        _readHistory = readHistory;
        PropertyChanged += OnVmPropertyChanged;
        InitializeAsync(InitAsync);
    }

    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy)) RefreshCommand.NotifyCanExecuteChanged();
    }

    private async Task InitAsync()
    {
        try { await RefreshAsync(); }
        catch (ManagementException ex) { Log.Warning("Battery auto-scan failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Warning("Battery auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Reading battery info…";

        try
        {
            await ReadBatteryAsync();

            // After the battery rather than alongside it: with no battery the card is not shown, and the report
            // would be a process and a temporary file for nothing.
            if (Battery.HasBattery) await RefreshHistoryAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    private async Task ReadBatteryAsync()
    {
        try
        {
            var battery = await _read();
            if (battery is null)
            {
                ShowFailedRead();
                return;
            }

            ReadFailed = false;
            Battery = battery;

            Summary = Battery.HasBattery
                ? Battery.HealthPercent >= 0
                    ? $"{Battery.Name} · {Battery.ChargePercent}% · Health {Battery.HealthPercent}% · {Battery.Status}"
                    : $"{Battery.Name} · {Battery.ChargePercent}% · Health: requires elevation · {Battery.Status}"
                : "No battery detected — this device runs on AC power only.";

            StatusMessage = Battery.HasBattery
                ? "Battery data loaded."
                : "No battery found.";
            Log.Information("Battery scan completed: {HasBattery}", Battery.HasBattery);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Battery read failed: {Error}", ex.Message);
            ShowFailedRead();
        }
    }

    /// <summary>
    /// A failed read changes nothing on screen: an earlier reading stays, and the summary and status line say the
    /// read failed rather than that there is no battery (#2503).
    /// </summary>
    private void ShowFailedRead()
    {
        ReadFailed = true;
        if (Battery.HasBattery)
        {
            StatusMessage = "The battery could not be read this time, so the figures below are from the last reading.";
            return;
        }

        Summary = "Could not read the battery. Press Refresh to try again.";
        StatusMessage = "The battery could not be read.";
    }

    private async Task RefreshHistoryAsync()
    {
        if (IsDisposed) return;
        if (!_historyShown) HistoryNote = HistoryReading;

        BatteryReportRead read;
        try
        {
            read = await _readHistory(_cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The tab was disposed while the report was being written.
            return;
        }

        ShowHistory(read);
    }

    /// <summary>
    /// Puts a read of the history on the card: the verdict and the line once there is a month of it, the "not enough
    /// history yet" message before that, or one line when Windows keeps none or the report could not be read.
    /// </summary>
    private void ShowHistory(BatteryReportRead read)
    {
        if (read.Outcome == BatteryReportOutcome.Failed)
        {
            if (_historyShown)
            {
                Log.Information("Battery report could not be read again; the capacity card keeps the last reading");
                return;
            }

            ClearHistory();
            HistoryNote = HistoryUnreadable;
            return;
        }

        _historyShown = true;
        var history = read.History;
        if (read.Outcome == BatteryReportOutcome.NoHistory || history.Count == 0)
        {
            ClearHistory();
            HistoryNote = NoHistoryKept;
            return;
        }

        var verdict = BatteryWearAnalyzer.Assess(history);
        if (verdict is null)
        {
            ClearHistory();
            HistoryTooShort = BatteryWearAnalyzer.DescribeTooShort(history);
            return;
        }

        CapacityChart.Update(history);
        WearVerdict = verdict;
        HistoryTooShort = "";
        HistoryNote = "";
    }

    private void ClearHistory()
    {
        CapacityChart.Update([]);
        WearVerdict = null;
        HistoryTooShort = "";
        HistoryNote = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            CapacityChart.Dispose();
        }
        base.Dispose(disposing);
    }
}
