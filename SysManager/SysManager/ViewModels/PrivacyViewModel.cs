// SysManager · PrivacyViewModel — privacy toggles management
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Privacy Toggles tab. Loads registry-backed toggles,
/// groups them by category, and applies changes immediately on toggle flip.
/// </summary>
public sealed partial class PrivacyViewModel : ViewModelBase
{
    private readonly PrivacyService _service;
    private bool _suppressApply;

    public BulkObservableCollection<PrivacyToggle> Toggles { get; } = new();

    [ObservableProperty] private List<string> _categories = new();
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private bool _isElevated;

    public BulkObservableCollection<PrivacyToggle> FilteredToggles { get; } = new();

    public PrivacyViewModel(PrivacyService service)
    {
        _service = service;
        IsElevated = AdminHelper.IsElevated();
        LoadToggles();
    }

    private void LoadToggles()
    {
        _suppressApply = true;
        try
        {
            // Unsubscribe from old toggles
            foreach (var t in Toggles)
                t.PropertyChanged -= OnTogglePropertyChanged;

            var loaded = _service.LoadToggles();
            Toggles.ReplaceWith(loaded);

            // Subscribe to property changes for immediate apply
            foreach (var t in Toggles)
                t.PropertyChanged += OnTogglePropertyChanged;

            // Build category list
            var cats = new List<string> { "All" };
            cats.AddRange(Toggles.Select(t => t.Category).Distinct().OrderBy(c => c));
            Categories = cats;
            SelectedCategory = "All";

            ApplyFilter();
            UpdateStatus();
        }
        finally
        {
            _suppressApply = false;
        }
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<PrivacyToggle> source = Toggles;

        if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "All")
            source = source.Where(t => t.Category == SelectedCategory);

        FilteredToggles.ReplaceWith(source);
    }

    private void OnTogglePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressApply) return;
        if (e.PropertyName != nameof(PrivacyToggle.IsEnabled)) return;
        if (sender is not PrivacyToggle toggle) return;

        _service.ApplyToggle(toggle);
        UpdateStatus();
    }

    [RelayCommand]
    private void ApplyAll()
    {
        _service.ApplyAll(Toggles);
        StatusMessage = $"All {Toggles.Count} toggles applied.";
        Log.Information("Privacy: applied all {Count} toggles", Toggles.Count);
    }

    [RelayCommand]
    private void ResetAll()
    {
        _suppressApply = true;
        try
        {
            foreach (var toggle in Toggles)
                toggle.IsEnabled = false;
        }
        finally
        {
            _suppressApply = false;
        }

        // Write defaults to registry in batch
        _service.ApplyAll(Toggles);
        UpdateStatus();
        StatusMessage = "All toggles reset to Windows defaults.";
        Log.Information("Privacy: reset all toggles to defaults");
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadToggles();
        StatusMessage = "Toggles refreshed from registry.";
        Log.Information("Privacy: refreshed toggle states from registry");
    }

    private void UpdateStatus()
    {
        var enabledCount = Toggles.Count(t => t.IsEnabled);
        StatusMessage = $"{enabledCount} of {Toggles.Count} privacy protections active.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var t in Toggles)
                t.PropertyChanged -= OnTogglePropertyChanged;
        }
        base.Dispose(disposing);
    }
}
