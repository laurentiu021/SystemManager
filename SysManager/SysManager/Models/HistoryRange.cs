// SysManager · HistoryRange
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One entry in a history tab's time-range picker: the label the user sees and how far back it
/// looks. Shared by Resource History and Bandwidth Monitor so the two pickers cannot drift apart.
/// </summary>
/// <param name="Label">What the ComboBox shows, e.g. "Last 24 hours".</param>
/// <param name="Range">
/// How far back to load. <see cref="TimeSpan.Zero"/> (or negative) means the live rolling window
/// rather than a stored range — see <see cref="IsLive"/>.
/// </param>
public sealed record HistoryRange(string Label, TimeSpan Range)
{
    /// <summary>
    /// True for the live rolling window: show what is happening right now, from memory, instead of
    /// loading stored samples. Only Bandwidth Monitor offers it — Resource History is always stored.
    /// </summary>
    public bool IsLive => Range <= TimeSpan.Zero;
}
