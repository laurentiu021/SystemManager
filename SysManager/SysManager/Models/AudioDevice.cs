// SysManager · AudioDevice
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// An immutable snapshot of one active audio render endpoint (output device), returned by
/// <c>IAudioMixerService.GetRenderDevices</c>. Carries no COM types so the mixer ViewModel and its
/// tests never touch Core Audio directly. <see cref="Id"/> is the Core Audio endpoint id string
/// (stable per device) used as the routing target; <see cref="IsDefault"/> marks the current
/// system default multimedia render endpoint. <see cref="Kind"/> is what the driver says the device is,
/// <see cref="AudioDeviceKind.Unknown"/> when it says nothing.
/// </summary>
public sealed record AudioDevice(
    string Id,
    string FriendlyName,
    bool IsDefault,
    AudioDeviceKind Kind = AudioDeviceKind.Unknown)
{
    /// <summary>
    /// The line the This PC picker shows under the device's name: what kind of device it is, and "in use now"
    /// on the one all sound plays through (#1588). Empty for a device of unknown kind that is not in use.
    /// </summary>
    public string Detail => IsDefault
        ? KindText(Kind) is { Length: > 0 } kind ? $"{kind} · in use now" : "In use now"
        : KindText(Kind);

    /// <summary>The kind in plain words, or empty when the driver did not say.</summary>
    internal static string KindText(AudioDeviceKind kind) => kind switch
    {
        AudioDeviceKind.Speakers => "Speakers",
        AudioDeviceKind.Headphones => "Headphones",
        AudioDeviceKind.Headset => "Headset",
        AudioDeviceKind.LineOut => "Line out, for external speakers or an amplifier",
        AudioDeviceKind.DigitalOutput => "Digital output",
        AudioDeviceKind.DisplayAudio => "Monitor or TV speakers, over HDMI or DisplayPort",
        AudioDeviceKind.Network => "Over the network",
        _ => "",
    };
}
