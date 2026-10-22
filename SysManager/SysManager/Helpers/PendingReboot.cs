// SysManager · PendingReboot
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Security;
using Microsoft.Win32;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// Whether Windows is waiting for a restart, and for what, read from the three places it records one: component
/// servicing, which a Windows feature change sets; Windows Update; and files a program asked Windows to replace at the
/// next start.
/// </summary>
/// <remarks>
/// The one check the Dashboard's alert and the Windows Update tab both make (#2802). The Dashboard read only Windows
/// Update's key, so after a feature change, or after an installer scheduled files to replace, it showed a green "No
/// pending reboots" while the Windows Update tab's own check, a script of its own, said a reboot was required.
/// </remarks>
internal static class PendingReboot
{
    internal const string ComponentServicingKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    internal const string WindowsUpdateKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

    internal const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    internal const string FileRenamesValue = "PendingFileRenameOperations";

    /// <summary>
    /// What a restart is pending for, in the words both tabs show, in that order; empty when nothing is, or null when
    /// the registry could not be read.
    /// </summary>
    /// <param name="machine">
    /// The root the paths are read under: the local machine's, unless a test gives its own.
    /// </param>
    internal static IReadOnlyList<string>? Reasons(RegistryKey? machine = null)
    {
        var root = machine ?? Registry.LocalMachine;
        try
        {
            List<string> reasons = [];
            if (Exists(root, ComponentServicingKey)) reasons.Add("Windows features or components");
            if (Exists(root, WindowsUpdateKey)) reasons.Add("Windows Update");
            using (var session = root.OpenSubKey(SessionManagerKey))
            {
                // A multi-string of source and target pairs, where a file to delete has an empty target.
                if (session?.GetValue(FileRenamesValue) is string[] renames && renames.Any(r => r.Length > 0))
                    reasons.Add("files waiting to be replaced");
            }

            return reasons;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Debug("Pending reboot could not be read: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>The line the Windows Update tab's check prints for <paramref name="reasons"/>.</summary>
    internal static string Describe(IReadOnlyList<string>? reasons) => reasons switch
    {
        null => "Could not read whether a reboot is pending.",
        [] => "No pending reboot detected.",
        _ => $"REBOOT REQUIRED - reasons: {string.Join(", ", reasons)}",
    };

    private static bool Exists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }
}
