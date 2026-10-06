// SysManager · IBrowserExtensionService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Lists the extensions in every browser profile Browser Cleaner reads, and opens a browser on its own extensions
/// page (#1526). It never changes a browser: turning an extension off or removing it is left to the browser, which
/// asks the user itself. Behind an interface so the view model's list, ordering and messages are unit-tested with a
/// substituted service — no real profile is read and no browser is started.
/// </summary>
public interface IBrowserExtensionService
{
    /// <summary>
    /// Every profile that has an extension, or whose list could not be read, with its extensions in the order the
    /// list shows them: added by another program first, then those that can read every website, then by name.
    /// Runs off the calling thread.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled before it finishes.</exception>
    Task<IReadOnlyList<ExtensionProfile>> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts the profile's browser on its extensions page and says what it did. Nothing is started when the browser
    /// has no reliable way to be started like that, when it would not start, or while SysManager runs as
    /// administrator; the caller then tells the user where to go instead. Never throws.
    /// </summary>
    ExtensionsPageOpening OpenExtensionsPage(ExtensionProfile profile);
}
