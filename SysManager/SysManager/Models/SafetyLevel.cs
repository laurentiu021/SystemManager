// SysManager · SafetyLevel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>How safe SysManager knows it is to turn a service or a Windows feature off.</summary>
/// <remarks>
/// The order is the Safety column's sort order: what is known to be harmless, then known to need care, then not known,
/// then known to break Windows. No value is stored anywhere, so a member added in the middle renumbers nothing that was
/// written down.
/// </remarks>
public enum SafetyLevel
{
    Safe,
    Caution,

    /// <summary>
    /// Not in SysManager's list, so it does not know (#1512). It used to be counted as <see cref="Critical"/>, which put
    /// the red pill on most rows and taught the user to ignore it; it is left alone just the same.
    /// </summary>
    NotRated,

    Critical,
}
