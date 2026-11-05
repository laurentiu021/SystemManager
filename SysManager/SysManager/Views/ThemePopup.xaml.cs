// SysManager · ThemePopup
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SysManager.Services;

namespace SysManager.Views;

public partial class ThemePopup : UserControl
{
    private readonly ThemeService _theme;
    private bool _suppressShadeEvent;
    private bool _isApplyingShade;
    private bool _checkingModePill;
    private bool _initialized;

    public ThemePopup() : this(ThemeService.Instance)
    {
    }

    /// <summary>The popup over <paramref name="theme"/>, so a test can drive it without the app's own theme.</summary>
    internal ThemePopup(ThemeService theme)
    {
        _theme = theme;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Shows the theme as it is now, each time the panel opens.</summary>
    /// <remarks>
    /// The panel is built once and kept, and it read the theme only the first time it opened. Under Auto the theme
    /// changes while the panel is closed, when Windows switches between light and dark, so the panel opened again on
    /// the other side's presets with the old one checked, and picking one of them left Auto (#3163). The handlers are
    /// wired once; a later read checks a pill and moves the slider under the guards that keep either from applying
    /// anything.
    /// </remarks>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncUiToService();
        BuildPresetCards();

        if (_initialized) return;
        _initialized = true;

        DarkMode.Checked += Mode_Changed;
        LightMode.Checked += Mode_Changed;
        FollowWindowsMode.Checked += Mode_Changed;
        CustomMode.Checked += Mode_Changed;
        ShadeSlider.ValueChanged += Shade_Changed;
    }


    /// <summary>Lists the presets of whichever arm the theme is actually on.</summary>
    /// <remarks>
    /// Keyed on the SERVICE's resolved theme rather than on <c>DarkMode.IsChecked</c>. In Auto mode neither the
    /// Dark nor the Light radio is checked, so the old expression fell through to "light" and listed the light
    /// presets on a machine Windows had on dark. Reading the service is also what makes the Loaded ordering
    /// stop mattering: it is the truth in every mode, where the radios are a view of it.
    /// </remarks>
    private void BuildPresetCards()
    {
        PresetPanel.Children.Clear();
        var isDark = _theme.CurrentTheme.IsDark;
        foreach (var (id, preset) in ThemePreset.Defaults)
        {
            if (preset.IsDark != isDark) continue;
            var card = CreatePresetCard(id, preset);
            PresetPanel.Children.Add(card);
        }
    }

    private RadioButton CreatePresetCard(string id, ThemePreset preset)
    {
        var mini = new StackPanel { Orientation = Orientation.Horizontal };
        mini.Children.Add(new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(preset.Background),
            Margin = new Thickness(0, 0, 3, 0)
        });
        mini.Children.Add(new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(preset.Accent),
            Margin = new Thickness(0, 0, 3, 0)
        });
        mini.Children.Add(new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(preset.TextPrimary)
        });

        var name = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        // References to the theme's brushes, not the brushes themselves, which a theme switch replaces (#2818).
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(mini);
        content.Children.Add(new Border { Width = 10 });
        content.Children.Add(name);

        var face = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Width = 140,
            Child = content,
        };
        face.SetResourceReference(Border.BorderBrushProperty, _theme.CurrentPresetId == id ? "Accent" : "Border1");

        // The card itself is a RadioButton around that face: one of a group, so which preset is chosen is a state a
        // screen reader reads, not only an accent border (#2785). A RadioButton takes Space and not Enter, where a
        // Button takes both, so Enter is turned on to pick it as the Border's own key handler did. The focus rectangle
        // is the one the Border was drawn with.
        var card = new RadioButton
        {
            GroupName = "ThemePresets",
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 8),
            Tag = id,
            Content = face,
            IsChecked = _theme.CurrentPresetId == id,
        };
        card.SetResourceReference(Control.TemplateProperty, "PresetCardChrome");
        card.SetResourceReference(FocusVisualStyleProperty, SystemParameters.FocusVisualStyleKey);
        KeyboardNavigation.SetAcceptsReturn(card, true);
        AutomationProperties.SetName(card, $"{preset.Name} theme");

        card.Checked += Preset_Checked;
        card.Click += Preset_Click;
        return card;
    }

    private void SyncUiToService()
    {
        var svc = _theme;

        _suppressShadeEvent = true;
        ShadeSlider.Value = svc.ShadePosition;
        _suppressShadeEvent = false;

        CheckModePill();

        // The Mode_Changed handler (which flips panel visibility) is only wired up AFTER this
        // runs, and clicking an already-checked radio doesn't re-raise Checked — so for a
        // persisted CUSTOM theme we must set the panel visibility and seed the hex fields here,
        // or the popup opens on the Presets list with the XAML-default hex literals. Without the
        // seed, editing one hex box would call SetCustom() with the defaults for the other three,
        // silently destroying the saved custom colours on the next save.
        UpdatePanels();
        if (svc.CurrentMode == "custom")
            PopulateCustomFields(svc.CurrentTheme);
    }

    /// <summary>Checks the mode pill for the mode the theme is in, without the pill applying a preset of its own.</summary>
    /// <remarks>
    /// Checking a pill raises <see cref="Mode_Changed"/>, which applies the mode's companion preset. That is right when
    /// the user clicks the pill and wrong when the pill only follows a change already made: after a preset is
    /// picked it would replace the pick.
    /// </remarks>
    private void CheckModePill()
    {
        _checkingModePill = true;
        try
        {
            switch (_theme.CurrentMode)
            {
                case "light": LightMode.IsChecked = true; break;
                case "custom": CustomMode.IsChecked = true; break;
                case ThemeService.AutoMode: FollowWindowsMode.IsChecked = true; break;
                default: DarkMode.IsChecked = true; break;
            }
        }
        finally
        {
            _checkingModePill = false;
        }
    }

    /// <summary>Shows the Custom editors or the Presets list to match the checked mode radio.</summary>
    private void UpdatePanels()
    {
        bool custom = CustomMode.IsChecked == true;
        PresetsPanel.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Seeds the four custom hex boxes + preview swatches from a persisted theme so a reopen shows
    /// the real saved colours (not the XAML defaults) and a single-field edit doesn't reset the rest.
    /// </summary>
    private void PopulateCustomFields(ThemePreset theme)
    {
        SetHexField(CustomAccentHex, CustomAccentPreview, theme.Accent);
        SetHexField(CustomBgHex, CustomBgPreview, theme.Background);
        SetHexField(CustomSurfaceHex, CustomSurfacePreview, theme.Surface);
        SetHexField(CustomTextHex, CustomTextPreview, theme.TextPrimary);
    }

    private static void SetHexField(TextBox box, Border swatch, Color color)
    {
        box.Text = color.ToString();
        swatch.Background = new SolidColorBrush(color);
    }


    // An unchecked card is picked by checking it: a click, Space, Enter, or a screen reader selecting it.
    private void Preset_Checked(object sender, RoutedEventArgs e) => SelectPreset((RadioButton)sender);

    /// <summary>A click, Space or Enter on the card that is already checked, which raises no Checked.</summary>
    /// <remarks>
    /// The checked card is not always the theme as it is pinned now. Under Auto it is the preset Windows' side resolved
    /// to, and picking it pins it, as picking any other does (#2599). And when Windows switches between light and dark
    /// under Auto, the theme moves on without these cards being rebuilt. As a Border, a card applied its preset on
    /// every click, so this keeps that. After a click whose Checked has just applied the card, there is nothing to do.
    /// </remarks>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var card = (RadioButton)sender;
        if ((string)card.Tag != _theme.CurrentPresetId || _theme.CurrentMode == ThemeService.AutoMode)
            SelectPreset(card);
    }

    /// <summary>Applies the preset <paramref name="card"/> stands for and marks it as the chosen one.</summary>
    /// <remarks>
    /// Checking the card raises <see cref="Preset_Checked"/>, which comes back here with it checked, so a pick made
    /// from code goes the way a click does. The group has unchecked the others by then.
    /// </remarks>
    internal void SelectPreset(RadioButton card)
    {
        if (card.IsChecked != true)
        {
            card.IsChecked = true;
            return;
        }

        var id = (string)card.Tag;
        _theme.SetPreset(id);
        // A preset pins its own mode, Dark or Light, so a pick made under Auto leaves Auto (#2599).
        CheckModePill();

        foreach (var c in PresetPanel.Children.OfType<RadioButton>())
            ((Border)c.Content).SetResourceReference(
                Border.BorderBrushProperty, c.IsChecked == true ? "Accent" : "Border1");
    }

    private void Shade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressShadeEvent || _isApplyingShade) return;
        _isApplyingShade = true;
        try
        {
            _theme.SetShade(e.NewValue);
        }
        finally
        {
            _isApplyingShade = false;
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_checkingModePill) return;
        UpdatePanels();
        if (CustomMode.IsChecked == true) return;

        // Auto resolves the arm from Windows and STAYS on auto; the other two pin one. Both end up applying a
        // companion preset, so the user's chosen family survives the switch either way.
        if (FollowWindowsMode.IsChecked == true)
            _theme.FollowWindows();
        else
            _theme.SetPreset(
                _theme.GetCompanionPreset(LightMode.IsChecked == true ? "light" : "dark"));

        BuildPresetCards();
    }

    private void CustomHex_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyCustomFromInputs();
    }

    /// <summary>
    /// Puts the shipped theme back and re-syncs the popup to it.
    /// </summary>
    /// <remarks>
    /// No confirmation dialog. The user reaching for this is most likely looking at a theme they cannot read,
    /// so a dialog would be one more unreadable thing between them and the fix — and the action is not
    /// destructive: it changes four colours and a slider, all of which they can set again.
    /// <para>The re-sync has to be explicit. <see cref="SyncUiToService"/> only runs once, on Loaded, and the
    /// mode radios and shade slider hold their own state — without this the popup would keep showing "Custom"
    /// and the old slider position over a theme that is no longer either.</para>
    /// </remarks>
    private void ResetTheme_Click(object sender, RoutedEventArgs e)
    {
        // The radio FIRST, so Mode_Changed's own SetPreset happens before the reset rather than after it.
        // Checking it from Custom raises Checked, which applies the companion preset — harmless in itself, but
        // it must not be the last write.
        DarkMode.IsChecked = true;
        _theme.ResetToDefault();

        // Suppressed, or putting the slider back would raise Shade_Changed and save a shade position on top
        // of the one the reset just restored.
        _suppressShadeEvent = true;
        ShadeSlider.Value = ThemeService.DefaultShade;
        _suppressShadeEvent = false;

        UpdatePanels();
        // Seed the custom boxes from the default too, so switching to Custom after a reset starts from a
        // readable theme rather than from the colours that made the reset necessary.
        PopulateCustomFields(_theme.CurrentTheme);
        BuildPresetCards();
    }

    private void CustomAccent_Click(object sender, MouseButtonEventArgs e) => FocusHex(CustomAccentHex);
    private void CustomBg_Click(object sender, MouseButtonEventArgs e) => FocusHex(CustomBgHex);
    private void CustomSurface_Click(object sender, MouseButtonEventArgs e) => FocusHex(CustomSurfaceHex);
    private void CustomText_Click(object sender, MouseButtonEventArgs e) => FocusHex(CustomTextHex);

    private static void FocusHex(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private void ApplyCustomFromInputs()
    {
        // Parse each field independently so one invalid hex doesn't discard the
        // other three valid edits. An unparseable field keeps its last-good colour
        // (from its preview swatch) and is flagged visibly instead of failing silently.
        var accent = ResolveHex(CustomAccentHex, CustomAccentPreview);
        var bg = ResolveHex(CustomBgHex, CustomBgPreview);
        var surface = ResolveHex(CustomSurfaceHex, CustomSurfacePreview);
        var text = ResolveHex(CustomTextHex, CustomTextPreview);

        CustomAccentPreview.Background = new SolidColorBrush(accent);
        CustomBgPreview.Background = new SolidColorBrush(bg);
        CustomSurfacePreview.Background = new SolidColorBrush(surface);
        CustomTextPreview.Background = new SolidColorBrush(text);

        _theme.SetCustom(accent, bg, surface, text);
    }

    /// <summary>
    /// Parses a hex TextBox; on success clears the invalid flag and returns the colour,
    /// on failure flags the box (red text + tooltip) and returns the swatch's current colour.
    /// </summary>
    private static Color ResolveHex(TextBox box, Border swatch)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(box.Text);
            box.ClearValue(ForegroundProperty);
            box.ClearValue(ToolTipProperty);
            return color;
        }
        catch (FormatException ex)
        {
            Serilog.Log.Debug(ex, "Theme custom hex parse failed for {Field}", box.Tag);
            box.Foreground = Brushes.IndianRed;
            box.ToolTip = "Invalid colour — use a hex value like #1E2530.";
            return (swatch.Background as SolidColorBrush)?.Color ?? Colors.Gray;
        }
    }
}
