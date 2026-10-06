// SysManager · BatteryWearVerdict
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The plain-English reading of a battery's capacity history: whether it is wearing at the rate ordinary use costs.
/// </summary>
/// <param name="Headline">Short answer, e.g. "Normal wear".</param>
/// <param name="Detail">What it lost over what period, and what (if anything) helps.</param>
/// <param name="ColorKey">
/// A semantic key from <see cref="Helpers.StatusColors"/>, resolved against the live theme, never a hex literal.
/// Never the failure colour: a worn battery is not a fault, and the wording never says it is dying.
/// </param>
public sealed record BatteryWearVerdict(string Headline, string Detail, string ColorKey);
