// SysManager · PrivacyAccessEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// One record of an application's access to a sensitive capability (camera, microphone, or
/// location), reconstructed from the Windows CapabilityAccessManager ConsentStore registry.
/// Read-only history — when <see cref="InUse"/> is true the app was still using the device
/// at scan time (it had a start time but no stop time).
/// </summary>
public sealed record PrivacyAccessEntry(
    string Capability,     // "Camera" / "Microphone" / "Location"
    string AppName,        // friendly app name derived from the registry key
    DateTime? LastUsed,    // local time of the most recent access, or null if unknown
    bool InUse)
{
    /// <summary>Human-readable last-used timestamp, or "In use now" / "—".</summary>
    public string LastUsedDisplay =>
        InUse ? "In use now"
              : LastUsed is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—";
}
