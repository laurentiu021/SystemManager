// SysManager · SpeedVerdict
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The plain-English reading of a speed-test result — what the numbers on the Speed Test tab mean for
/// the things someone actually does with their connection.
/// </summary>
/// <param name="Headline">Short answer to "is that good", e.g. "Fast connection".</param>
/// <param name="Detail">What that allows, in recognisable terms rather than in Mbps.</param>
/// <param name="ColorKey">
/// A semantic key from <see cref="Helpers.StatusColors"/>, resolved against the live theme by
/// HexToBrushConverter — never a hex literal, or the light presets would render it below the AA
/// contrast floor.
/// </param>
/// <param name="Comparison">
/// One sentence against the previous run on the same engine, or empty when there is no history.
/// </param>
/// <remarks>
/// An immutable record rather than an <c>ObservableObject</c> like <see cref="HealthDiagnostic"/>: a
/// speed test produces one verdict per completed run, so the view model swaps the whole value through
/// an <c>[ObservableProperty]</c>. HealthDiagnostic is mutable because ping updates it in place
/// several times a second and a single binding has to follow it.
/// </remarks>
public sealed record SpeedVerdict(
    string Headline,
    string Detail,
    string ColorKey,
    string Comparison);
