// SysManager · ThemePopupUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Controls;
using SysManager.Services;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The Appearance panel's mode pills say which mode the theme is in after a preset is picked (#2599).
/// </summary>
/// <remarks>
/// Picking a preset pins the preset's own mode, Dark or Light, so a pick made under Auto leaves Auto. The pills were set
/// once, when the panel opened, so Auto stayed selected while the theme no longer followed Windows. The theme service
/// here keeps its file in a folder of its own and does not repaint the shared Application, so no later test in this
/// process reads the colours this one picked.
/// </remarks>
public sealed class ThemePopupUiTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ThemePopup_");

    public void Dispose() => _dir.Delete(recursive: true);

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

            var card = popup.PresetPanel.Children.OfType<Border>().First(c => (string)c.Tag != theme.CurrentPresetId);
            popup.SelectPreset(card);

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
