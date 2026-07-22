// SysManager · AudioDevice
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// An immutable snapshot of one active audio render endpoint (output device), returned by
/// <c>IAudioMixerService.GetRenderDevices</c>. Carries no COM types so the mixer ViewModel and its
/// tests never touch Core Audio directly. <see cref="Id"/> is the Core Audio endpoint id string
/// (stable per device) used as the routing target; <see cref="IsDefault"/> marks the current
/// system default multimedia render endpoint.
/// </summary>
public sealed record AudioDevice(
    string Id,
    string FriendlyName,
    bool IsDefault);
