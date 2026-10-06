// SysManager · PcVolumeInfo
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// The whole PC's volume, as the Windows taskbar slider shows it: the level and mute of the default output
/// device, which apply to all sound on top of each app's own level. Returned by
/// <c>IAudioMixerService.GetPcVolume</c>. Carries no COM types, so the This PC card and its tests never touch
/// Core Audio directly (#1588). <see cref="Volume"/> is normalized 0.0–1.0.
/// </summary>
public sealed record PcVolumeInfo(float Volume, bool IsMuted);
