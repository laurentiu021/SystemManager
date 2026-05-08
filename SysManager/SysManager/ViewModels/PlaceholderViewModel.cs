// SysManager · PlaceholderViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.ViewModels;

/// <summary>
/// Lightweight placeholder ViewModel for tabs that are planned but not yet
/// implemented. Displays the feature name and a "Work in Progress" message.
/// Replace with a real ViewModel when the feature is built.
/// </summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    public string FeatureName { get; }
    public string Description { get; }
    public string IssueNumber { get; }

    public PlaceholderViewModel(string featureName, string description, string issueNumber)
    {
        FeatureName = featureName;
        Description = description;
        IssueNumber = issueNumber;
    }
}
