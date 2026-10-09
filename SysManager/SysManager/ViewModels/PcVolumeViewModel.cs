// SysManager · PcVolumeViewModel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// The This PC card on Volume Control (#1588): the whole PC's volume, mute and level, and the device all sound
/// plays through. It sits above the app rows in a card of its own, so dragging the PC's volume is not mistaken for
/// one app's.
/// <para>Shaped like <see cref="AudioSessionRowViewModel"/> on purpose. Volume and mute write through to the
/// service when the user changes them; a refresh from the service writes them under a re-entrancy guard so it is
/// not echoed back, and never moves the slider while the user holds it; a refused write is reported rather than
/// left looking applied.</para>
/// <para>The picker switches the device only where Windows lets SysManager (<see cref="CanSwitchOutput"/>).
/// Elsewhere the card names the current device, which is always readable, and offers Windows' sound settings
/// instead. Neither the PC's volume nor its device is part of a volume preset: presets keep each app's level by
/// program name, and applying one must never move the sound to other speakers.</para>
/// </summary>
public sealed partial class PcVolumeViewModel : ObservableObject, IAdjustableVolume
{
    private readonly IAudioMixerService _service;

    // Where an outcome is reported: the tab's status line. A delegate, as the rows take one.
    private readonly Action<string> _report;

    // Reads the devices and the apps again once the user has moved the sound, so everything shows where it went.
    private readonly Func<Task> _refresh;

    // Set while applying values that came FROM the service, so the change handlers do not write them back.
    private bool _suppressPropagation;

    // The device Windows was last read to play through, or the one SysManager just moved the sound to. Picking it
    // again switches nothing; compared by id because a refresh replaces the list's records.
    private string? _outputId;

    /// <summary>Builds the card over the tab's service and device list; the parent reads the first values in.</summary>
    /// <param name="outputDevices">
    /// The parent's live device list, shared with the app rows, so a device plugged in later appears here too.
    /// </param>
    /// <param name="report">Called with a finished, user-facing sentence: a refused write, or where the sound went.</param>
    /// <param name="refresh">Reads the devices and the apps again; awaited after every switch, refused or not.</param>
    public PcVolumeViewModel(IAudioMixerService service, IReadOnlyList<AudioDevice> outputDevices,
        Action<string> report, Func<Task> refresh)
    {
        _service = service;
        OutputDevices = outputDevices;
        _report = report;
        _refresh = refresh;
    }

    /// <summary>The output devices the picker offers.</summary>
    public IReadOnlyList<AudioDevice> OutputDevices { get; }

    /// <summary>True once the PC's volume has been read; false while there is no output device to control.</summary>
    [ObservableProperty] private bool _isAvailable;

    /// <summary>
    /// True once a read found no output device to control. Not simply <c>!IsAvailable</c>: before the first read
    /// the card must not say there is nothing there while it is still looking.
    /// </summary>
    [ObservableProperty] private bool _isUnavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeDisplay))]
    private float _volume;

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private float _peakLevel;

    /// <inheritdoc/>
    [ObservableProperty] private bool _isUserAdjusting;

    /// <summary>The device all sound plays through, as the picker shows it.</summary>
    [ObservableProperty] private AudioDevice? _selectedOutput;

    /// <summary>The name of the device all sound plays through; empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(ShowOutputPicker), nameof(ShowOutputSettings))]
    private string _outputName = "";

    /// <summary>True where Windows lets SysManager switch the device. Read once by the parent from the service.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutputPicker), nameof(ShowOutputSettings))]
    private bool _canSwitchOutput;

    /// <summary>The PC's volume as a percentage, e.g. "38%".</summary>
    public string VolumeDisplay => $"{Volume * 100:F0}%";

    /// <summary>True when there is a device the sound plays through.</summary>
    public bool HasOutput => OutputName.Length > 0;

    /// <summary>The picker shows where SysManager can switch the device.</summary>
    public bool ShowOutputPicker => CanSwitchOutput && HasOutput;

    /// <summary>Where it cannot, the device is named and Windows' sound settings are offered instead.</summary>
    public bool ShowOutputSettings => !CanSwitchOutput && HasOutput;

    /// <summary>
    /// The switch in flight, or the last one, so a test can wait for it to finish. Null before the first.
    /// </summary>
    internal Task? OutputSwitch { get; private set; }

    /// <summary>
    /// Shows the PC's volume and mute from a refresh without writing them back. The volume is left alone while the
    /// user holds the slider. Null — no device, or one that could not be read — darkens the card's level and hides
    /// its slider.
    /// </summary>
    public void ApplyUpdate(PcVolumeInfo? info)
    {
        _suppressPropagation = true;
        try
        {
            IsAvailable = info is not null;
            IsUnavailable = info is null;
            if (info is null)
            {
                PeakLevel = 0f;
                return;
            }
            if (!IsUserAdjusting) Volume = info.Volume;
            IsMuted = info.IsMuted;
        }
        finally { _suppressPropagation = false; }
    }

    /// <summary>
    /// Selects the device the device list flags as the one in use, without switching anything. Called after every
    /// refresh of <see cref="OutputDevices"/>, so the picker follows a change made outside SysManager.
    /// </summary>
    public void SetOutputFromService()
    {
        var current = OutputDevices.FirstOrDefault(d => d.IsDefault);
        _suppressPropagation = true;
        try
        {
            _outputId = current?.Id;
            SelectedOutput = current;
            OutputName = current?.FriendlyName ?? "";
        }
        finally { _suppressPropagation = false; }
    }

    partial void OnVolumeChanged(float value)
    {
        if (_suppressPropagation) return;
        if (!_service.SetPcVolume(value))
            _report("Could not change the volume for this PC — the speakers or headphones may have just been unplugged.");
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_suppressPropagation) return;
        if (!_service.SetPcMute(value))
            _report($"Could not {(value ? "mute" : "unmute")} this PC — the speakers or headphones may have just been unplugged.");
    }

    /// <summary>Flip the PC's mute; the change propagates via <see cref="OnIsMutedChanged(bool)"/>.</summary>
    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnSelectedOutputChanged(AudioDevice? value)
    {
        // A null is the picker losing its selection while the list is replaced, not a choice.
        if (_suppressPropagation || value is null || !CanSwitchOutput) return;
        if (string.Equals(value.Id, _outputId, StringComparison.OrdinalIgnoreCase)) return;
        OutputSwitch = SwitchOutputAsync(value);
    }

    /// <summary>
    /// Moves all sound to <paramref name="device"/> and says whether Windows did. Either way the devices and the
    /// apps are read again straight after, so the picker shows the device Windows really uses — on a refusal it
    /// goes back to it — and the app list and this card follow the sound to its new device.
    /// </summary>
    private async Task SwitchOutputAsync(AudioDevice device)
    {
        if (_service.SetDefaultOutputDevice(device.Id))
        {
            _outputId = device.Id;
            OutputName = device.FriendlyName;
            _report($"Sound now plays through {device.FriendlyName}.");
            ActivityLogService.Instance.Log("Volume", $"Sound now plays through {device.FriendlyName}");
        }
        else
        {
            _report($"Could not move the sound to {device.FriendlyName} — Windows refused the change.");
        }
        await _refresh().ConfigureAwait(true);
    }

    /// <summary>
    /// Shown instead of the picker where SysManager cannot switch the device: opens Windows' sound settings, where
    /// "Choose where to play sound" is. SysManager switches nothing in this mode.
    /// </summary>
    [RelayCommand]
    private void OpenOutputSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) { Log.Debug("Open sound settings failed: {Error}", ex.Message); }
        catch (InvalidOperationException ex) { Log.Debug("Open sound settings failed: {Error}", ex.Message); }
    }
}
