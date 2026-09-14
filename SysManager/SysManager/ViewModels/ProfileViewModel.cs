// SysManager · ProfileViewModel — export/import SysManager's own configuration
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
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
    /// <inheritdoc/>
    protected internal override IRelayCommand? RefreshOnF5 => RefreshCommand;

    private readonly ProfileService _service;

    /// <summary>Sections discovered for export (those whose config file exists).</summary>
    public BulkObservableCollection<SelectableSection> Sections { get; } = new();

    [ObservableProperty] private bool _hasSections;

    public ProfileViewModel(ProfileService service)
    {
        _service = service;
        StatusMessage = "Export your SysManager settings to a file, or import a profile from another PC.";
        // Read the config files off the UI thread so the eagerly-built VM doesn't block
        // startup; the collection update runs back on the UI thread.
        InitializeAsync(RefreshSectionsAsync);
    }

    private async Task RefreshSectionsAsync()
    {
        var available = await Task.Run(_service.AvailableSections).ConfigureAwait(true);
        ApplySections(available);
    }

    private void RefreshSections() => ApplySections(_service.AvailableSections());

    private void ApplySections(IReadOnlyList<ConfigSection> available)
    {
        var fresh = available.Select(s => new SelectableSection(s)).ToList();
        CarryForwardSelection(Sections, fresh);
        Sections.ReplaceWith(fresh);
        HasSections = Sections.Count > 0;
    }

    /// <summary>
    /// Copies the user's ticks from the previous section list onto a freshly built one, matched by the
    /// section's key.
    /// </summary>
    /// <remarks>
    /// This method used to rebuild every row with <c>IsSelected = true</c> hard-coded, so a refresh did not
    /// merely forget which sections the user had chosen — it re-ticked all of them. The ticks decide what
    /// gets written into an export and what gets applied on an import, so an untick that came back meant
    /// carrying over settings the user had deliberately left behind. <c>RefreshOnF5</c> is
    /// <c>RefreshCommand</c>, which calls straight through to here, so pressing F5 was enough (#2304).
    /// <para>An empty <paramref name="previous"/> is the first population, the only time every section
    /// being ticked is the answer. A list that is present with nothing selected is a DECISION and is
    /// honoured, which is why this tests the collection being empty rather than whether anything is
    /// selected.</para>
    /// <para>Keyed on <c>Section.Key</c> rather than the display name: the key is the stable identifier the
    /// service builds the section from, and a display name is presentation text that can be reworded.</para>
    /// </remarks>
    internal static void CarryForwardSelection(
        IReadOnlyCollection<SelectableSection> previous, IReadOnlyCollection<SelectableSection> fresh)
    {
        if (previous.Count == 0) return;

        var decided = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var s in previous)
            decided[s.Section.Key] = s.IsSelected;

        foreach (var s in fresh)
        {
            if (decided.TryGetValue(s.Section.Key, out var wasSelected))
                s.IsSelected = wasSelected;
        }
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
            FileName = $"SysManager-Profile-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json",
            Filter = "SysManager profile (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
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
        finally { IsBusy = false; IsProgressIndeterminate = false; }
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
        IsProgressIndeterminate = true;
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
                    $"Exported {profile.ExportedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} by SysManager v{profile.AppVersion}.\n\n" +
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
        finally { IsBusy = false; IsProgressIndeterminate = false; }
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
