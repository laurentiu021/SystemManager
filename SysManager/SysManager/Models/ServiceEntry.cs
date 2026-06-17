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
    /// The startup type in effect immediately before SysManager last disabled this
    /// service, captured so "Enable" can restore the exact previous type (Automatic /
    /// Manual / Boot / System) instead of always falling back to Manual. Null when
    /// SysManager has not disabled it this session.
    /// </summary>
    public string? PreviousStartType { get; set; }

    /// <summary>Gaming recommendation: "safe-to-disable", "keep-enabled", "advanced", or "" (no recommendation).</summary>
    public string Recommendation { get; init; } = "";

    /// <summary>Short explanation of what this service does and why the recommendation.</summary>
    public string RecommendationReason { get; init; } = "";

    public SafetyLevel SafetyLevel { get; init; } = SafetyLevel.Critical;
    public string SafetyDescription { get; init; } = "";
}
