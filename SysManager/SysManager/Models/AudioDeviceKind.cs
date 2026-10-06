// SysManager · AudioDeviceKind
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// What kind of output device an <see cref="AudioDevice"/> is, from the form factor its driver reports
/// (<c>PKEY_AudioEndpoint_FormFactor</c>, a documented endpoint property). It is what tells a device named
/// "LG ULTRAGEAR (NVIDIA High Definition Audio)" apart as the monitor's speakers, which the name alone does
/// not say to most people (#1588).
/// </summary>
public enum AudioDeviceKind
{
    /// <summary>The driver reported no form factor, or one that is not an output.</summary>
    Unknown = 0,

    /// <summary>Speakers, built in or external.</summary>
    Speakers,

    /// <summary>Headphones without a microphone.</summary>
    Headphones,

    /// <summary>Headphones with a microphone, or a phone handset.</summary>
    Headset,

    /// <summary>A line-level output socket, which usually feeds external speakers or an amplifier.</summary>
    LineOut,

    /// <summary>A digital output: S/PDIF (optical or coaxial) or another digital pass-through.</summary>
    DigitalOutput,

    /// <summary>A monitor or TV with speakers, connected over HDMI or DisplayPort.</summary>
    DisplayAudio,

    /// <summary>A device reached over the network.</summary>
    Network,
}
