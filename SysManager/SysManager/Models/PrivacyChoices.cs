// SysManager · PrivacyChoices
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The privacy protections a profile asks for: each toggle's <see cref="PrivacyToggle.Key"/> and whether its
/// protection should be on. A desired state, never an applied one — an import hands it to the Privacy &amp;
/// Telemetry tab as pending changes, and Windows changes only if the user presses Apply there.
/// </summary>
public sealed record PrivacyChoices
{
    /// <summary>
    /// Toggle key → whether that protection should be on. A defaulted init property rather than a positional
    /// parameter for the reason <see cref="ConfigProfile.Sections"/> gives: System.Text.Json leaves a missing
    /// property null on a positional parameter, and this is read from a file someone else made.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Protections { get; init; } = new Dictionary<string, bool>();
}
