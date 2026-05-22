// SysManager · BulkInstallerViewModel — bulk install curated apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
    public ICollectionView GroupedView { get; }

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
        "Security",
        "Office & Productivity",
        "Creativity",
        "Networking & VPN",
        "Runtimes & Frameworks",
        "Custom"
    ];

    // Custom winget search
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    public BulkObservableCollection<InstallableApp> SearchResults { get; } = new();

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    public BulkInstallerViewModel(BulkInstallerService service)
    {
        _service = service;
        Apps.ReplaceWith(BuildCuratedApps());
        ApplyFilter();

        GroupedView = CollectionViewSource.GetDefaultView(FilteredApps);
        GroupedView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InstallableApp.Category)));
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

    [RelayCommand]
    private async Task SearchWingetAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery) || SearchQuery.Length < 2) return;
        IsSearching = true;
        SearchResults.Clear();
        try
        {
            var results = await Task.Run(() => SearchWingetPackages(SearchQuery));
            SearchResults.ReplaceWith(results);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Winget search failed for query {Query}", SearchQuery);
            StatusMessage = "Search failed. Ensure winget is available.";
        }
        finally { IsSearching = false; }
    }

    [RelayCommand]
    private void AddToInstallList(InstallableApp? app)
    {
        if (app == null || Apps.Any(a => a.WingetId == app.WingetId)) return;
        Apps.Add(app);
        ApplyFilter();
    }

    private List<InstallableApp> SearchWingetPackages(string query)
    {
        var results = new List<InstallableApp>();

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"search \"{query}\" --accept-source-agreements",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return results;

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);

        // Parse the tabular output from winget search.
        // The output has a header line with column names, a separator line of dashes,
        // then data rows. Columns are: Name, Id, Version, [Match], Source
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the separator line (all dashes and spaces)
        var separatorIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd().All(c => c == '-' || c == ' ') && lines[i].Contains("--"))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex < 1 || separatorIndex + 1 >= lines.Length) return results;

        // Determine column positions from the header line
        var header = lines[separatorIndex - 1];
        var idStart = header.IndexOf("Id", StringComparison.Ordinal);
        var versionStart = header.IndexOf("Version", StringComparison.Ordinal);

        if (idStart < 0 || versionStart < 0) return results;

        // Parse data rows
        for (var i = separatorIndex + 1; i < lines.Length && results.Count < 30; i++)
        {
            var line = lines[i];
            if (line.Length < versionStart) continue;

            var name = line[..idStart].TrimEnd();
            var id = line[idStart..versionStart].TrimEnd();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

            results.Add(new InstallableApp
            {
                Name = name,
                WingetId = id,
                Category = "Custom",
                Description = $"winget: {id}",
            });
        }

        return results;
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
        new() { Name = "Google Chrome",  WingetId = "Google.Chrome",   Category = "Browsers",       Description = "Fast, secure web browser by Google" },
        new() { Name = "Firefox",        WingetId = "Mozilla.Firefox", Category = "Browsers",       Description = "Privacy-focused open-source web browser" },
        new() { Name = "Brave",          WingetId = "Brave.Brave",     Category = "Browsers",       Description = "Privacy-first browser with built-in ad blocker" },
        new() { Name = "Vivaldi",        WingetId = "Vivaldi.Vivaldi", Category = "Browsers",       Description = "Highly customizable browser for power users" },

        // Communication
        new() { Name = "Discord",  WingetId = "Discord.Discord",              Category = "Communication", Description = "Voice, video, and text chat for communities" },
        new() { Name = "Slack",    WingetId = "SlackTechnologies.Slack",       Category = "Communication", Description = "Team messaging and collaboration platform" },
        new() { Name = "Zoom",     WingetId = "Zoom.Zoom",                    Category = "Communication", Description = "Video conferencing and online meetings" },
        new() { Name = "Telegram", WingetId = "Telegram.TelegramDesktop",     Category = "Communication", Description = "Fast, secure cloud-based messaging" },

        // Media
        new() { Name = "VLC",        WingetId = "VideoLAN.VLC",              Category = "Media", Description = "Free multimedia player supporting all formats" },
        new() { Name = "Spotify",    WingetId = "Spotify.Spotify",           Category = "Media", Description = "Music streaming with millions of songs" },
        new() { Name = "foobar2000", WingetId = "PeterPawlowski.foobar2000", Category = "Media", Description = "Lightweight advanced audio player" },

        // Development
        new() { Name = "VS Code",  WingetId = "Microsoft.VisualStudioCode", Category = "Development", Description = "Lightweight code editor with extensions" },
        new() { Name = "Git",      WingetId = "Git.Git",                    Category = "Development", Description = "Distributed version control system" },
        new() { Name = "Node.js",  WingetId = "OpenJS.NodeJS.LTS",          Category = "Development", Description = "JavaScript runtime for server-side apps" },
        new() { Name = "Python",   WingetId = "Python.Python.3.12",         Category = "Development", Description = "General-purpose programming language" },

        // Utilities
        new() { Name = "7-Zip",      WingetId = "7zip.7zip",              Category = "Utilities", Description = "Free file archiver with high compression" },
        new() { Name = "Notepad++",  WingetId = "Notepad++.Notepad++",    Category = "Utilities", Description = "Powerful text and source code editor" },
        new() { Name = "Everything", WingetId = "voidtools.Everything",   Category = "Utilities", Description = "Instant file search for Windows" },
        new() { Name = "PowerToys",  WingetId = "Microsoft.PowerToys",    Category = "Utilities", Description = "Microsoft utilities for power users" },

        // Gaming
        new() { Name = "Steam",        WingetId = "Valve.Steam",                    Category = "Gaming", Description = "Gaming platform and digital store" },
        new() { Name = "Epic Games",   WingetId = "EpicGames.EpicGamesLauncher",    Category = "Gaming", Description = "Game store and launcher" },
        new() { Name = "GOG Galaxy",   WingetId = "GOG.Galaxy",                     Category = "Gaming", Description = "DRM-free gaming platform" },

        // Security
        new() { Name = "Bitwarden",    WingetId = "Bitwarden.Bitwarden",       Category = "Security", Description = "Open-source password manager" },
        new() { Name = "Malwarebytes", WingetId = "Malwarebytes.Malwarebytes", Category = "Security", Description = "Anti-malware protection and scanning" },

        // Office & Productivity
        new() { Name = "LibreOffice",         WingetId = "TheDocumentFoundation.LibreOffice",  Category = "Office & Productivity", Description = "Free open-source office suite" },
        new() { Name = "Obsidian",            WingetId = "Obsidian.Obsidian",                  Category = "Office & Productivity", Description = "Knowledge base with Markdown notes" },
        new() { Name = "Notion",              WingetId = "Notion.Notion",                      Category = "Office & Productivity", Description = "All-in-one workspace for notes and docs" },
        new() { Name = "Adobe Acrobat Reader", WingetId = "Adobe.Acrobat.Reader.64-bit",       Category = "Office & Productivity", Description = "View, print, and annotate PDFs" },

        // Creativity
        new() { Name = "OBS Studio", WingetId = "OBSProject.OBSStudio",       Category = "Creativity", Description = "Free streaming and screen recording" },
        new() { Name = "GIMP",       WingetId = "GIMP.GIMP",                  Category = "Creativity", Description = "Free image editor (Photoshop alternative)" },
        new() { Name = "Audacity",   WingetId = "Audacity.Audacity",          Category = "Creativity", Description = "Free audio editor and recorder" },
        new() { Name = "Blender",    WingetId = "BlenderFoundation.Blender",  Category = "Creativity", Description = "Free 3D creation suite" },

        // Networking & VPN
        new() { Name = "qBittorrent", WingetId = "qBittorrent.qBittorrent",       Category = "Networking & VPN", Description = "Free open-source BitTorrent client" },
        new() { Name = "ProtonVPN",   WingetId = "ProtonTechnologies.ProtonVPN",   Category = "Networking & VPN", Description = "Free privacy-focused VPN" },
        new() { Name = "WireGuard",   WingetId = "WireGuard.WireGuard",            Category = "Networking & VPN", Description = "Fast modern VPN protocol" },
        new() { Name = "PuTTY",       WingetId = "SimonTatham.PuTTY",              Category = "Networking & VPN", Description = "SSH and Telnet client" },

        // Runtimes & Frameworks
        new() { Name = ".NET Desktop Runtime 8",      WingetId = "Microsoft.DotNet.DesktopRuntime.8", Category = "Runtimes & Frameworks", Description = "Required by many modern apps" },
        new() { Name = "Visual C++ Redistributable",  WingetId = "Microsoft.VCRedist.2015+.x64",     Category = "Runtimes & Frameworks", Description = "Required by games and apps" },
        new() { Name = "Java Runtime",                WingetId = "Oracle.JavaRuntimeEnvironment",     Category = "Runtimes & Frameworks", Description = "Required by Minecraft and enterprise apps" },
        new() { Name = "DirectX Runtime",             WingetId = "Microsoft.DirectX",                 Category = "Runtimes & Frameworks", Description = "Required by most games" },

        // More Utilities
        new() { Name = "WinRAR",        WingetId = "RARLab.WinRAR",             Category = "Utilities", Description = "Popular file archiver" },
        new() { Name = "ShareX",        WingetId = "ShareX.ShareX",             Category = "Utilities", Description = "Screenshot and screen recording tool" },
        new() { Name = "Greenshot",     WingetId = "Greenshot.Greenshot",       Category = "Utilities", Description = "Lightweight screenshot tool" },
        new() { Name = "TreeSize Free", WingetId = "JAMSoftware.TreeSize.Free", Category = "Utilities", Description = "Visualize disk space usage" },

        // More Communication
        new() { Name = "WhatsApp",        WingetId = "WhatsApp.WhatsApp",   Category = "Communication", Description = "Messaging app for desktop" },
        new() { Name = "Microsoft Teams", WingetId = "Microsoft.Teams",     Category = "Communication", Description = "Business communication platform" },
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
