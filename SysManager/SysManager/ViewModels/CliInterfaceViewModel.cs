// SysManager · CliInterfaceViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// Reference tab for the command-line interface. Lists the available CLI commands (the
/// single source of truth is <see cref="CliRunner.Commands"/>) so a user can discover and
/// copy them for use in scripts or Task Scheduler. Read-only: this tab runs nothing, it
/// just documents the headless commands the executable accepts.
/// </summary>
public sealed partial class CliInterfaceViewModel : ViewModelBase
{
    public sealed record CommandRow(string Flags, string Description);

    public BulkObservableCollection<CommandRow> Commands { get; } = new();

    /// <summary>A ready-to-paste example invocation shown at the top of the tab.</summary>
    public string Example => "SysManager.exe --cleanup --silent";

    public CliInterfaceViewModel()
    {
        Commands.ReplaceWith(CliRunner.Commands.Select(c => new CommandRow(c.Flags, c.Description)));
        StatusMessage = "Use these from a script, Task Scheduler, or a deployment tool. Add --json for machine-readable output.";
    }

    [RelayCommand]
    private void CopyExample() => TryCopy(Example);

    [RelayCommand]
    private void CopyCommand(CommandRow? row)
    {
        if (row is null) return;
        // Copy just the primary flag (the part before the first comma) so it pastes clean.
        var primary = row.Flags.Split(',', 2)[0].Trim();
        TryCopy($"SysManager.exe {primary}");
    }

    private void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            StatusMessage = $"Copied: {text}";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be transiently locked by another process; report, don't crash.
            StatusMessage = "Could not access the clipboard — try again.";
        }
    }
}
