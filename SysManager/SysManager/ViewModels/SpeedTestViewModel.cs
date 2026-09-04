// SysManager · SpeedTestViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>HTTP + Ookla speed tests with persistent history.</summary>
public sealed partial class SpeedTestViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        (IsOoklaTesting || IsHttpTesting) ? CancelSpeedCommand : null;

    public NetworkSharedState Shared { get; }
    private readonly SpeedTestHistoryService _history;
    private readonly EtaCalculator _eta = new();
    private CancellationTokenSource? _speedCts;

    [ObservableProperty] private SpeedTestResult? _httpResult;
    [ObservableProperty] private SpeedTestResult? _ooklaResult;

    // The plain-English reading of each engine's latest result. The tab used to show only the raw Mbps,
    // which answers "what is my speed" and not the question people actually open it with — "is that
    // good". One per engine, because each has its own result card and its own history to compare against.
    [ObservableProperty] private SpeedVerdict? _httpVerdict;
    [ObservableProperty] private SpeedVerdict? _ooklaVerdict;
    [ObservableProperty] private string _selectedOoklaServer = "Auto (nearest)";

    public string[] OoklaServerOptions { get; } = {
        "Auto (nearest)",
        "Bucharest, RO (ID: 2616)",
        "London, UK (ID: 12884)",
        "Frankfurt, DE (ID: 31120)",
        "Amsterdam, NL (ID: 28922)",
        "Paris, FR (ID: 5765)",
        "New York, US (ID: 10390)",
    };
    [ObservableProperty] private int _speedProgress;
    [ObservableProperty] private string _httpStatus = "";
    [ObservableProperty] private string _ooklaStatus = "";
    [ObservableProperty] private bool _isSpeedTesting;
    [ObservableProperty] private bool _isHttpTesting;
    [ObservableProperty] private bool _isOoklaTesting;
    [ObservableProperty] private string _estimatedTime = "";

    /// <summary>Persisted history of HTTP speed test results (newest first).</summary>
    public BulkObservableCollection<SpeedTestResult> HttpHistory { get; } = new();

    /// <summary>Persisted history of Ookla speed test results (newest first).</summary>
    public BulkObservableCollection<SpeedTestResult> OoklaHistory { get; } = new();

    public SpeedTestViewModel(NetworkSharedState shared, SpeedTestHistoryService history)
    {
        Shared = shared;
        _history = history;
        InitializeAsync(LoadHistoryAsync);
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var all = await _history.LoadAsync();
            HttpHistory.ReplaceWith(all.Where(r => string.Equals(r.Engine, "HTTP", StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending(r => r.CompletedAt));
            OoklaHistory.ReplaceWith(all.Where(r => string.Equals(r.Engine, "Ookla", StringComparison.OrdinalIgnoreCase))
                                        .OrderByDescending(r => r.CompletedAt));
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "Failed to load speed test history");
        }
    }

    [RelayCommand]
    private async Task RunHttpSpeedAsync()
    {
        if (IsSpeedTesting) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "HTTP Speed Test");
        if (opLock is null)
        {
            HttpStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
        IsSpeedTesting = true;
        IsHttpTesting = true;
        SpeedProgress = 0;
        EstimatedTime = "";
        _eta.Reset();
        HttpStatus = "Starting HTTP speed test…";
        _speedCts?.Dispose();
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t =>
        { SpeedProgress = t.p; HttpStatus = t.m; EstimatedTime = _eta.Update(t.p); });
        try
        {
            HttpResult = await Shared.Speed.RunHttpAsync(progress, _speedCts.Token);
            HttpStatus = "HTTP done";
            Log.Information("HTTP speed test: {Down:F1} Mbps down, {Up:F1} Mbps up",
                HttpResult.DownloadMbps, HttpResult.UploadMbps);

            // Read the verdict BEFORE the history insert below: HttpHistory[0] is the previous run only
            // until this one is prepended. Same-engine history, so the comparison never puts an HTTP
            // result next to an Ookla one — the two engines measure differently.
            HttpVerdict = SpeedVerdictAnalyzer.Analyze(HttpResult, HttpHistory.FirstOrDefault());

            // Persist result to history. The in-memory list is still updated when the write fails, so the
            // reading stays on screen for this session — but the status says so, because a result the user
            // believes was recorded and then cannot find next launch is worse than one they were warned
            // about. SaveAsync does not throw; it reports.
            if (!await _history.SaveAsync(HttpResult))
                HttpStatus = "HTTP done — result could not be saved to history";
            HttpHistory.Insert(0, HttpResult);
            if (HttpHistory.Count > SpeedTestHistoryService.MaxPerEngine)
                HttpHistory.RemoveAt(HttpHistory.Count - 1);
        }
        catch (OperationCanceledException) { HttpStatus = "Cancelled"; }
        catch (System.Net.Http.HttpRequestException ex)
        { HttpStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { HttpStatus = "Error: " + ex.Message; }
        // EstimatedTime must be cleared here, not just on the next run. Its TextBlock's Visibility binds
        // to the string itself, so a leftover value stays on screen indefinitely — and because both cards
        // share this one property, a finished HTTP run left the word "done" sitting under the Ookla card
        // too. Matches AppUpdates/BulkInstaller/Uninstaller, which all clear their ETA text in `finally`.
        finally { IsSpeedTesting = false; IsHttpTesting = false; EstimatedTime = string.Empty; }
    }

    [RelayCommand]
    private async Task RunOoklaSpeedAsync()
    {
        if (IsSpeedTesting) return;
        using var opLock = OperationLockService.Instance.TryAcquire(OperationCategory.Network, "Ookla Speed Test");
        if (opLock is null)
        {
            OoklaStatus = $"Cannot start — {OperationLockService.Instance.GetActiveOperationName(OperationCategory.Network)} is already running.";
            return;
        }
        IsSpeedTesting = true;
        IsOoklaTesting = true;
        SpeedProgress = 0;
        EstimatedTime = "";
        _eta.Reset();
        OoklaStatus = "Starting Ookla speed test…";
        _speedCts?.Dispose();
        _speedCts = new CancellationTokenSource();
        var progress = new Progress<(int p, string m)>(t =>
        { SpeedProgress = t.p; OoklaStatus = t.m; EstimatedTime = _eta.Update(t.p); });
        try
        {
            OoklaResult = await Shared.Speed.RunOoklaAsync(progress, _speedCts.Token, ParseServerId(SelectedOoklaServer));
            OoklaStatus = "Ookla done";
            Log.Information("Ookla speed test: {Down:F1} Mbps down, {Up:F1} Mbps up",
                OoklaResult.DownloadMbps, OoklaResult.UploadMbps);

            // Before the insert, for the same reason as the HTTP path above.
            OoklaVerdict = SpeedVerdictAnalyzer.Analyze(OoklaResult, OoklaHistory.FirstOrDefault());

            // Persist result to history, reporting a failed write — same contract as the HTTP path above.
            if (!await _history.SaveAsync(OoklaResult))
                OoklaStatus = "Ookla done — result could not be saved to history";
            OoklaHistory.Insert(0, OoklaResult);
            if (OoklaHistory.Count > SpeedTestHistoryService.MaxPerEngine)
                OoklaHistory.RemoveAt(OoklaHistory.Count - 1);
        }
        catch (OperationCanceledException) { OoklaStatus = "Cancelled"; }
        catch (System.ComponentModel.Win32Exception ex)
        { OoklaStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { OoklaStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsOoklaTesting = false; EstimatedTime = string.Empty; }
    }

    [RelayCommand]
    private Task ClearHttpHistoryAsync() => ClearHistoryAsync("HTTP", HttpHistory);

    [RelayCommand]
    private Task ClearOoklaHistoryAsync() => ClearHistoryAsync("Ookla", OoklaHistory);

    /// <summary>
    /// Confirms, then drops one engine's saved results. ClearAsync rewrites the history file
    /// immediately, so the past measurements cannot be recovered afterwards.
    /// </summary>
    /// <remarks>
    /// The grid is emptied only if the disk write actually succeeded. Emptying it regardless is a worse
    /// version of the save bug fixed in 1.65.10: the user has just confirmed a dialog saying the data is
    /// gone for good, so a silent failure showed an empty list over a file that still held every reading —
    /// and they came back on the next launch, with nothing to say which state was real.
    /// </remarks>
    private async Task ClearHistoryAsync(string engine, BulkObservableCollection<SpeedTestResult> history)
    {
        int count = history.Count;
        if (count > 0 && !DialogService.Instance.Confirm(
                $"Delete all {count} saved {engine} result{(count == 1 ? "" : "s")}?\n\n" +
                "The saved history is removed from disk and cannot be recovered.",
                $"Clear {engine} History — Confirm"))
            return;

        if (!await _history.ClearAsync(engine))
        {
            // Reported under the engine's OWN card. The two engines have separate status lines in separate
            // cards (SpeedTestView.xaml binds OoklaStatus and HttpStatus independently), so writing to the
            // wrong one would put an Ookla failure under the HTTP heading.
            var message = $"{engine} history could not be cleared — the saved file could not be written.";
            if (string.Equals(engine, "Ookla", StringComparison.OrdinalIgnoreCase)) OoklaStatus = message;
            else HttpStatus = message;

            // Leave the rows on screen: they still exist on disk, so showing them is the honest state.
            return;
        }

        history.Clear();
    }

    [RelayCommand]
    private void CancelSpeed() => _speedCts?.Cancel();

    private static int? ParseServerId(string option)
    {
        if (option.StartsWith("Auto")) return null;
        var match = ServerIdRegex().Match(option);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    // Speedtest server options are formatted "Name (ID: 1234)".
    [GeneratedRegex(@"ID:\s*(\d+)")]
    private static partial Regex ServerIdRegex();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _speedCts?.Cancel();
            _speedCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsSpeedTestingChanged(bool value) => IsBusy = value;
}
