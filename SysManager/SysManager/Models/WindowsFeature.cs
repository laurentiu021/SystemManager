// SysManager · WindowsFeature — model for Windows optional features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.ComponentModel;

namespace SysManager.Models;

/// <summary>
/// Represents a Windows optional feature with its current state.
/// </summary>
public sealed partial class WindowsFeature : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _requiresReboot;
    [ObservableProperty] private string _category = "Other";
    [ObservableProperty] private string _status = "";

    /// <summary>Category assignment based on feature name patterns.</summary>
    public static string CategorizeFeature(string featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName)) return "Other";

        var name = featureName.ToUpperInvariant();

        if (name.Contains("HYPER") || name.Contains("VIRTUAL") ||
            name.Contains("CONTAINER") || name.Contains("SANDBOX") ||
            name.Contains("WSL") || name.Contains("LINUX"))
            return "Virtualization";

        if (name.Contains("IIS") || name.Contains("INTERNET") ||
            name.Contains("TFTP") || name.Contains("TELNET") ||
            name.Contains("SMB") || name.Contains("NFS") ||
            name.Contains("SNMP") || name.Contains("RIP"))
            return "Networking";

        if (name.Contains("NETFX") || name.Contains(".NET") ||
            name.Contains("DEVELOPER") || name.Contains("POWERSHELL") ||
            name.Contains("OPENSSH") || name.Contains("BASH"))
            return "Development";

        if (name.Contains("MEDIA") || name.Contains("PRINT") ||
            name.Contains("XPS") || name.Contains("PDF") ||
            name.Contains("SCAN") || name.Contains("FAX"))
            return "Media & Print";

        if (name.Contains("LEGACY") || name.Contains("DIRECTPLAY") ||
            name.Contains("INDEXING") || name.Contains("WORK"))
            return "Legacy";

        return "Other";
    }
}
