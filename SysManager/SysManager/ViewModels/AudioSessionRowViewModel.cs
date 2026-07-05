// SysManager · AudioSessionRowViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// One row in the per-app volume mixer: an application's icon, name, volume slider, mute
/// toggle, and live peak meter. <see cref="Volume"/> (0–1) and <see cref="IsMuted"/>
/// propagate to <see cref="IAudioMixerService"/> when the user changes them; when the
/// service reports an external change during a refresh, <see cref="ApplyUpdate"/> writes
/// the new values under a re-entrancy guard so they are NOT echoed back to the service.
/// </summary>
public sealed partial class AudioSessionRowViewModel : ObservableObject
{
    private readonly IAudioMixerService _service;

    // Set while applying values that came FROM the service, so the property-changed
    // callbacks don't turn an external/refresh update into a redundant write back.
    private bool _suppressPropagation;

    /// <summary>Stable session-instance key used to correlate this row across refreshes.</summary>
    public string SessionId { get; }

    public uint ProcessId { get; }
    public bool IsSystemSounds { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeDisplay))]
    private float _volume;

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private float _peakLevel;

    /// <summary>Volume as a friendly percentage, e.g. "65%".</summary>
    public string VolumeDisplay => $"{Volume * 100:F0}%";

    public AudioSessionRowViewModel(IAudioMixerService service, AudioSessionInfo info)
    {
        _service = service;
        SessionId = info.SessionId;
        ProcessId = info.ProcessId;
        IsSystemSounds = info.IsSystemSounds;

        _displayName = info.DisplayName;
        _volume = info.Volume;
        _isMuted = info.IsMuted;
        _peakLevel = info.PeakLevel;
        IsActive = info.State == AudioSessionState.Active;
        Icon = info.IsSystemSounds
            ? IconExtractorService.WindowsIcon
            : IconExtractorService.GetProcessIcon(info.ExePath, info.DisplayName);
    }

    /// <summary>
    /// Update this row in place from a fresh service snapshot (a refresh tick). Volume and
    /// mute are written under the re-entrancy guard so a change that originated in the
    /// system — not the user — is not written straight back to the service.
    /// </summary>
    public void ApplyUpdate(AudioSessionInfo info)
    {
        _suppressPropagation = true;
        try
        {
            DisplayName = info.DisplayName;
            Volume = info.Volume;
            IsMuted = info.IsMuted;
            IsActive = info.State == AudioSessionState.Active;
        }
        finally
        {
            _suppressPropagation = false;
        }
    }

    partial void OnVolumeChanged(float value)
    {
        if (_suppressPropagation) return;
        _service.SetVolume(SessionId, value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_suppressPropagation) return;
        _service.SetMute(SessionId, value);
    }

    /// <summary>Flip the mute state; the change propagates via <see cref="OnIsMutedChanged"/>.</summary>
    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;
}
