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
    private readonly ISpeedTestService _engine;
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
        : this(shared, history, shared.Speed)
    {
    }

    /// <summary>
    /// Runs both tests on <paramref name="engine"/> instead of the shared service, so a test can hand back a
    /// result without a network, including one whose upload or ping was not measured.
    /// </summary>
    internal SpeedTestViewModel(NetworkSharedState shared, SpeedTestHistoryService history, ISpeedTestService engine)
    {
        Shared = shared;
        _history = history;
        _engine = engine;
        _history.Saved += OnHistorySaved;
        InitializeAsync(LoadHistoryAsync);
    }

    // A result saved anywhere, the Dashboard's quick test included, belongs in the list on screen. The event comes
    // from whichever thread saved it, so the list is only touched on the UI thread.
    private void OnHistorySaved(SpeedTestResult result) => UiThread.Post(() => AddToHistory(result));

    /// <summary>
    /// Puts a result at the top of its engine's history, once, trimmed to what the file keeps.
    /// </summary>
    /// <remarks>
    /// Called for this tab's own runs and for the <see cref="SpeedTestHistoryService.Saved"/> event alike, so a run
    /// made here arrives twice, and nothing here relies on which comes first. The second is a no-op: a record
    /// compares by value, and two runs never share a completion time to the tick. A run whose save failed arrives
    /// only once, with no event, and stays on screen for the session, as it always did.
    /// </remarks>
    internal void AddToHistory(SpeedTestResult result)
    {
        var history = string.Equals(result.Engine, "Ookla", StringComparison.OrdinalIgnoreCase) ? OoklaHistory : HttpHistory;
        if (history.Contains(result)) return;

        history.Insert(0, result);
        if (history.Count > SpeedTestHistoryService.MaxPerEngine)
            history.RemoveAt(history.Count - 1);
    }

    /// <summary>
    /// Fills both lists from the history file, newest first, keeping any result already on screen.
    /// </summary>
    /// <remarks>
    /// The <see cref="SpeedTestHistoryService.Saved"/> subscription starts before this load, so that nothing saved
    /// while the file is read is missed. The price is that a result can reach the screen through the event and
    /// be absent from the file as it was read a moment earlier, and replacing the lists outright would take it
    /// away again until the next launch. A result in both is kept once, because a record compares by value, and
    /// each list is capped at what the file keeps. Internal so a test can load over a list the event has
    /// already added to.
    /// </remarks>
    internal async Task LoadHistoryAsync()
    {
        try
        {
            if (await _history.LoadAsync() is not { } saved)
            {
                // Said under both cards, because the one file holds both engines' results. Until it can be read,
                // a new result is not saved either: the save refuses to write over it (#2521).
                HttpStatus = OoklaStatus = HistoryUnreadable;
                return;
            }

            var all = saved.Union(HttpHistory).Union(OoklaHistory).ToList();
            HttpHistory.ReplaceWith(NewestFirst(all, "HTTP"));
            OoklaHistory.ReplaceWith(NewestFirst(all, "Ookla"));
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "Failed to load speed test history");
        }
    }

    /// <summary>The status line under each card when the saved results could not be read.</summary>
    internal const string HistoryUnreadable =
        "Your saved results could not be read, so none are shown. New results are not saved until they can be.";

    private static IEnumerable<SpeedTestResult> NewestFirst(IEnumerable<SpeedTestResult> all, string engine) =>
        all.Where(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase))
           .OrderByDescending(r => r.CompletedAt)
           .Take(SpeedTestHistoryService.MaxPerEngine);

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
        var progress = new SettlingProgress<(int p, string m)>(t =>
        { SpeedProgress = t.p; HttpStatus = t.m; EstimatedTime = _eta.Update(t.p); });
        try
        {
            HttpResult = await progress.SettleAfterAsync(
                reporter => _engine.RunHttpAsync(reporter, _speedCts.Token));
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
            HttpStatus = DescribeFinished("HTTP", HttpResult, saved: await _history.SaveAsync(HttpResult));
            AddToHistory(HttpResult);
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
        var progress = new SettlingProgress<(int p, string m)>(t =>
        { SpeedProgress = t.p; OoklaStatus = t.m; EstimatedTime = _eta.Update(t.p); });
        try
        {
            OoklaResult = await progress.SettleAfterAsync(
                reporter => _engine.RunOoklaAsync(reporter, _speedCts.Token, ParseServerId(SelectedOoklaServer)));
            OoklaStatus = "Ookla done";
            Log.Information("Ookla speed test: {Down:F1} Mbps down, {Up:F1} Mbps up",
                OoklaResult.DownloadMbps, OoklaResult.UploadMbps);

            // Before the insert, for the same reason as the HTTP path above.
            OoklaVerdict = SpeedVerdictAnalyzer.Analyze(OoklaResult, OoklaHistory.FirstOrDefault());

            // Persist result to history, reporting a failed write — same contract as the HTTP path above.
            OoklaStatus = DescribeFinished("Ookla", OoklaResult, saved: await _history.SaveAsync(OoklaResult));
            AddToHistory(OoklaResult);
        }
        catch (OperationCanceledException) { OoklaStatus = "Cancelled"; }
        catch (System.ComponentModel.Win32Exception ex)
        { OoklaStatus = "Error: " + ex.Message; }
        catch (InvalidOperationException ex)
        { OoklaStatus = "Error: " + ex.Message; }
        finally { IsSpeedTesting = false; IsOoklaTesting = false; EstimatedTime = string.Empty; }
    }

    /// <summary>
    /// The status line under a finished run's card: what was not measured, and whether it was saved.
    /// </summary>
    /// <remarks>
    /// The card shows "—" for an upload or ping that was not measured, and this line says why, so the dash does
    /// not read as a glitch. A ping with no answer is the common case, because some networks block ping. Pure,
    /// so it is testable without WPF.
    /// </remarks>
    internal static string DescribeFinished(string engine, SpeedTestResult result, bool saved)
    {
        List<string> notes = [];
        if (result.UploadMbps is null) notes.Add("upload could not be measured");
        if (result.PingMs is null) notes.Add("no reply to ping (some networks block it)");
        if (!saved) notes.Add("result could not be saved to history");
        return notes.Count == 0 ? $"{engine} done" : $"{engine} done — {string.Join("; ", notes)}";
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
            var message = $"{engine} history could not be cleared — the saved file could not be read or written.";
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
            _history.Saved -= OnHistorySaved;
            _speedCts?.Cancel();
            _speedCts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // Forward any running state to IsBusy so the sidebar progress indicator works
    partial void OnIsSpeedTestingChanged(bool value) => IsBusy = value;
}
