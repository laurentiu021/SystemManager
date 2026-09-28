// SysManager · QuitGuard
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Helpers;

/// <summary>
/// Asks before SysManager closes while it is still working on something.
/// </summary>
/// <remarks>
/// Closing disposes every open tab. Each tab cancels what it is running, and <c>PowerShellRunner</c> answers a
/// cancellation by ending the child process, so a DISM repair, a Windows feature being turned on or a Windows Update
/// install was cut off part-way, or left running with nothing watching it, and no exit route said so (#2499). The
/// repairs, installs, clean-ups, scans and tweaks that take an <see cref="OperationLockService"/> lock are registered
/// there already, so this reads what is running from there rather than keeping a second list. An operation that
/// takes no lock is not seen here.
/// <para>One sentence serves every exit, so the prompts cannot drift apart: <see cref="ConfirmStoppingActiveWork"/>
/// for an exit with no confirmation of its own, and <see cref="ActiveWorkWarning"/> to add to one that already asks.</para>
/// </remarks>
public static class QuitGuard
{
    /// <summary>
    /// What to add to a confirmation that closes SysManager: an empty string when nothing is running, otherwise a
    /// paragraph naming what is.
    /// </summary>
    public static string ActiveWorkWarning()
    {
        var names = OperationLockService.Instance.ActiveOperations
            .Select(operation => operation.Info.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count == 0
            ? ""
            : $"\n\nSysManager is still working on: {string.Join(", ", names)}. If this window closes now, that " +
              "stops part-way.";
    }

    /// <summary>
    /// For an exit that has no confirmation of its own. True when nothing is running, or when the user chooses to
    /// stop it. False means SysManager stays open.
    /// </summary>
    /// <param name="title">The dialog's title, naming the exit: "Exit SysManager", "Run as administrator".</param>
    /// <param name="question">The closing question, naming the exit again: "Exit anyway?".</param>
    public static bool ConfirmStoppingActiveWork(string title, string question)
    {
        var warning = ActiveWorkWarning();
        return warning.Length == 0
            || DialogService.Instance.Confirm(warning.TrimStart('\n') + "\n\n" + question, title);
    }
}
