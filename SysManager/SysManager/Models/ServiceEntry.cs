// SysManager · ServiceEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a Windows service with its current state and gaming recommendation.
/// </summary>
public sealed partial class ServiceEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _startType = "";
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>
    /// True when <see cref="StartType"/> is Automatic and Windows delays the start until shortly after the
    /// other automatic services — what services.msc calls "Automatic (Delayed Start)".
    /// </summary>
    /// <remarks>
    /// A separate flag because <see cref="StartType"/> is the <c>ServiceStartMode</c> name, and that enum has
    /// no delayed member: a delayed service reads as plain Automatic there. Always false for any other start
    /// type, where Windows ignores the delay setting.
    /// </remarks>
    [ObservableProperty] private bool _isDelayedAutoStart;

    /// <summary>
    /// The startup type in effect immediately before SysManager last disabled this
    /// service, captured so "Enable" can restore the exact previous type (Automatic,
    /// Automatic (Delayed Start) or Manual) instead of always falling back to Manual.
    /// Null when SysManager has not disabled it this session.
    /// </summary>
    public string? PreviousStartType { get; set; }

    /// <summary>Gaming recommendation: "safe-to-disable", "keep-enabled", "advanced", or "" (no recommendation).</summary>
    public string Recommendation { get; init; } = "";

    /// <summary>Short explanation of what this service does and why the recommendation.</summary>
    public string RecommendationReason { get; init; } = "";

    public SafetyLevel SafetyLevel { get; init; } = SafetyLevel.Critical;
    public string SafetyDescription { get; init; } = "";

    /// <summary>
    /// Display names of the services Windows would stop along with this one, in the order the service
    /// control manager reports them. Empty for most services.
    /// </summary>
    /// <remarks>
    /// Only the DEPENDENT direction is read, not <c>ServicesDependedOn</c>. Measured back to back through
    /// <c>GetAllServices</c> on a machine with 320 services: 167 ms without this read, 184 ms with it, so
    /// +17 ms or 10% — which is why it is read eagerly for every row rather than lazily on selection.
    /// Adding <c>ServicesDependedOn</c> as well cost six times more in the same comparison (+149 ms
    /// against +24 ms on an equivalent enumeration), and it is the direction that does NOT answer the
    /// question being asked: "what breaks if I turn this off" is the dependent direction, while "what does
    /// this need in order to run" would not change the decision.
    /// <para>Only 39 of those 320 services have any dependent at all (12%), which is why this is
    /// surfaced in the confirmation and the safety tooltip rather than as a grid column that would be
    /// blank on seven rows out of eight.</para>
    /// </remarks>
    public IReadOnlyList<string> DependentServices { get; init; } = [];

    /// <summary>How many other services this one carries. Zero for most.</summary>
    public bool HasDependents => DependentServices.Count > 0;

    /// <summary>
    /// The dependent services as one readable list, capped so a long one cannot grow without bound.
    /// </summary>
    /// <remarks>
    /// The single source for the NAMES; each call site supplies its own consequence, because stopping a
    /// service and disabling it do not have the same effect on its dependents and one shared sentence
    /// would have to be wrong about one of them.
    /// <para>Capped at five: a service with 25 dependents exists on an ordinary install, and an unbounded
    /// list in a confirmation dialog pushes its own buttons off screen — so the prompt asking "are you
    /// sure" would become unanswerable.</para>
    /// </remarks>
    public string DependentNames =>
        DependentServices.Count <= MaxNamesShown
            ? string.Join(", ", DependentServices)
            : string.Join(", ", DependentServices.Take(MaxNamesShown))
              + ", and " + (DependentServices.Count - MaxNamesShown)
                  .ToString(System.Globalization.CultureInfo.CurrentCulture) + " more";

    private const int MaxNamesShown = 5;

    /// <summary>
    /// One standalone sentence naming what else stops, or empty when nothing depends on this service.
    /// Used where there is no surrounding action to phrase it against, such as the safety tooltip.
    /// </summary>
    public string DependencyWarning =>
        HasDependents ? "Other services depend on this one: " + DependentNames + "." : "";

    /// <summary>
    /// What the safety pill's tooltip says: the curated safety note, plus the dependency sentence when
    /// there is one.
    /// </summary>
    /// <remarks>
    /// The pill tooltip was <see cref="SafetyDescription"/> alone. Extending it here rather than adding a
    /// column keeps the fact reachable without admin rights: the confirmation dialog that also names the
    /// dependents sits behind an elevation check, so an unelevated user would never have seen it.
    /// </remarks>
    public string SafetyTooltip =>
        DependencyWarning.Length == 0 ? SafetyDescription
        : SafetyDescription.Length == 0 ? DependencyWarning
        : SafetyDescription + "\n\n" + DependencyWarning;
}
