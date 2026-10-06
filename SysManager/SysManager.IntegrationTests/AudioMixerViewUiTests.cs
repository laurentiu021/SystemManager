// SysManager · AudioMixerViewUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// Volume Control really draws its This PC card (#1588): above the apps, with the whole PC's volume, and the
/// device all sound plays through.
/// </summary>
/// <remarks>
/// The unit tests prove the view models. They cannot prove the view: a card bound to the wrong context draws
/// empty, a picker that misses its display member draws a record's <c>ToString()</c>, and a slider outside the drag
/// handlers' reach is moved by every refresh. So this loads the real XAML on the STA thread with the app's
/// dictionaries, the way <see cref="PrivacyViewUiTests"/> does. The audio service is a fake written here: nothing
/// reaches Core Audio, so this machine's volume and output device are never touched.
/// </remarks>
public sealed class AudioMixerViewUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SysManagerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { /* nothing was written */ }
    }

    private static readonly AudioDevice Headphones =
        new("{0.0.0.00000000}.{headphones}", "Headphones (Studio)", IsDefault: true, AudioDeviceKind.Headphones);

    private static readonly AudioDevice Speakers =
        new("{0.0.0.00000000}.{speakers}", "Speakers (Desk)", IsDefault: false, AudioDeviceKind.Speakers);

    private static readonly AudioSessionInfo Spotify =
        new("spotify", 4200, "Spotify", ExePath: "", Volume: 0.72f, IsMuted: true, AudioSessionState.Active,
            IsSystemSounds: false, PeakLevel: 0f);

    /// <summary>An audio service that answers from fields and changes nothing anywhere.</summary>
    private sealed class FakeAudio : IAudioMixerService
    {
        public List<AudioSessionInfo> Sessions { get; } = [Spotify];
        public List<AudioDevice> Devices { get; } = [Headphones, Speakers];
        public PcVolumeInfo? Pc { get; set; } = new(0.38f, IsMuted: false);
        public bool CanSwitch { get; set; } = true;

        public IReadOnlyList<AudioSessionInfo> GetSessions() => Sessions;
        public bool SetVolume(string sessionId, float level) => true;
        public bool SetMute(string sessionId, bool muted) => true;
        public float GetPeak(string sessionId) => 0f;
        public IReadOnlyDictionary<string, float> GetPeaks(IEnumerable<string> sessionIds) =>
            sessionIds.ToDictionary(id => id, _ => 0f, StringComparer.Ordinal);
        public PcVolumeInfo? GetPcVolume() => Pc;
        public bool SetPcVolume(float level) => true;
        public bool SetPcMute(bool muted) => true;
        public float GetPcPeak() => 0f;
        public IReadOnlyList<AudioDevice> GetRenderDevices() => Devices;
        public bool IsDefaultOutputSwitchSupported => CanSwitch;
        public bool SetDefaultOutputDevice(string deviceId) => false;
        public bool IsRoutingSupported => false;
        public string? GetSessionOutputDevice(string sessionId) => null;
        public bool SetSessionOutputDevice(string sessionId, string deviceId) => false;
    }

    /// <summary>Built off the STA thread; the tab is never activated, so both loops stay parked.</summary>
    private async Task<AudioMixerViewModel> VmAsync(FakeAudio audio)
    {
        var vm = new AudioMixerViewModel(audio, new VolumePresetService(_dir));
        await vm.InitializationComplete;
        return vm;
    }

    private static AudioMixerView Laid(AudioMixerViewModel vm)
    {
        AppResources.Ensure();
        var view = new AudioMixerView { DataContext = vm };
        view.Measure(new Size(1200, 1400));
        view.Arrange(new Rect(0, 0, 1200, 1400));
        view.UpdateLayout();
        return view;
    }

    [Fact]
    public async Task TheCard_SitsAboveTheApps_AndShowsTheWholePcsVolume()
    {
        using var vm = await VmAsync(new FakeAudio());

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var card = Named<Border>(view, "This PC");
            var apps = Named<ItemsControl>(view, "Application volume mixer");

            Assert.True(card.TranslatePoint(default, view).Y < apps.TranslatePoint(default, view).Y,
                "the This PC card is not above the app list");

            var shown = ShownTexts(card);
            Assert.Contains("This PC", shown);
            Assert.Contains("Applies to everything, on top of each app's own level", shown);
            Assert.Contains("All sound", shown);
            Assert.Contains("38%", shown);
            Assert.DoesNotContain("Spotify", shown);

            var slider = Named<Slider>(card, "Volume for all sound on this PC");
            Assert.Equal(0.38, slider.Value, precision: 5);
            Assert.True(Shown(slider));
            Assert.Same(vm.Pc.ToggleMuteCommand, Named<Button>(card, "Mute all sound on this PC").Command);
        });
    }

    [Fact]
    public async Task ThePicker_ShowsTheDeviceInUse_OnOneLine_AndEachDeviceInTheList_OnTwo()
    {
        using var vm = await VmAsync(new FakeAudio());

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var picker = Named<ComboBox>(view, "Sound plays through");

            Assert.True(Shown(picker));
            Assert.Equal(Headphones, picker.SelectedItem);
            Assert.Equal(["Headphones (Studio)"], ShownTexts(picker));

            // The open list's items, drawn through the same container style the picker gives them.
            Assert.Equal(["Headphones (Studio)", "Headphones · in use now"], ItemTexts(picker, Headphones));
            Assert.Equal(["Speakers (Desk)", "Speakers"], ItemTexts(picker, Speakers));
        });
    }

    [Fact]
    public async Task WhereSysManagerCannotSwitch_TheCardNamesTheDevice_AndOffersWindowsSettings()
    {
        using var vm = await VmAsync(new FakeAudio { CanSwitch = false });

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var card = Named<Border>(view, "This PC");

            Assert.False(Shown(Named<ComboBox>(card, "Sound plays through")));
            var shown = ShownTexts(card);
            Assert.Contains("Headphones (Studio)", shown);
            Assert.Contains("Change in Windows sound settings…", shown);
            Assert.Same(vm.Pc.OpenOutputSettingsCommand,
                Named<Button>(card, "Change the device this PC plays sound through, in Windows sound settings").Command);
        });
    }

    [Fact]
    public async Task WithNoDevice_TheCardSaysSo_InPlaceOfTheSlider()
    {
        var audio = new FakeAudio { Pc = null };
        audio.Devices.Clear();
        using var vm = await VmAsync(audio);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var card = Named<Border>(view, "This PC");

            Assert.False(Shown(Named<Slider>(card, "Volume for all sound on this PC")));
            Assert.Contains("No speakers or headphones were found. Connect some and they appear here.", ShownTexts(card));
            Assert.False(Shown(Named<ComboBox>(card, "Sound plays through")));
            Assert.DoesNotContain("Change in Windows sound settings…", ShownTexts(card));
        });
    }

    [Fact]
    public async Task DraggingTheCardsSlider_HoldsItAgainstARefresh()
    {
        using var vm = await VmAsync(new FakeAudio());

        StaHelper.Run(() =>
        {
            var view = Laid(vm);
            var slider = Named<Slider>(view, "Volume for all sound on this PC");

            slider.RaiseEvent(new DragStartedEventArgs(0, 0));
            Assert.True(vm.Pc.IsUserAdjusting);

            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: false));
            Assert.False(vm.Pc.IsUserAdjusting);
        });
    }

    [Fact]
    public async Task TheMuteGlyph_FollowsTheCard_AndEachRow()
    {
        var audio = new FakeAudio { Pc = new PcVolumeInfo(0.38f, IsMuted: true) };
        using var vm = await VmAsync(audio);

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            // The struck-through speaker, drawn by the one shared style, on the card and on Spotify's muted row.
            Assert.Equal("\uE74F", Glyph(Named<Button>(view, "Mute all sound on this PC")));
            Assert.Equal("\uE74F", Glyph(Named<Button>(view, "Mute Spotify")));
        });
    }

    [Fact]
    public async Task ThePresetsBox_ShowsThePresetsName_OnceOneIsPicked()
    {
        using var vm = await VmAsync(new FakeAudio());
        vm.Presets.ReplaceWith([new VolumePreset("Evening", [])]);
        vm.SelectedPreset = vm.Presets[0];

        StaHelper.Run(() =>
        {
            var view = Laid(vm);

            Assert.Equal(["Evening"], ShownTexts(Named<ComboBox>(view, "Saved volume presets")));
        });
    }

    [Fact]
    public async Task TheStatusLine_NamesWhereTheSoundGoes()
    {
        using var vm = await VmAsync(new FakeAudio());

        Assert.Equal("1 app playing audio · output: Headphones (Studio)", vm.StatusMessage);
    }

    /// <summary>The texts one list item draws, laid out through the picker's own container style.</summary>
    private static List<string> ItemTexts(ComboBox picker, AudioDevice device)
    {
        var item = new ComboBoxItem { Style = picker.ItemContainerStyle, Content = device };
        var host = new Border { Child = item };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        host.UpdateLayout();
        return ShownTexts(item);
    }

    private static string Glyph(Button button) =>
        Assert.Single(Descendants<TextBlock>(button)).Text;

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants<T>(root), e => AutomationProperties.GetName(e) == name);

    /// <summary>
    /// Whether nothing between <paramref name="element"/> and the view is collapsed. <c>IsVisible</c> cannot say: it
    /// is false for anything not shown in a window, and these views are laid out without one.
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    /// <summary>The text actually on screen under <paramref name="root"/>, in tree order, leaving out icon glyphs.</summary>
    private static List<string> ShownTexts(DependencyObject root)
    {
        List<string> texts = [];
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is TextBlock { Text.Length: > 0 } block && !IsGlyph(block.Text)) texts.Add(block.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return texts;
    }

    private static bool IsGlyph(string text) => text.Length == 1 && text[0] is >= '\uE000' and <= '\uF8FF';

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
