// SysManager · PrivacyAccessReport
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One read of the Windows consent store: the access it recorded, and the capabilities it could not read.
/// </summary>
/// <param name="Entries">The access records, most recent first.</param>
/// <param name="Unreadable">
/// The capabilities ("Camera", "Microphone", "Location") whose history could not be read, in the order the tab
/// lists them. Empty when every one was read.
/// </param>
public sealed record PrivacyAccessReport(
    IReadOnlyList<PrivacyAccessEntry> Entries,
    IReadOnlyList<string> Unreadable);
