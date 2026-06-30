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
    private bool _suppressShadeEvent;
    private bool _isApplyingShade;
    private bool _initialized;

    public ThemePopup()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        BuildPresetCards();
        SyncUiToService();

        DarkMode.Checked += Mode_Changed;
        LightMode.Checked += Mode_Changed;
        CustomMode.Checked += Mode_Changed;
        ShadeSlider.ValueChanged += Shade_Changed;
    }


    private void BuildPresetCards()
    {
        PresetPanel.Children.Clear();
        var mode = DarkMode.IsChecked == true ? "dark" : "light";
        foreach (var (id, preset) in ThemePreset.Defaults)
        {
            if (preset.IsDark != (mode == "dark")) continue;
            var card = CreatePresetCard(id, preset);
            PresetPanel.Children.Add(card);
        }
    }

    private Border CreatePresetCard(string id, ThemePreset preset)
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

        var textBrush = TryFindResource("TextPrimary") as Brush ?? new SolidColorBrush(Colors.White);
        var borderBrush = TryFindResource("Border1") as Brush ?? new SolidColorBrush(Colors.DarkGray);
        var accentBrush = TryFindResource("Accent") as Brush ?? new SolidColorBrush(Colors.Purple);

        var name = new TextBlock
        {
            Text = preset.Name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(mini);
        content.Children.Add(new Border { Width = 10 });
        content.Children.Add(name);

        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeService.Instance.CurrentPresetId == id ? accentBrush : borderBrush,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 8),
            Width = 140,
            Tag = id,
            Child = content,
            // Keyboard accessibility: make the card Tab-reachable, activatable with
            // Enter/Space, and announced by its preset name to assistive technology.
            Focusable = true
        };
        AutomationProperties.SetName(card, $"{preset.Name} theme");
        KeyboardNavigation.SetIsTabStop(card, true);

        card.MouseLeftButtonUp += Preset_Click;
        card.KeyDown += Preset_KeyDown;
        return card;
    }

    private void SyncUiToService()
    {
        var svc = ThemeService.Instance;

        _suppressShadeEvent = true;
        ShadeSlider.Value = svc.ShadePosition;
        _suppressShadeEvent = false;

        switch (svc.CurrentMode)
        {
            case "light": LightMode.IsChecked = true; break;
            case "custom": CustomMode.IsChecked = true; break;
            default: DarkMode.IsChecked = true; break;
        }
    }


    private void Preset_Click(object sender, MouseButtonEventArgs e)
        => SelectPreset((Border)sender);

    private void Preset_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter/Space activate the focused preset card, matching standard button semantics.
        if (e.Key is Key.Enter or Key.Space)
        {
            SelectPreset((Border)sender);
            e.Handled = true;
        }
    }

    private void SelectPreset(Border card)
    {
        var id = (string)card.Tag;
        ThemeService.Instance.SetPreset(id);

        var borderBrush = TryFindResource("Border1") as Brush ?? Brushes.DarkGray;
        var accentBrush = TryFindResource("Accent") as Brush ?? Brushes.Purple;

        foreach (Border c in PresetPanel.Children)
            c.BorderBrush = borderBrush;
        card.BorderBrush = accentBrush;
    }

    private void Shade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressShadeEvent || _isApplyingShade) return;
        _isApplyingShade = true;
        try
        {
            ThemeService.Instance.SetShade(e.NewValue);
        }
        finally
        {
            _isApplyingShade = false;
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (CustomMode.IsChecked == true)
        {
            PresetsPanel.Visibility = Visibility.Collapsed;
            CustomPanel.Visibility = Visibility.Visible;
        }
        else
        {
            PresetsPanel.Visibility = Visibility.Visible;
            CustomPanel.Visibility = Visibility.Collapsed;

            var targetMode = LightMode.IsChecked == true ? "light" : "dark";
            var companion = ThemeService.Instance.GetCompanionPreset(targetMode);
            ThemeService.Instance.SetPreset(companion);

            BuildPresetCards();
        }
    }

    private void CustomHex_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyCustomFromInputs();
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

        ThemeService.Instance.SetCustom(accent, bg, surface, text);
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
