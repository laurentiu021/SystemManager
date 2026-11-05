// SysManager · ThemePopupUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysManager.Services;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Appearance panel's mode pills say which mode the theme is in after a preset is picked (#2599), and each preset
/// card is a radio button a screen reader can find, read and pick (#2785).
/// </summary>
/// <remarks>
/// Picking a preset pins the preset's own mode, Dark or Light, so a pick made under Auto leaves Auto. The pills were set
/// once, when the panel opened, so Auto stayed selected while the theme no longer followed Windows. The theme service
/// here keeps its file in a folder of its own and does not repaint the shared Application, so no later test in this
/// process reads the colours this one picked.
/// <para>The cards were focusable Borders, which have no automation peer, so a screen reader could neither name nor
/// pick one and heard nothing of which was chosen. Keys are pressed here through a stand-in input source: a key event
/// needs one, and a panel that is never shown has none of its own.</para>
/// </remarks>
public sealed class ThemePopupUiTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ThemePopup_");

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>The source a key event names. Nothing is drawn through it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }

    private ThemeService DarkTheme(string presetId)
    {
        var theme = new ThemeService(_dir.FullName, windowsPrefersDark: () => true, paintsTheApp: false);
        theme.SetPreset(presetId);
        return theme;
    }

    private static ThemePopup Opened(ThemeService theme)
    {
        var popup = new ThemePopup(theme);
        popup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return popup;
    }

    private static List<RadioButton> Cards(ThemePopup popup) => [.. popup.PresetPanel.Children.OfType<RadioButton>()];

    private static ISelectionItemProvider Selection(RadioButton card) =>
        (ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(card)
            .GetPattern(PatternInterface.SelectionItem);

    /// <summary>Presses and releases <paramref name="key"/> on <paramref name="card"/>, as the focused card.</summary>
    private static void Press(RadioButton card, Key key)
    {
        var source = new KeySource();
        foreach (var routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            card.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routed });
    }

    [Fact]
    public void EachPresetCard_IsARadioButtonNamedForItsPreset_WithOnlyTheThemeInUseSelected()
    {
        var theme = DarkTheme("deep-ocean");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var popup = Opened(theme);
            var cards = Cards(popup);

            Assert.Equal(popup.PresetPanel.Children.Count, cards.Count);
            Assert.Equal(ThemePreset.Defaults.Values.Count(p => p.IsDark), cards.Count);
            foreach (var card in cards)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(card);
                Assert.IsType<RadioButtonAutomationPeer>(peer);
                Assert.Equal($"{ThemePreset.Defaults[(string)card.Tag].Name} theme", peer.GetName());
                Assert.True(card.Focusable && card.IsTabStop, $"Tab cannot reach the {peer.GetName()} card");
            }
            Assert.Equal("deep-ocean", (string)Assert.Single(cards, c => Selection(c).IsSelected).Tag);
        });
    }

    [Fact]
    public void AScreenReaderSelectingACard_PicksItsPreset_AndTheSelectionAndAccentMoveToIt()
    {
        var theme = DarkTheme("deep-ocean");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var popup = Opened(theme);
            var cards = Cards(popup);
            var pick = cards.First(c => (string)c.Tag == "warm-ember");

            Selection(pick).Select();

            Assert.Equal("warm-ember", theme.CurrentPresetId);
            Assert.Same(pick, Assert.Single(cards, c => Selection(c).IsSelected));
            var accent = popup.TryFindResource("Accent");
            Assert.NotNull(accent);
            Assert.All(cards, c => Assert.Equal(c == pick, ReferenceEquals(accent, ((Border)c.Content).BorderBrush)));
        });
    }

    [Fact]
    public void EnterAndSpace_EachPickACard()
    {
        var theme = DarkTheme("deep-ocean");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var cards = Cards(Opened(theme));

            Press(cards.First(c => (string)c.Tag == "warm-ember"), Key.Enter);
            Assert.Equal("warm-ember", theme.CurrentPresetId);

            Press(cards.First(c => (string)c.Tag == "dark-forest"), Key.Space);
            Assert.Equal("dark-forest", theme.CurrentPresetId);
        });
    }

    [Fact]
    public void PressingTheCheckedCardUnderAuto_StillPinsItsMode()
    {
        // Under Auto the checked card is the preset Windows' side resolved to. Picking it pins it, as picking any other
        // does (#2599), though checking a checked card raises no Checked.
        var theme = new ThemeService(_dir.FullName, windowsPrefersDark: () => true, paintsTheApp: false);
        theme.FollowWindows();

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var popup = Opened(theme);
            var resolved = theme.CurrentPresetId;

            Press(Assert.Single(Cards(popup), c => c.IsChecked == true), Key.Enter);

            Assert.Equal("dark", theme.CurrentMode);
            Assert.Equal(resolved, theme.CurrentPresetId);
            Assert.True(popup.DarkMode.IsChecked, "Dark is not selected after the resolved preset was picked");
        });
    }

    [Fact]
    public void PressingTheCheckedCard_PicksItAgainAfterTheThemeMovedOn()
    {
        // The cards are built when the panel opens, and a theme that changes without them (Windows switching sides
        // under Auto) leaves the old card checked. Pressing it must still apply it, as clicking the Border it was did.
        var theme = DarkTheme("deep-ocean");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var cards = Cards(Opened(theme));
            theme.SetPreset("neon-rose");

            Press(cards.First(c => (string)c.Tag == "deep-ocean"), Key.Space);

            Assert.Equal("deep-ocean", theme.CurrentPresetId);
        });
    }

    [Fact]
    public void PickingAPresetUnderAuto_SelectsThePillOfThePresetsMode_AndKeepsThePick()
    {
        var theme = new ThemeService(_dir.FullName, windowsPrefersDark: () => true, paintsTheApp: false);
        theme.FollowWindows();

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var popup = new ThemePopup(theme);
            popup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(popup.FollowWindowsMode.IsChecked, "Auto is not selected when the panel opens under Auto");

            var card = popup.PresetPanel.Children.OfType<RadioButton>()
                .First(c => (string)c.Tag != theme.CurrentPresetId);
            popup.SelectPreset(card);

            Assert.True(card.IsChecked, "the picked card is not the checked one");
            Assert.Equal("dark", theme.CurrentMode);
            Assert.Equal((string)card.Tag, theme.CurrentPresetId);
            Assert.True(popup.DarkMode.IsChecked, "Dark is not selected after a dark preset was picked");
            Assert.False(popup.FollowWindowsMode.IsChecked, "Auto is still selected after a preset was picked");
        });
    }

    [Fact]
    public void ClickingAPill_StillAppliesItsMode()
    {
        // The other direction is unchanged: a pill the user clicks applies the mode's companion preset.
        var theme = new ThemeService(_dir.FullName, windowsPrefersDark: () => true, paintsTheApp: false);
        theme.SetPreset("midnight-indigo");

        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var popup = new ThemePopup(theme);
            popup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            popup.LightMode.IsChecked = true;

            Assert.Equal("light", theme.CurrentMode);
            Assert.False(theme.CurrentTheme.IsDark);
        });
    }
}
