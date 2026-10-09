// SysManager · DriversViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public sealed partial class DriversViewModel : ViewModelBase
{
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => ListDriversCommand;

    /// <inheritdoc/>
    protected internal override IRelayCommand? EscapeCancel =>
        IsBusy ? CancelCommand : null;

    private readonly IPowerShellRunner _runner;
    private CancellationTokenSource? _cts;
    private readonly List<DriverEntry> _allDrivers = new();

    public BulkObservableCollection<DriverEntry> Drivers { get; } = new();

    [ObservableProperty] private int _driverCount;
    [ObservableProperty] private string _summary = "Click List drivers to scan installed drivers.";
    [ObservableProperty] private bool _hideSystemDrivers;

    /// <summary>
    /// True when a scan found drivers but the filter hid every one of them. Distinguishes "nothing
    /// scanned yet" from "filtered to nothing" — with one shared empty state, checking the filter on
    /// a machine whose drivers are all Microsoft-supplied told the user to click a button they had
    /// already clicked. Same split as the Logs tab.
    /// </summary>
    [ObservableProperty] private bool _hasNoResults;

    /// <summary>True before the first scan — drives the "click List drivers" prompt.</summary>
    [ObservableProperty] private bool _hasNotScanned = true;

    // Distinguishes "Windows answered" from "the scan failed". A failed scan reported "0 drivers found", "Done"
    // and a completion toast, because the exit code was discarded and an empty answer parsed to nothing (#2503).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyMessage))]
    private bool _listFailed;

    public string EmptyTitle => ListFailed ? "Drivers could not be read" : "No drivers listed";

    public string EmptyMessage => ListFailed
        ? "Windows did not answer when SysManager asked for the installed drivers. Press List drivers to try again."
        : "Click 'List drivers' to enumerate installed system drivers.";

    public DriversViewModel(IPowerShellRunner runner)
    {
        _runner = runner;
        // Re-evaluate ListDrivers' CanExecute when IsBusy flips. Without this gate a second
        // click while a scan runs double-subscribes the LineReceived capture (concatenated
        // JSON → JsonException) and recreates the shared _cts the first run is still awaiting.
        // Mirrors AppUpdates/Uninstaller/WindowsUpdate.
        PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>Gate for the long-running scan command; Cancel stays enabled.</summary>
    private bool NotBusy => !IsBusy;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsBusy)) return;
        ListDriversCommand.NotifyCanExecuteChanged();
    }

    partial void OnHideSystemDriversChanged(bool value) => ApplyFilter();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ListDriversAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning installed drivers…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var json = new System.Text.StringBuilder();
            void Capture(PowerShellLine l)
            {
                if (l.Kind == OutputKind.Output)
                    json.AppendLine(l.Text);
            }

            int exitCode;
            _runner.LineReceived += Capture;
            try
            {
                exitCode = await _runner.RunScriptViaPwshAsync(@"
                    Get-CimInstance Win32_PnPSignedDriver |
                      Where-Object { $_.DeviceName -and $_.DriverVersion } |
                      Select-Object DeviceName, DriverVersion, Manufacturer, DriverDate, IsSigned |
                      ConvertTo-Json -Compress
                ", cancellationToken: _cts.Token);
            }
            finally { _runner.LineReceived -= Capture; }

            var scan = ReadScan(exitCode, json.ToString());
            ListFailed = scan.Drivers is null;
            if (scan.Drivers is null)
            {
                // A failed scan changes nothing on screen: what was listed stays listed, and the empty state and the
                // status line say the scan failed rather than that there are no drivers (#2503).
                StatusMessage = _allDrivers.Count == 0
                    ? "Could not read the installed drivers. Press List drivers to try again."
                    : "Could not read the installed drivers, so the list below is from the last scan.";
                return;
            }

            _allDrivers.Clear();
            _allDrivers.AddRange(scan.Drivers);
            ApplyFilter();
            HasNotScanned = false;
            Summary = $"{_allDrivers.Count} drivers found" +
                      (HideSystemDrivers ? $" ({DriverCount} shown, built-in Windows drivers hidden)." : ".");
            StatusMessage = scan.Complete
                ? "Done"
                : "Windows reported an error while listing the drivers, so some may be missing.";
            ToastService.Instance.Show("Driver scan complete", $"{_allDrivers.Count} drivers found");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _allDrivers.Count == 0
                ? "Cancelled."
                : "Cancelled, so the list below is from the last scan.";
        }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

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

    /// <summary>What one scan returned: the drivers, or null when the scan failed, and whether it ran clean.</summary>
    internal readonly record struct DriverScan(IReadOnlyList<DriverEntry>? Drivers, bool Complete);

    /// <summary>Reads one scan's exit code and output. Pure, so every outcome is testable without PowerShell.</summary>
    /// <remarks>
    /// The exit code used to be discarded. A query that failed outright printed nothing, which parsed to no
    /// drivers, so the tab reported "0 drivers found", "Done" and a completion toast (#2503).
    /// <list type="bullet">
    /// <item><description>Output that is not JSON is a failed scan. <see cref="JsonDocument"/> parses all or
    /// nothing, so none of it can be shown, and saying "some drivers may not be shown" was never true.</description></item>
    /// <item><description>A non-zero exit with nothing listed is a failed scan.</description></item>
    /// <item><description>A non-zero exit with drivers listed keeps them, marked incomplete. Windows PowerShell
    /// exits 1 when any command in the pipeline wrote an error, and still prints what the pipeline
    /// produced.</description></item>
    /// <item><description>A clean exit with no output is Windows answering that there are none.</description></item>
    /// </list>
    /// </remarks>
    internal static DriverScan ReadScan(int exitCode, string output)
    {
        var drivers = ParseDrivers(output);
        if (drivers is null || (exitCode != 0 && drivers.Count == 0)) return new DriverScan(null, false);
        return new DriverScan(drivers, exitCode == 0);
    }

    /// <summary>The drivers in <paramref name="raw"/>: an empty list for no output, null when it is not JSON.</summary>
    internal static List<DriverEntry>? ParseDrivers(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // PS returns an array if multiple, or a single object if only one.
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : [root];

            return [.. items.Select(el => new DriverEntry
            {
                DeviceName = el.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "" : "",
                Manufacturer = el.TryGetProperty("Manufacturer", out var mf) ? mf.GetString() ?? "" : "",
                DriverVersion = el.TryGetProperty("DriverVersion", out var dv) ? dv.GetString() ?? "" : "",
                DriverDate = ParseCimDate(el.TryGetProperty("DriverDate", out var dd) ? dd : default),
                IsSigned = ParseCimBool(el.TryGetProperty("IsSigned", out var sg) ? sg : default),
            })];
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to parse driver JSON: {Error}", ex.Message);
            return null;
        }
    }

    private void ApplyFilter()
    {
        var filtered = HideSystemDrivers
            ? _allDrivers.Where(d => !IsSystemDriver(d))
            : _allDrivers;

        Drivers.ReplaceWith(filtered);

        DriverCount = Drivers.Count;
        HasNoResults = _allDrivers.Count > 0 && DriverCount == 0;
        if (_allDrivers.Count > 0)
            Summary = HideSystemDrivers
                ? $"{_allDrivers.Count} drivers found ({DriverCount} shown, built-in Windows drivers hidden)."
                : $"{_allDrivers.Count} drivers found.";
    }

    /// <summary>
    /// True for a driver Windows itself supplies, as opposed to one a hardware maker installed.
    /// Matched on the publisher name because <c>Win32_PnPSignedDriver</c> exposes no "inbox" flag.
    /// </summary>
    internal static bool IsSystemDriver(DriverEntry d)
        => d.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
           d.Manufacturer.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a CIM boolean, keeping "absent" distinct from "false".
    /// </summary>
    /// <remarks>
    /// The distinction is the point (#1581). If a missing <c>IsSigned</c> collapsed to <c>false</c>, the
    /// column would call a driver unsigned because Windows declined to say — and on this tab that reads as
    /// an accusation the user may act on. <c>ConvertTo-Json</c> can also emit the value as the strings
    /// "True"/"False" depending on how the property is surfaced, so both shapes are accepted; anything else
    /// is unknown rather than guessed at.
    /// </remarks>
    internal static bool? ParseCimBool(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.TryParse(el.GetString(), out var parsed) ? parsed : null,
        _ => null,
    };

    /// <summary>
    /// CIM dates come as "/Date(ticks)/" strings in JSON.
    /// </summary>
    private static DateTime? ParseCimDate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;

        var text = el.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // "/Date(1234567890000)/" format
        if (text.StartsWith("/Date(", StringComparison.Ordinal) && text.EndsWith(")/", StringComparison.Ordinal))
        {
            var ticksStr = text[6..^2];
            // Handle timezone offset like /Date(1234567890000+0000)/
            var plusIdx = ticksStr.IndexOf('+');
            var minusIdx = ticksStr.IndexOf('-', 1);
            var endIdx = plusIdx >= 0 ? plusIdx : (minusIdx >= 0 ? minusIdx : ticksStr.Length);
            if (long.TryParse(ticksStr[..endIdx], out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        }

        // Fallback: try standard parse
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        return null;
    }
}
