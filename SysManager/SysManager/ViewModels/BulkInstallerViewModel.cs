// SysManager · BulkInstallerViewModel — bulk install curated apps via winget
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
/// Bulk Installer tab — select curated apps and install them via winget.
/// </summary>
public sealed partial class BulkInstallerViewModel : ViewModelBase
{
    private readonly BulkInstallerService _service;
    private CancellationTokenSource? _cts;

    public BulkObservableCollection<InstallableApp> Apps { get; } = new();
    public ObservableCollection<InstallableApp> FilteredApps { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedCategory = "All";

    public List<string> Categories { get; } =
    [
        "All",
        "Browsers",
        "Communication",
        "Media",
        "Development",
        "Utilities",
        "Gaming",
        "Security"
    ];

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    public BulkInstallerViewModel(BulkInstallerService service)
    {
        _service = service;
        Apps.ReplaceWith(BuildCuratedApps());
        ApplyFilter();
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No apps selected.";
            return;
        }

        IsBusy = true;
        IsProgressIndeterminate = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var installed = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var app = selected[i];
                app.Status = "Installing...";
                Progress = (int)((double)i / selected.Count * 100);
                StatusMessage = $"Installing {app.Name} ({i + 1}/{selected.Count})…";

                try
                {
                    var exitCode = await _service.InstallAsync(app.WingetId, _cts.Token);
                    if (exitCode == 0)
                    {
                        app.Status = "Installed";
                        installed++;
                    }
                    else
                    {
                        app.Status = $"Failed (exit {exitCode})";
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    app.Status = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    app.Status = $"Error: {ex.Message}";
                    failed++;
                    Log.Warning(ex, "Failed to install {WingetId}", app.WingetId);
                }
            }

            Progress = 100;
            StatusMessage = $"Done. Installed: {installed}, Failed: {failed}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Installation cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var app in FilteredApps)
            app.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var app in Apps)
            app.IsSelected = false;
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        foreach (var app in Apps.Where(a => a.Category == category))
            app.IsSelected = true;
    }

    private void ApplyFilter()
    {
        var filtered = Apps.AsEnumerable();

        if (SelectedCategory != "All")
            filtered = filtered.Where(a => a.Category == SelectedCategory);

        if (!string.IsNullOrWhiteSpace(FilterText))
            filtered = filtered.Where(a =>
                a.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

        FilteredApps.Clear();
        foreach (var app in filtered)
            FilteredApps.Add(app);
    }

    private static List<InstallableApp> BuildCuratedApps() =>
    [
        // Browsers
        new() { Name = "Google Chrome",  WingetId = "Google.Chrome",   Category = "Browsers" },
        new() { Name = "Firefox",        WingetId = "Mozilla.Firefox", Category = "Browsers" },
        new() { Name = "Brave",          WingetId = "Brave.Brave",     Category = "Browsers" },
        new() { Name = "Vivaldi",        WingetId = "Vivaldi.Vivaldi", Category = "Browsers" },

        // Communication
        new() { Name = "Discord",  WingetId = "Discord.Discord",              Category = "Communication" },
        new() { Name = "Slack",    WingetId = "SlackTechnologies.Slack",       Category = "Communication" },
        new() { Name = "Zoom",     WingetId = "Zoom.Zoom",                    Category = "Communication" },
        new() { Name = "Telegram", WingetId = "Telegram.TelegramDesktop",     Category = "Communication" },

        // Media
        new() { Name = "VLC",       WingetId = "VideoLAN.VLC",              Category = "Media" },
        new() { Name = "Spotify",   WingetId = "Spotify.Spotify",           Category = "Media" },
        new() { Name = "foobar2000", WingetId = "PeterPawlowski.foobar2000", Category = "Media" },

        // Development
        new() { Name = "VS Code",  WingetId = "Microsoft.VisualStudioCode", Category = "Development" },
        new() { Name = "Git",      WingetId = "Git.Git",                    Category = "Development" },
        new() { Name = "Node.js",  WingetId = "OpenJS.NodeJS.LTS",          Category = "Development" },
        new() { Name = "Python",   WingetId = "Python.Python.3.12",         Category = "Development" },

        // Utilities
        new() { Name = "7-Zip",      WingetId = "7zip.7zip",              Category = "Utilities" },
        new() { Name = "Notepad++",  WingetId = "Notepad++.Notepad++",    Category = "Utilities" },
        new() { Name = "Everything", WingetId = "voidtools.Everything",   Category = "Utilities" },
        new() { Name = "PowerToys",  WingetId = "Microsoft.PowerToys",    Category = "Utilities" },

        // Gaming
        new() { Name = "Steam",        WingetId = "Valve.Steam",                    Category = "Gaming" },
        new() { Name = "Epic Games",   WingetId = "EpicGames.EpicGamesLauncher",    Category = "Gaming" },
        new() { Name = "GOG Galaxy",   WingetId = "GOG.Galaxy",                     Category = "Gaming" },

        // Security
        new() { Name = "Bitwarden",    WingetId = "Bitwarden.Bitwarden",       Category = "Security" },
        new() { Name = "Malwarebytes", WingetId = "Malwarebytes.Malwarebytes", Category = "Security" },
    ];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
