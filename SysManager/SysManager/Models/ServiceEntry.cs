// SysManager · ServiceEntry
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a Windows service with its current state and gaming recommendation.
/// </summary>
public partial class ServiceEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _startType = "";

    /// <summary>Gaming recommendation: "safe-to-disable", "keep-enabled", "advanced", or "" (no recommendation).</summary>
    public string Recommendation { get; init; } = "";

    /// <summary>Short explanation of what this service does and why the recommendation.</summary>
    public string RecommendationReason { get; init; } = "";
}
