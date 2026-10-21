// SysManager · NavigationArrival
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What a tab opens on when a link sends the user to it, for a tab that can show exactly what the link counted.
/// </summary>
/// <remarks>
/// Each destination derives its own record and ignores any other, so navigation carries an arrival without knowing
/// any tab's options. The shell hands it to a tab that implements <c>IArrivalDestination</c> (#2887).
/// </remarks>
public abstract record NavigationArrival;
