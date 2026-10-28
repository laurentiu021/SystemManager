// SysManager · ClipboardFeedback — one wording for every "copy to clipboard" button
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using System.Windows;

namespace SysManager.Helpers;

/// <summary>
/// Copies text to the clipboard, catching the one failure every copy button across the app can hit, so
/// the busy-clipboard wording cannot drift between tabs the way it already had.
/// </summary>
/// <remarks>
/// Before this: Large Files' Copy path swallowed a busy clipboard with no message at all; Duplicate
/// Finder's and Disk Analyzer's equivalents only logged the failure at Debug level, where no user ever
/// reads it; the live output console's Copy all did the same; System Logs showed the raw exception
/// message, which names <c>System.Runtime.InteropServices.ExternalException</c> rather than a sentence
/// a user can act on; and About, System Health, System Report and the command-line each separately
/// invented their own wording for the identical failure — three different sentences for "the clipboard
/// is in use by another program" (#2859).
/// <para>Only the FAILURE half is forced into one sentence (<see cref="Busy"/>): what each tab says on
/// SUCCESS still names the specific thing it copied ("BIOS info copied to clipboard.", "Report copied to
/// clipboard."), which is more useful than a single generic sentence and is not the part that had drifted
/// into inconsistency — the busy case had.</para>
/// </remarks>
internal static class ClipboardFeedback
{
    /// <summary>What a tab's status line says once the clipboard actually held the text, when it has no
    /// more specific success wording of its own.</summary>
    internal const string Copied = "Copied to clipboard.";

    /// <summary>
    /// What a tab's status line says when the clipboard refused it — another program is holding it, or
    /// no clipboard is available at all (a headless host).
    /// </summary>
    internal const string Busy = "Could not copy — the clipboard is busy or unavailable. Try again.";

    /// <summary>
    /// Writes <paramref name="text"/> to the real clipboard. True on success; false on the one failure
    /// every copy button can hit, so the caller can show <see cref="Busy"/> or its own equivalent.
    /// </summary>
    internal static bool TrySetText(string text) => TrySetText(text, Clipboard.SetText);

    /// <summary>
    /// <see cref="TrySetText(string)"/>, through <paramref name="setText"/> instead of the real
    /// clipboard — the seam a test uses to prove the busy case without needing another process
    /// actually holding the OS clipboard, which no test can arrange deterministically.
    /// </summary>
    /// <remarks>
    /// The three catches are every exception <see cref="Clipboard.SetText(string)"/> is documented to
    /// raise: another process holding the clipboard (<see cref="ExternalException"/>), no STA message
    /// loop to own it (<see cref="System.Threading.ThreadStateException"/>), and no clipboard at all,
    /// such as a headless test host (<see cref="InvalidOperationException"/>).
    /// </remarks>
    internal static bool TrySetText(string text, Action<string> setText)
    {
        try
        {
            setText(text);
            return true;
        }
        catch (ExternalException) { return false; }
        catch (System.Threading.ThreadStateException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the clipboard and returns <see cref="Copied"/> or <see cref="Busy"/>,
    /// for a caller with no more specific success wording of its own.
    /// </summary>
    internal static string TrySetTextAndDescribe(string text) => TrySetText(text) ? Copied : Busy;
}
