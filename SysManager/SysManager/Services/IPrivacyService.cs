// SysManager · IPrivacyService — testable seam for the privacy toggles
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Seam over <see cref="PrivacyService"/>, so the tabs and the profile that read and write the privacy toggles
/// can be tested against toggles the test chooses rather than whatever this machine's registry holds.
/// </summary>
/// <remarks>
/// A profile export carries the toggles as the registry reads them, so a test of that export built on the real
/// service would pass or fail with the privacy settings of the machine running it. The registry-root seam on
/// <see cref="PrivacyService"/> stays for the tests that prove the writes themselves.
/// </remarks>
public interface IPrivacyService
{
    /// <summary>Creates the full toggle list with the current state read from the registry.</summary>
    List<PrivacyToggle> LoadToggles();

    /// <summary>
    /// Writes one toggle's <see cref="PrivacyToggle.IsEnabled"/> state to the registry. Returns false when the
    /// write was refused, as an HKLM toggle is without elevation, so the caller does not report it as applied.
    /// </summary>
    bool ApplyToggle(PrivacyToggle toggle);

    /// <summary>
    /// Applies every toggle in turn and returns the ones that failed, empty when all succeeded. One failure does
    /// not stop the rest.
    /// </summary>
    IReadOnlyList<PrivacyToggle> ApplyAll(IEnumerable<PrivacyToggle> toggles);
}
