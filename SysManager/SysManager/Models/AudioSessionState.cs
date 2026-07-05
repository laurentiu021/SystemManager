// SysManager · AudioSessionState
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// Lifecycle state of a per-application audio session, mirroring the Windows Core Audio
/// <c>AudioSessionState</c> enum. The mixer drops <see cref="Expired"/> sessions (their
/// process has ended) and shows <see cref="Active"/> + <see cref="Inactive"/> ones —
/// an inactive session belongs to a running app that simply isn't rendering right now,
/// and is still worth showing and controlling.
/// </summary>
public enum AudioSessionState
{
    /// <summary>The process owns a session but isn't currently rendering audio.</summary>
    Inactive = 0,

    /// <summary>The process is actively rendering audio.</summary>
    Active = 1,

    /// <summary>The process has ended / the stream was torn down — dropped from the list.</summary>
    Expired = 2,
}
