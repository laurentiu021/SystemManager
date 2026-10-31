// SysManager · UnappliedChanges — what Refresh asks before it reads a tab again over changes not made yet
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// The question a tab that keeps changes until Apply or Save asks before Refresh, or the F5 that runs it, reads
/// its list again, and what the tab says when the user keeps them. One wording for the four tabs that do (#2734).
/// </summary>
/// <remarks>
/// A read replaces the list and the baseline the changes are counted against, so every change not made yet went
/// with no word. The tabs ask rather than carry the changes across: each was made against the values read before,
/// and the new read can find those different.
/// </remarks>
internal static class UnappliedChanges
{
    /// <summary>The title of the question, worded like the tabs' other confirmations.</summary>
    internal const string Title = "Confirm Refresh";

    /// <summary>The question, for a tab whose changes <paramref name="button"/> makes: "Apply" or "Save".</summary>
    internal static string Question(string button) =>
        "Refresh, and lose your pending changes?\n\n"
        + $"Refresh reads the list again from Windows, without them. Choose No to keep them, then press {button} to "
        + "make them.";

    /// <summary>What the tab says when the user chose No, for a tab whose changes <paramref name="button"/> makes.</summary>
    internal static string Kept(string button) =>
        $"Refresh cancelled. Your pending changes are kept — press {button} to make them.";
}
