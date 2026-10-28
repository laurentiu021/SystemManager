// SysManager · CliInterfaceViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Helpers;
using SysManager.Models;
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

    /// <summary>The running executable's full path, or null when Windows does not say.</summary>
    private readonly Func<string?> _processPath;

    /// <summary>A ready-to-paste example invocation shown at the top of the tab.</summary>
    public string Example => Invocation("--cleanup --silent");

    public CliInterfaceViewModel() : this(() => Environment.ProcessPath)
    {
    }

    /// <summary>A tab whose commands name <paramref name="processPath"/>'s answer, so a test decides the file.</summary>
    internal CliInterfaceViewModel(Func<string?> processPath)
    {
        _processPath = processPath;
        Commands.ReplaceWith(CliRunner.Commands.Select(c => new CommandRow(c.Flags, c.Description)));
        StatusMessage = "Use these from a script, Task Scheduler, or a deployment tool. Add --json for machine-readable output. "
                        + "In PowerShell, put & and a space in front of the command.";
    }

    [RelayCommand]
    private void CopyExample() => TryCopy(Example);

    [RelayCommand]
    private void CopyCommand(CommandRow? row)
    {
        if (row is null) return;
        TryCopy(CommandFor(row));
    }

    /// <summary>What Copy puts on the clipboard for <paramref name="row"/>: a command that runs as pasted.</summary>
    /// <remarks>
    /// Only the primary flag is taken (the part before the first comma). A modifier on its own is a usage error, so
    /// <c>--json</c> and <c>--silent</c> come with <c>--health</c>, the command that changes nothing. The parser
    /// decides which rows are modifiers, so a new verb or modifier needs no list here.
    /// </remarks>
    internal string CommandFor(CommandRow row)
    {
        var primary = row.Flags.Split(',', 2)[0].Trim();
        var isModifier = CliRunner.Parse([primary]).Command == CliCommand.None;
        return Invocation(isModifier ? $"--health {primary}" : primary);
    }

    /// <summary>
    /// <paramref name="arguments"/> after the full, quoted path of the file that is running. The download is named
    /// <c>SysManager-v&lt;version&gt;.exe</c>, and a renamed or moved copy keeps its own name, so a bare
    /// <c>SysManager.exe</c> was not found from a script or Task Scheduler.
    /// </summary>
    private string Invocation(string arguments) =>
        _processPath() is { Length: > 0 } exe ? $"\"{exe}\" {arguments}" : $"SysManager.exe {arguments}";

    private void TryCopy(string text)
    {
        StatusMessage = Helpers.ClipboardFeedback.TrySetText(text)
            ? $"Copied: {text}"
            : Helpers.ClipboardFeedback.Busy;
    }
}
