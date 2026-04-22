// SysManager · ConsoleViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;

namespace SysManager.ViewModels;

/// <summary>
/// Shared, scrollable console view-model. Each tab has its own instance.
/// Lines are capped to avoid unbounded memory growth on long-running installs.
/// </summary>
public partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 5000;
    private readonly object _gate = new();

    public ObservableCollection<PowerShellLine> Lines { get; } = new();

    [ObservableProperty] private bool _autoScroll = true;

    public void Append(PowerShellLine line)
    {
        // Marshal to UI thread
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(() => Append(line));
            return;
        }
        // Even on the UI thread, two services can BeginInvoke in quick
        // succession and Clear might run between reads; protect mutations.
        lock (_gate)
        {
            Lines.Add(line);
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_gate) Lines.Clear();
    }

    [RelayCommand]
    private void CopyAll()
    {
        PowerShellLine[] snapshot;
        lock (_gate) snapshot = Lines.ToArray();
        try
        {
            var text = string.Join(Environment.NewLine, snapshot.Select(l => $"[{l.Timestamp:HH:mm:ss}] {l.Kind}: {l.Text}"));
            Clipboard.SetText(text);
        }
        catch { /* ignore */ }
    }
}
