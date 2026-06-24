// SysManager · ProfileViewModel — export/import SysManager's own configuration
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Profile Export/Import tab. Exports SysManager's own configuration
/// (theme, speed-test history, …) to a portable JSON file and imports it on another PC,
/// with selective per-section apply. Only SysManager's app config is touched — never the
/// system — so importing is fully reversible.
/// </summary>
public sealed partial class ProfileViewModel : ViewModelBase
{
    private readonly ProfileService _service;

    /// <summary>Sections discovered for export (those whose config file exists).</summary>
    public BulkObservableCollection<SelectableSection> Sections { get; } = new();

    [ObservableProperty] private bool _hasSections;

    public ProfileViewModel(ProfileService service)
    {
        _service = service;
        RefreshSections();
        StatusMessage = "Export your SysManager settings to a file, or import a profile from another PC.";
    }

    private void RefreshSections()
    {
        var available = _service.AvailableSections()
            .Select(s => new SelectableSection(s) { IsSelected = true });
        Sections.ReplaceWith(available);
        HasSections = Sections.Count > 0;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var chosen = Sections.Where(s => s.IsSelected).Select(s => s.Section).ToList();
        if (chosen.Count == 0)
        {
            StatusMessage = "Select at least one section to export.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            FileName = $"SysManager-Profile-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "SysManager profile (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            var profile = _service.BuildProfile(DateTime.Now, chosen);
            await _service.ExportToFileAsync(dlg.FileName, profile).ConfigureAwait(true);
            StatusMessage = $"Exported {chosen.Count} section{(chosen.Count == 1 ? "" : "s")} to {Path.GetFileName(dlg.FileName)}.";
            ToastService.Instance.Show("Profile exported", Path.GetFileName(dlg.FileName));
            Log.Information("Profile: exported {Count} sections", chosen.Count);
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SysManager profile (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            ConfigProfile? profile;
            try { profile = await _service.ImportFromFileAsync(dlg.FileName).ConfigureAwait(true); }
            catch (NotSupportedException ex) { StatusMessage = ex.Message; return; }

            if (profile is null)
            {
                StatusMessage = "That file isn't a valid SysManager profile.";
                return;
            }
            if (profile.Sections.Count == 0)
            {
                StatusMessage = "The profile contains no config sections.";
                return;
            }

            var preview = string.Join("\n", profile.Sections.Select(s => $"  • {s.DisplayName}"));
            if (!DialogService.Instance.Confirm(
                    $"Import {profile.Sections.Count} section{(profile.Sections.Count == 1 ? "" : "s")} from this profile?\n\n{preview}\n\n" +
                    $"Exported {profile.ExportedAt:yyyy-MM-dd} by SysManager v{profile.AppVersion}.\n\n" +
                    "This overwrites the matching SysManager settings on this PC. Restart SysManager afterwards for all changes to take effect.",
                    "Import profile"))
            {
                StatusMessage = "Import cancelled.";
                return;
            }

            var applied = _service.ApplySections(profile.Sections);
            RefreshSections();
            StatusMessage = $"Imported {applied} section{(applied == 1 ? "" : "s")}. Restart SysManager to apply everything.";
            ToastService.Instance.Show("Profile imported", "Restart SysManager to apply all changes.");
        }
        catch (IOException ex) { StatusMessage = $"Import failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Import failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshSections();
        StatusMessage = "Section list refreshed.";
    }
}

/// <summary>A config section paired with a checkbox state for selective export.</summary>
public sealed partial class SelectableSection : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;

    public ConfigSection Section { get; }
    public string DisplayName => Section.DisplayName;

    public SelectableSection(ConfigSection section) => Section = section;
}
