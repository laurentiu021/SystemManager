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

    // The exe path the current Icon was extracted from — lets ApplyUpdate detect when a row's
    // resolved identity changed and re-extract the icon rather than showing a stale one.
    private string _iconSourcePath;

    /// <summary>
    /// Per-app group key (derived from the session-instance identifier, PID-reuse-proof) used to
    /// correlate this row across refreshes. See <see cref="AudioSessionInfo.SessionId"/>.
    /// </summary>
    public string SessionId { get; }

    public bool IsSystemSounds { get; }

    [ObservableProperty] private uint _processId;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeDisplay))]
    private float _volume;

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private float _peakLevel;

    /// <summary>
    /// True while the user is actively dragging the volume slider. A background refresh must NOT
    /// overwrite <see cref="Volume"/> during a drag, or a stale snapshot value would fight the
    /// thumb. Set by the view on Thumb.DragStarted/DragCompleted.
    /// </summary>
    [ObservableProperty] private bool _isUserAdjusting;

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
        _iconSourcePath = info.ExePath;
        Icon = ResolveIcon(info);
    }

    private static ImageSource? ResolveIcon(AudioSessionInfo info) =>
        info.IsSystemSounds
            ? IconExtractorService.WindowsIcon
            : IconExtractorService.GetProcessIcon(info.ExePath, info.DisplayName);

    /// <summary>
    /// Update this row in place from a fresh service snapshot (a refresh tick). Volume and
    /// mute are written under the re-entrancy guard so a change that originated in the
    /// system — not the user — is not written straight back to the service. Volume is left
    /// untouched while the user is dragging the slider (see <see cref="IsUserAdjusting"/>).
    /// The icon is re-extracted only when the resolved identity actually changed, so a row that
    /// somehow rebinds to a different process shows the correct icon rather than a stale one.
    /// </summary>
    public void ApplyUpdate(AudioSessionInfo info)
    {
        _suppressPropagation = true;
        try
        {
            DisplayName = info.DisplayName;
            if (!IsUserAdjusting) Volume = info.Volume;
            IsMuted = info.IsMuted;
            IsActive = info.State == AudioSessionState.Active;

            if (info.ProcessId != ProcessId ||
                !string.Equals(info.ExePath, _iconSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                ProcessId = info.ProcessId;
                _iconSourcePath = info.ExePath;
                Icon = ResolveIcon(info);
            }
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
