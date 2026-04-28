// SysManager · DriversViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

public partial class DriversViewModel : ViewModelBase
{
    private readonly PowerShellRunner _runner;
    private CancellationTokenSource? _cts;

    public ObservableCollection<DriverEntry> Drivers { get; } = new();

    [ObservableProperty] private int _driverCount;
    [ObservableProperty] private string _summary = "Click List drivers to scan installed drivers.";

    public DriversViewModel(PowerShellRunner runner)
    {
        _runner = runner;
    }

    [RelayCommand]
    private async Task ListDriversAsync()
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusMessage = "Scanning installed drivers…";
        Drivers.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var json = new System.Text.StringBuilder();
            void Capture(PowerShellLine l)
            {
                if (l.Kind == OutputKind.Output)
                    json.AppendLine(l.Text);
            }

            _runner.LineReceived += Capture;
            try
            {
                await _runner.RunScriptViaPwshAsync(@"
                    Get-CimInstance Win32_PnPSignedDriver |
                      Where-Object { $_.DeviceName -and $_.DriverVersion } |
                      Select-Object DeviceName, DriverVersion, Manufacturer, DriverDate |
                      ConvertTo-Json -Compress
                ", cancellationToken: _cts.Token);
            }
            finally { _runner.LineReceived -= Capture; }

            ParseDriverJson(json.ToString());
            DriverCount = Drivers.Count;
            Summary = $"{DriverCount} drivers found.";
            StatusMessage = "Done";
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
        catch (InvalidOperationException ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void ParseDriverJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // PS returns an array if multiple, or a single object if only one.
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : new[] { root }.AsEnumerable();

            foreach (var el in items)
            {
                var entry = new DriverEntry
                {
                    DeviceName = el.TryGetProperty("DeviceName", out var dn) ? dn.GetString() ?? "" : "",
                    Manufacturer = el.TryGetProperty("Manufacturer", out var mf) ? mf.GetString() ?? "" : "",
                    DriverVersion = el.TryGetProperty("DriverVersion", out var dv) ? dv.GetString() ?? "" : "",
                    DriverDate = ParseCimDate(el.TryGetProperty("DriverDate", out var dd) ? dd : default),
                };
                Drivers.Add(entry);
            }
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to parse driver JSON: {Error}", ex.Message);
            StatusMessage = "Parse error — some drivers may not be shown.";
        }
    }

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
