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

    /// <summary>
    /// True while the tab is on screen. Set by <see cref="MainWindowViewModel.SetActive"/>.
    /// </summary>
    /// <remarks>
    /// Becoming visible re-reads the list. The tab is built once and kept for the whole session, which in tray
    /// mode can be days, so a setting saved on another tab since it opened was otherwise not offered for export
    /// until the user thought to press Refresh (#2477).
    /// </remarks>
    [ObservableProperty] private bool _isActive;

    /// <summary>The list refresh the tab started when it was last shown. Internal so a test can await it.</summary>
    internal Task ShownRefresh { get; private set; } = Task.CompletedTask;

    public ProfileViewModel(ProfileService service)
    {
        _service = service;
        StatusMessage = "Export your SysManager settings to a file, or import a profile from another PC.";
        // Read the config files off the UI thread so the eagerly-built VM doesn't block
        // startup; the collection update runs back on the UI thread.
        InitializeAsync(RefreshSectionsAsync);
    }

    partial void OnIsActiveChanged(bool value)
    {
        // Not while the first read is still running: the tab is shown the moment it is built, and that read is
        // already the fresh one.
        if (value && InitializationComplete.IsCompleted)
            ShownRefresh = RefreshSectionsAsync();
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
        Helpers.SelectionCarry.Apply(previous, fresh, s => s.Section.Key, StringComparer.Ordinal);
    }

    /// <summary>The keys of the sections the user has ticked.</summary>
    internal IReadOnlyList<string> SelectedKeys() => [.. Sections.Where(s => s.IsSelected).Select(s => s.Section.Key)];

    /// <summary>
    /// The profile an export writes: the ticked sections, read from disk now rather than when the list was built.
    /// </summary>
    /// <remarks>
    /// The list keeps each file's contents from when it was read, and export used to write those. A theme,
    /// preset or speed test changed since the tab opened was then missing from the exported file, and importing
    /// it elsewhere restored the old values (#2477). Off the UI thread, as the list's own read is.
    /// </remarks>
    internal Task<ConfigProfile> BuildExportAsync(IReadOnlyCollection<string> keys)
        => Task.Run(() => _service.BuildProfile(DateTime.Now, keys));

    [RelayCommand]
    private async Task ExportAsync()
    {
        var keys = SelectedKeys();
        if (keys.Count == 0)
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
            var profile = await BuildExportAsync(keys).ConfigureAwait(true);
            if (profile.Sections.Count < keys.Count)
                await RefreshSectionsAsync().ConfigureAwait(true);   // a ticked file is gone; so is its row
            if (profile.Sections.Count == 0)
            {
                StatusMessage = "Nothing was exported: none of the selected settings are saved on this PC any more.";
                return;
            }

            await _service.ExportToFileAsync(dlg.FileName, profile).ConfigureAwait(true);
            StatusMessage = DescribeExport(profile.Sections.Count, keys.Count, Path.GetFileName(dlg.FileName));
            ToastService.Instance.Show("Profile exported", Path.GetFileName(dlg.FileName));
            Log.Information("Profile: exported {Count} of {Selected} sections", profile.Sections.Count, keys.Count);
        }
        catch (IOException ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Export failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    /// <summary>
    /// The status after an export. The sections are read when the export runs, so one the user ticked can have
    /// been deleted since the list was built; the status then says how many were left out instead of implying
    /// every ticked one was written.
    /// </summary>
    internal static string DescribeExport(int exported, int selected, string fileName)
    {
        static string Sections(int n) => $"{n} section{(n == 1 ? "" : "s")}";
        var missing = selected - exported;
        return missing > 0
            ? $"Exported {exported} of {Sections(selected)} to {fileName}. "
              + (missing == 1
                  ? "1 is no longer saved on this PC, so it was left out."
                  : $"{missing} are no longer saved on this PC, so they were left out.")
            : $"Exported {Sections(exported)} to {fileName}.";
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
            StatusMessage = DescribeImport(applied, profile.Sections.Count);
            // Only an import that changed something is announced (#2454).
            if (applied > 0)
                ToastService.Instance.Show("Profile imported", "Restart SysManager to apply all changes.");
        }
        catch (IOException ex) { StatusMessage = $"Import failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusMessage = $"Import failed (access denied): {ex.Message}"; }
        finally { IsBusy = false; IsProgressIndeterminate = false; }
    }

    /// <summary>
    /// The status after an import. <see cref="ProfileService.ApplySections"/> skips a section it does not
    /// know, one whose content fails the import check, and one it cannot write, and only logs each skip. The
    /// confirm dialog listed all of them, so the status says how many did not land (#2454).
    /// </summary>
    internal static string DescribeImport(int applied, int total)
    {
        static string Sections(int n) => $"{n} section{(n == 1 ? "" : "s")}";
        if (applied == 0)
            return $"Nothing was imported: none of the profile's {Sections(total)} could be applied. The log has the reason.";
        if (applied < total)
            return $"Imported {applied} of {Sections(total)} — {total - applied} could not be applied, and the log has "
                + "the reason. Restart SysManager to apply the imported settings.";
        return $"Imported {Sections(applied)}. Restart SysManager to apply everything.";
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshSections();
        StatusMessage = "Section list refreshed.";
    }
}

/// <summary>A config section paired with a checkbox state for selective export.</summary>
public sealed partial class SelectableSection : ObservableObject, Helpers.ISelectableRow
{
    [ObservableProperty] private bool _isSelected = true;

    public ConfigSection Section { get; }
    public string DisplayName => Section.DisplayName;

    public SelectableSection(ConfigSection section) => Section = section;
}
