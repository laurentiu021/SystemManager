// SysManager · AppBlockerViewModel — block/unblock applications from running
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// App Blocker tab — prevents selected applications from executing using
/// Image File Execution Options (IFEO) registry mechanism.
/// </summary>
public sealed partial class AppBlockerViewModel : ViewModelBase
{
    public BulkObservableCollection<BlockedApp> BlockedApps { get; } = new();

    [ObservableProperty] private string _newExeName = "";
    [ObservableProperty] private string _blockStatus = "Enter an executable name and click Block to prevent it from running.";
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private bool _isElevated;

    private readonly IAppBlockerService _blocker;

    public AppBlockerViewModel(IAppBlockerService blocker)
    {
        _blocker = blocker;
        IsElevated = AdminHelper.IsElevated();
        // Walk the IFEO registry tree off the UI thread so the eagerly-built VM doesn't
        // block startup; the UI update runs back on the UI thread (ConfigureAwait true).
        InitializeAsync(RefreshListAsync);
    }

    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        if (AdminHelper.RelaunchAsAdmin())
            Application.Current?.Shutdown();
    }

    private async Task RefreshListAsync()
    {
        var apps = await Task.Run(_blocker.GetBlockedApps).ConfigureAwait(true);
        ApplyBlockedApps(apps);
    }

    [RelayCommand]
    private void RefreshList() => ApplyBlockedApps(_blocker.GetBlockedApps());

    private void ApplyBlockedApps(IReadOnlyList<BlockedApp> apps)
    {
        BlockedApps.ReplaceWith(apps);
        BlockedCount = BlockedApps.Count;
        BlockStatus = BlockedCount == 0
            ? "No applications are currently blocked."
            : $"{BlockedCount} application{(BlockedCount == 1 ? "" : "s")} blocked.";
    }

    [RelayCommand]
    private void BlockApp()
    {
        if (string.IsNullOrWhiteSpace(NewExeName))
        {
            BlockStatus = "Enter an executable name (e.g., notepad.exe).";
            return;
        }

        if (!IsElevated)
        {
            BlockStatus = "Blocking requires administrator privileges.";
            return;
        }

        var exeName = NewExeName.Trim();
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName += ".exe";

        if (_blocker.IsBlocked(exeName))
        {
            BlockStatus = $"{exeName} is already blocked.";
            return;
        }

        if (!DialogService.Instance.Confirm(
            $"Block \"{exeName}\" from running?\n\nThis will prevent the application from launching until you unblock it.",
            "Block Application — Confirm")) return;

        var success = _blocker.BlockApp(exeName);
        if (success)
        {
            NewExeName = "";
            RefreshList();
            BlockStatus = $"Blocked {exeName}.";
            Log.Information("User blocked application: {ExeName}", exeName);
        }
        else
        {
            BlockStatus = $"Failed to block {exeName} — check admin privileges.";
        }
    }

    [RelayCommand]
    private void UnblockSelected()
    {
        var selected = BlockedApps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            BlockStatus = "Select applications to unblock.";
            return;
        }

        if (!DialogService.Instance.Confirm(
            $"Unblock {selected.Count} application{(selected.Count == 1 ? "" : "s")}?\n\nThey will be allowed to run again.",
            "Unblock Applications — Confirm")) return;

        int unblocked = 0;
        foreach (var app in selected)
        {
            if (_blocker.UnblockApp(app.ExecutableName))
                unblocked++;
        }

        RefreshList();
        BlockStatus = $"Unblocked {unblocked} application{(unblocked == 1 ? "" : "s")}.";
        Log.Information("User unblocked {Count} applications", unblocked);
    }

    [RelayCommand]
    private void BrowseForExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select application to block",
            Filter = "Executables (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            NewExeName = System.IO.Path.GetFileName(dialog.FileName);
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var a in BlockedApps) a.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var a in BlockedApps) a.IsSelected = false;
    }
}
