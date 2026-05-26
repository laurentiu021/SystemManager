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
    private readonly StartupService _service;
    private readonly List<StartupEntry> _allEntries = new();

    public BulkObservableCollection<StartupEntry> Entries { get; } = new();

    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _scanSummary = "Click Scan to discover startup items.";
    [ObservableProperty] private bool _hideWindowsEntries;

    public StartupViewModel(StartupService service)
    {
        _service = service;
        InitializeAsync(InitAsync);
    }

    partial void OnHideWindowsEntriesChanged(bool value) => ApplyFilter();

    private async Task InitAsync()
    {
        try { await ScanAsync(); }
        catch (InvalidOperationException ex) { Log.Warning("Startup auto-scan failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Startup auto-scan failed: {Error}", ex.Message); }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning startup items…";
        try
        {
            var items = await _service.ScanAsync().ConfigureAwait(false);
            var sorted = items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in sorted)
                item.Icon = IconExtractorService.GetIcon(item.Command);

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(() =>
                {
                    _allEntries.Clear();
                    _allEntries.AddRange(sorted);
                    ApplyFilter();
                });
            }
            else
            {
                _allEntries.Clear();
                _allEntries.AddRange(sorted);
                ApplyFilter();
            }

            StatusMessage = $"Found {_allEntries.Count} startup items.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ToggleEntry(object? parameter)
    {
        if (parameter is not StartupEntry entry) return;

        // The CheckBox two-way binding has already flipped IsEnabled before
        // this command runs. We use the current (already-flipped) value as
        // the desired new state.
        var desiredState = entry.IsEnabled;
        var success = StartupService.SetEnabled(entry, desiredState);
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

    [RelayCommand]
    private void EnableAll()
    {
        foreach (var entry in Entries.Where(e => !e.IsEnabled))
            StartupService.SetEnabled(entry, true);
        UpdateCounts();
        StatusMessage = "All items enabled.";
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
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                StatusMessage = "File not found — the application may have been moved or uninstalled.";
            }
        }
        catch (InvalidOperationException) { StatusMessage = "Could not open file location."; }
        catch (System.ComponentModel.Win32Exception) { StatusMessage = "Could not open file location."; }
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
