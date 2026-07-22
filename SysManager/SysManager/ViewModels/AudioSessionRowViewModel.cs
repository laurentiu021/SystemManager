// SysManager · AudioSessionRowViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
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

    /// <summary>The owning app's executable path (as last resolved), used for preset keying by exe name.</summary>
    public string ExePath => _iconSourcePath;

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
    /// The output device this app is routed to. Bound to the per-row device picker when in-app
    /// routing is supported. Set from the service on refresh under the propagation guard; a
    /// user-initiated change writes through <see cref="OnSelectedOutputDeviceChanged"/>.
    /// </summary>
    [ObservableProperty] private AudioDevice? _selectedOutputDevice;

    /// <summary>
    /// True when true in-app routing is available for THIS row (the device picker is shown). False
    /// for the system-sounds pseudo-session (never routable) and when the OS lacks the routing
    /// interface. Set by the parent VM.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGuidedRouting))]
    private bool _routingSupported;

    /// <summary>
    /// True when the guided "open Windows sound settings" button should show instead of the picker:
    /// a real (non-system) app on a build without in-app routing. System sounds show neither.
    /// </summary>
    public bool ShowGuidedRouting => !RoutingSupported && !IsSystemSounds;

    /// <summary>The output devices offered in the per-row picker (shared list from the parent VM).</summary>
    public IReadOnlyList<AudioDevice> OutputDevices { get; }

    /// <summary>
    /// True while the user is actively dragging the volume slider. A background refresh must NOT
    /// overwrite <see cref="Volume"/> during a drag, or a stale snapshot value would fight the
    /// thumb. Set by the view on Thumb.DragStarted/DragCompleted.
    /// </summary>
    [ObservableProperty] private bool _isUserAdjusting;

    /// <summary>Volume as a friendly percentage, e.g. "65%".</summary>
    public string VolumeDisplay => $"{Volume * 100:F0}%";

    public AudioSessionRowViewModel(IAudioMixerService service, AudioSessionInfo info,
        IReadOnlyList<AudioDevice>? outputDevices = null, bool routingSupported = false)
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

        OutputDevices = outputDevices ?? [];
        // System-sounds can't be rerouted; only real apps get the picker.
        _routingSupported = routingSupported && !info.IsSystemSounds;
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

    /// <summary>
    /// User picked an output device for this app → route it via the service. Skipped when the
    /// change came from a refresh (guard) or when in-app routing isn't supported. A failed write
    /// (the service returned false) is left to the parent VM's status; the picker keeps the choice.
    /// </summary>
    partial void OnSelectedOutputDeviceChanged(AudioDevice? value)
    {
        if (_suppressPropagation || !RoutingSupported || value is null) return;
        _service.SetSessionOutputDevice(SessionId, value.IsDefault ? string.Empty : value.Id);
    }

    /// <summary>
    /// Guided fallback (shown when in-app routing isn't supported): open Windows' per-app volume &amp;
    /// device settings so the user can route this app there. SysManager never reroutes in this mode.
    /// </summary>
    [RelayCommand]
    private void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug("Open apps-volume settings failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Debug("Open apps-volume settings failed: {Error}", ex.Message); }
    }

    /// <summary>
    /// Sets the current output-device selection from the service snapshot without writing back
    /// (used on refresh). Matches by endpoint id; a null/empty id selects the default device.
    /// </summary>
    public void SetOutputDeviceFromService(string endpointId)
    {
        _suppressPropagation = true;
        try
        {
            SelectedOutputDevice = string.IsNullOrEmpty(endpointId)
                ? OutputDevices.FirstOrDefault(d => d.IsDefault)
                : OutputDevices.FirstOrDefault(d => string.Equals(d.Id, endpointId, StringComparison.OrdinalIgnoreCase))
                  ?? OutputDevices.FirstOrDefault(d => d.IsDefault);
        }
        finally { _suppressPropagation = false; }
    }
}
