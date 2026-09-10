// SysManager · SignatureTrust
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What a certificate check could establish about an executable on disk.
/// </summary>
/// <remarks>
/// Its own file rather than a companion inside <c>StartupEntry.cs</c>, where it started: the Startup
/// Manager asked the question first, but a running process and a startup entry ask the identical one, and
/// a type shared by two models does not belong inside one of them.
/// </remarks>
public enum SignatureTrust
{
    /// <summary>
    /// Nothing was checked — no readable file was resolved for the entry. Renders no pill at all: this
    /// says something about the scan, not about the program, and a grey "unknown" chip would read as a
    /// verdict.
    /// </summary>
    Unknown,

    /// <summary>
    /// No embedded signature. Ordinary, not suspicious — most small utilities are unsigned, and so are
    /// SysManager's own builds.
    /// </summary>
    Unsigned,

    /// <summary>Signed, and the certificate's chain validated to a trusted root.</summary>
    Verified,

    /// <summary>
    /// Signed, but the signature could not be confirmed — the chain failed to validate, or the signature
    /// data itself could not be read. The one state here that deserves attention.
    /// </summary>
    Invalid,
}
