// SysManager · AudioDeviceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;

namespace SysManager.Tests;

/// <summary>
/// The line the This PC picker shows under each device's name (#1588): what kind of device it is, and "in use
/// now" on the one all sound plays through.
/// </summary>
public class AudioDeviceTests
{
    [Theory]
    [InlineData(AudioDeviceKind.Headphones, true, "Headphones · in use now")]
    [InlineData(AudioDeviceKind.Headphones, false, "Headphones")]
    [InlineData(AudioDeviceKind.Speakers, false, "Speakers")]
    [InlineData(AudioDeviceKind.Headset, false, "Headset")]
    [InlineData(AudioDeviceKind.LineOut, false, "Line out, for external speakers or an amplifier")]
    [InlineData(AudioDeviceKind.DigitalOutput, false, "Digital output")]
    [InlineData(AudioDeviceKind.DisplayAudio, true, "Monitor or TV speakers, over HDMI or DisplayPort · in use now")]
    [InlineData(AudioDeviceKind.Network, false, "Over the network")]
    [InlineData(AudioDeviceKind.Unknown, true, "In use now")]
    [InlineData(AudioDeviceKind.Unknown, false, "")]
    public void TheDetail_SaysWhatTheDeviceIs_AndWhichOneIsInUse(AudioDeviceKind kind, bool inUse, string expected)
    {
        var device = new AudioDevice("{0.0.0.00000000}.{id}", "LG ULTRAGEAR (NVIDIA High Definition Audio)", inUse, kind);

        Assert.Equal(expected, device.Detail);
    }

    [Fact]
    public void EveryKnownKind_HasWordsOfItsOwn()
    {
        var kinds = Enum.GetValues<AudioDeviceKind>().Where(k => k != AudioDeviceKind.Unknown).ToList();
        Assert.True(kinds.Count >= 7, $"only {kinds.Count} known kinds — the enum no longer reads as expected");

        Assert.All(kinds, kind => Assert.NotEqual("", AudioDevice.KindText(kind)));
        Assert.Equal(kinds.Count, kinds.Select(AudioDevice.KindText).Distinct(StringComparer.Ordinal).Count());
    }
}
