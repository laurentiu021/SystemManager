// SysManager · FeatureParent
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A Windows optional feature that another one needs, as DISM reports it.
/// </summary>
/// <param name="Name">The feature's name, such as <c>IIS-WebServerRole</c>.</param>
/// <param name="IsEnabled">Whether it is on, or turns on at the next restart.</param>
/// <param name="Pending">Whether a change to it waits for a restart.</param>
public sealed record FeatureParent(string Name, bool IsEnabled, bool Pending);
