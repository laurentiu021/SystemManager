// SysManager · PcVolumeViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The This PC card on Volume Control (#1588): the whole PC's volume, mute and level, and the device all sound
/// plays through. The audio service is substituted, so nothing here changes this machine's volume or its output
/// device.
/// </summary>
/// <remarks>
/// In the serialized collection because a switch writes to <see cref="ActivityLogService.Instance"/>, which one
/// test here swaps for a scope of its own.
/// </remarks>
[Collection("ProcessWideStatics")]
public class PcVolumeViewModelTests
{
    private static readonly AudioDevice Headphones =
        new("{0.0.0.00000000}.{headphones}", "Headphones (Studio)", IsDefault: true, AudioDeviceKind.Headphones);

    private static readonly AudioDevice Speakers =
        new("{0.0.0.00000000}.{speakers}", "Speakers (Desk)", IsDefault: false, AudioDeviceKind.Speakers);

    /// <summary>A card over a substituted service and a device list, with what it reports and refreshes recorded.</summary>
    private sealed class Card
    {
        public IAudioMixerService Service { get; } = Substitute.For<IAudioMixerService>();
        public BulkObservableCollection<AudioDevice> Devices { get; } = new();
        public List<string> Reports { get; } = [];
        public int Refreshes { get; private set; }
        public PcVolumeViewModel Vm { get; }

        public Card(bool canSwitch = true, params AudioDevice[] devices)
        {
            Devices.ReplaceWith(devices);
            Vm = new PcVolumeViewModel(Service, Devices, Reports.Add, () =>
            {
                Refreshes++;
                return Task.CompletedTask;
            })
            {
                CanSwitchOutput = canSwitch,
            };
            Vm.SetOutputFromService();
        }
    }

    // ── Volume and mute ─────────────────────────────────────────────────────

    [Fact]
    public void ARefresh_ShowsThePcVolumeAndMute_WithoutWritingThemBack()
    {
        var card = new Card();

        card.Vm.ApplyUpdate(new PcVolumeInfo(0.38f, IsMuted: true));

        Assert.True(card.Vm.IsAvailable);
        Assert.False(card.Vm.IsUnavailable);
        Assert.Equal(0.38f, card.Vm.Volume);
        Assert.Equal("38%", card.Vm.VolumeDisplay);
        Assert.True(card.Vm.IsMuted);
        card.Service.DidNotReceive().SetPcVolume(Arg.Any<float>());
        card.Service.DidNotReceive().SetPcMute(Arg.Any<bool>());
    }

    [Fact]
    public void ARefresh_LeavesTheVolumeAlone_WhileTheUserHoldsTheSlider()
    {
        var card = new Card();
        card.Vm.ApplyUpdate(new PcVolumeInfo(0.5f, IsMuted: false));
        card.Vm.IsUserAdjusting = true;

        card.Vm.ApplyUpdate(new PcVolumeInfo(0.9f, IsMuted: true));

        Assert.Equal(0.5f, card.Vm.Volume);
        Assert.True(card.Vm.IsMuted);
    }

    [Fact]
    public void ARefreshThatReadNothing_SaysThereIsNoDevice_AndDarkensTheMeter()
    {
        var card = new Card();
        card.Vm.ApplyUpdate(new PcVolumeInfo(0.5f, IsMuted: false));
        card.Vm.PeakLevel = 0.7f;

        card.Vm.ApplyUpdate(null);

        Assert.False(card.Vm.IsAvailable);
        Assert.True(card.Vm.IsUnavailable);
        Assert.Equal(0f, card.Vm.PeakLevel);
    }

    [Fact]
    public void BeforeTheFirstRead_TheCardDoesNotSayThereIsNoDevice()
    {
        var card = new Card();

        Assert.False(card.Vm.IsAvailable);
        Assert.False(card.Vm.IsUnavailable);
    }

    [Fact]
    public void MovingTheSlider_SetsThePcVolume()
    {
        var card = new Card();
        card.Service.SetPcVolume(Arg.Any<float>()).Returns(true);

        card.Vm.Volume = 0.25f;

        card.Service.Received(1).SetPcVolume(0.25f);
        Assert.Empty(card.Reports);
    }

    [Fact]
    public void AVolumeWindowsRefused_IsReported()
    {
        var card = new Card();
        card.Service.SetPcVolume(Arg.Any<float>()).Returns(false);

        card.Vm.Volume = 0.25f;

        Assert.Equal(
            ["Could not change the volume for this PC — the speakers or headphones may have just been unplugged."],
            card.Reports);
    }

    [Fact]
    public void TheMuteButton_MutesThePc()
    {
        var card = new Card();
        card.Service.SetPcMute(Arg.Any<bool>()).Returns(true);

        card.Vm.ToggleMuteCommand.Execute(null);

        Assert.True(card.Vm.IsMuted);
        card.Service.Received(1).SetPcMute(true);
        Assert.Empty(card.Reports);
    }

    [Theory]
    [InlineData(false, "Could not mute this PC — the speakers or headphones may have just been unplugged.")]
    [InlineData(true, "Could not unmute this PC — the speakers or headphones may have just been unplugged.")]
    public void AMuteWindowsRefused_IsReported(bool mutedBefore, string expected)
    {
        var card = new Card();
        card.Vm.ApplyUpdate(new PcVolumeInfo(0.5f, mutedBefore));
        card.Service.SetPcMute(Arg.Any<bool>()).Returns(false);

        card.Vm.ToggleMuteCommand.Execute(null);

        Assert.Equal([expected], card.Reports);
    }

    // ── Where the sound plays ───────────────────────────────────────────────

    [Fact]
    public void TheDeviceList_SelectsTheDeviceInUse_WithoutSwitchingAnything()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);

        Assert.Same(Headphones, card.Vm.SelectedOutput);
        Assert.Equal("Headphones (Studio)", card.Vm.OutputName);
        Assert.True(card.Vm.ShowOutputPicker);
        Assert.False(card.Vm.ShowOutputSettings);
        card.Service.DidNotReceive().SetDefaultOutputDevice(Arg.Any<string>());
    }

    [Fact]
    public async Task PickingAnotherDevice_MovesTheSound_SaysSo_AndReadsEverythingAgain()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);
        card.Service.SetDefaultOutputDevice(Arg.Any<string>()).Returns(true);

        card.Vm.SelectedOutput = Speakers;
        Assert.NotNull(card.Vm.OutputSwitch);
        await card.Vm.OutputSwitch;

        card.Service.Received(1).SetDefaultOutputDevice(Speakers.Id);
        Assert.Equal(["Sound now plays through Speakers (Desk)."], card.Reports);
        Assert.Equal("Speakers (Desk)", card.Vm.OutputName);
        Assert.Equal(1, card.Refreshes);
    }

    [Fact]
    public async Task ASwitchWindowsRefused_IsReported_AndTheDevicesAreReadAgainAnyway()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);
        card.Service.SetDefaultOutputDevice(Arg.Any<string>()).Returns(false);

        card.Vm.SelectedOutput = Speakers;
        Assert.NotNull(card.Vm.OutputSwitch);
        await card.Vm.OutputSwitch;

        Assert.Equal(["Could not move the sound to Speakers (Desk) — Windows refused the change."], card.Reports);
        Assert.Equal("Headphones (Studio)", card.Vm.OutputName);
        Assert.Equal(1, card.Refreshes);
    }

    [Fact]
    public void PickingTheDeviceInUse_SwitchesNothing()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);

        // The picker drops its selection while the list is replaced, then lands on the device in use again.
        card.Vm.SelectedOutput = null;
        card.Vm.SelectedOutput = Headphones with { };

        Assert.Null(card.Vm.OutputSwitch);
        card.Service.DidNotReceive().SetDefaultOutputDevice(Arg.Any<string>());
        Assert.Equal(0, card.Refreshes);
    }

    [Fact]
    public async Task TheDeviceJustMovedTo_IsNotSwitchedToTwice()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);
        card.Service.SetDefaultOutputDevice(Arg.Any<string>()).Returns(true);
        card.Vm.SelectedOutput = Speakers;
        Assert.NotNull(card.Vm.OutputSwitch);
        await card.Vm.OutputSwitch;

        card.Vm.SelectedOutput = null;
        card.Vm.SelectedOutput = Speakers with { IsDefault = true };

        card.Service.Received(1).SetDefaultOutputDevice(Arg.Any<string>());
    }

    [Fact]
    public void WhereWindowsDoesNotLetSysManagerSwitch_TheDeviceIsNamed_AndPickingDoesNothing()
    {
        var card = new Card(canSwitch: false, Headphones, Speakers);

        Assert.False(card.Vm.ShowOutputPicker);
        Assert.True(card.Vm.ShowOutputSettings);
        Assert.Equal("Headphones (Studio)", card.Vm.OutputName);

        card.Vm.SelectedOutput = Speakers;

        Assert.Null(card.Vm.OutputSwitch);
        card.Service.DidNotReceive().SetDefaultOutputDevice(Arg.Any<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithNoDevice_NeitherThePickerNorWindowsSettingsShow(bool canSwitch)
    {
        var card = new Card(canSwitch);

        Assert.False(card.Vm.HasOutput);
        Assert.False(card.Vm.ShowOutputPicker);
        Assert.False(card.Vm.ShowOutputSettings);
        Assert.Null(card.Vm.SelectedOutput);
    }

    [Fact]
    public void AChangeMadeOutsideSysManager_ReachesThePicker_WithTheNextDeviceList()
    {
        var card = new Card(canSwitch: true, Headphones, Speakers);

        card.Devices.ReplaceWith([Headphones with { IsDefault = false }, Speakers with { IsDefault = true }]);
        card.Vm.SetOutputFromService();

        Assert.Equal(Speakers.Id, card.Vm.SelectedOutput?.Id);
        Assert.Equal("Speakers (Desk)", card.Vm.OutputName);
        card.Service.DidNotReceive().SetDefaultOutputDevice(Arg.Any<string>());
    }

    [Fact]
    public async Task AMove_IsRecordedInTheActivityHistory()
    {
        using var log = new ActivityLogScope();
        var card = new Card(canSwitch: true, Headphones, Speakers);
        card.Service.SetDefaultOutputDevice(Arg.Any<string>()).Returns(true);

        card.Vm.SelectedOutput = Speakers;
        Assert.NotNull(card.Vm.OutputSwitch);
        await card.Vm.OutputSwitch;

        var entry = Assert.Single(ActivityLogService.Instance.GetRecent(10));
        Assert.Equal("Volume", entry.Action);
        Assert.Equal("Sound now plays through Speakers (Desk)", entry.Detail);
    }

    [Fact]
    public async Task ARefusedMove_IsNotRecordedInTheActivityHistory()
    {
        using var log = new ActivityLogScope();
        var card = new Card(canSwitch: true, Headphones, Speakers);
        card.Service.SetDefaultOutputDevice(Arg.Any<string>()).Returns(false);

        card.Vm.SelectedOutput = Speakers;
        Assert.NotNull(card.Vm.OutputSwitch);
        await card.Vm.OutputSwitch;

        Assert.Empty(ActivityLogService.Instance.GetRecent(10));
    }
}
