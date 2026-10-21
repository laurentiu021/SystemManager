// SysManager · FeatureChangeResult
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What Windows answered when an optional feature was turned on or off.
/// </summary>
/// <param name="Success">Whether the change went through.</param>
/// <param name="RebootRequired">Whether it takes effect only after a restart.</param>
/// <param name="ErrorCode">
/// DISM's error code for a change that did not go through, or null when there was none to read. It is the same in
/// every language, so it decides what the tab says.
/// </param>
/// <param name="ErrorMessage">The first line of DISM's own message for that error, in Windows' language, or "".</param>
public sealed record FeatureChangeResult(
    bool Success,
    bool RebootRequired,
    int? ErrorCode = null,
    string ErrorMessage = "");
