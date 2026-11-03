// SysManager · PrivacyWriteOutcome
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>What became of writing one privacy switch.</summary>
public enum PrivacyWriteOutcome
{
    /// <summary>The switch's values were written, or put back as they were.</summary>
    Written,

    /// <summary>
    /// Windows refused the write, or the key could not be opened: as a key that needs administrator rights does
    /// without them.
    /// </summary>
    Refused,

    /// <summary>
    /// Nothing was written: SysManager could not keep a copy of what the values held first, and without it turning
    /// the switch off again could not put them back (#2778).
    /// </summary>
    NotRecorded,

    /// <summary>
    /// Nothing was put back: SysManager could not read its copy of what the values held before, so it left them as
    /// they are rather than guess (#2778).
    /// </summary>
    RecordUnreadable,
}

/// <summary>A switch that was not written, and why.</summary>
/// <param name="Toggle">The switch.</param>
/// <param name="Outcome">Why; never <see cref="PrivacyWriteOutcome.Written"/>.</param>
public sealed record PrivacyWriteFailure(PrivacyToggle Toggle, PrivacyWriteOutcome Outcome);
