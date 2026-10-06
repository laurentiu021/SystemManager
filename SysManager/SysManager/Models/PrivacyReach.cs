// SysManager · PrivacyReach
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>Whose settings a privacy switch changes, which is also whether it needs administrator rights.</summary>
public enum PrivacyReach
{
    /// <summary>Only the signed-in account's own settings (HKCU). No administrator needed.</summary>
    JustYou,

    /// <summary>Windows for every account on the PC (HKLM). Needs administrator.</summary>
    EveryoneOnThisPc,
}

/// <summary>How Privacy &amp; Telemetry lists its switches (#1517).</summary>
public enum PrivacyGrouping
{
    /// <summary>One list, narrowed by the topic filter.</summary>
    ByTopic,

    /// <summary>Two lists: the switches that change only your account, then those that change the whole PC.</summary>
    ByReach,
}

/// <summary>One "By reach" section of Privacy &amp; Telemetry: its heading, what it means, and its switches.</summary>
/// <param name="Reach">Whose settings these switches change.</param>
/// <param name="Title">The heading with its count, e.g. "Just you — 8 switches".</param>
/// <param name="Description">What the reach means for the person flipping them.</param>
/// <param name="Toggles">The switches, in the order the list shows them.</param>
public sealed record PrivacyReachGroup(
    PrivacyReach Reach, string Title, string Description, IReadOnlyList<PrivacyToggle> Toggles);
