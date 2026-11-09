// SysManager · TemplateLabelColourTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// A button's, chip's, checkbox's, column header's or menu item's label takes the colour its template gives it, and
/// that colour reads at 4.5:1 on what the label sits on (#2926).
/// </summary>
/// <remarks>
/// App.xaml's keyless TextBlock style sets <c>Foreground</c>, and WPF applies it to the TextBlock a
/// <c>ContentPresenter</c> makes for string content too. A style setter outranks an inherited value, so the colour a
/// template passed down never reached the label: a primary button's label was the main text colour where it should
/// be the colour picked for the accent fill, 1.93:1 on Warm Ember, and the danger, admin and chip colours never
/// showed. Both checks lay out every App.xaml style for a button-like control or a menu item, keyed or implicit, on
/// every built-in theme, and put the app's resources back afterwards, as the theme-switch tests do.
/// </remarks>
public sealed partial class TemplateLabelColourTests
{
    private const string Label = "Label text";

    /// <summary>
    /// The styles whose templates draw no content, so neither check has a label to read: the toggle switch is a track
    /// and a thumb, and the ComboBox's toggle an arrow. Exact, so a style that stops drawing its label, or one the
    /// checks would skip unmeasured, fails them.
    /// </summary>
    private static readonly HashSet<string> NoLabel = ["ComboBoxToggleButton", "ToggleSwitch"];

    /// <summary>
    /// The styles that draw their label as a TextBlock of their own rather than through a presenter, so there is no
    /// presenter colour to compare it with: the theme popup's mode pills, whose checked trigger colours that TextBlock.
    /// The contrast check still measures them.
    /// </summary>
    private static readonly HashSet<string> NoPresenter = ["ModePill"];

    /// <summary>
    /// Every label takes the colour its template gives the presenter that draws it, given as a string or inside a
    /// panel, enabled or not.
    /// </summary>
    /// <remarks>
    /// Run on every theme because a template colour that equals the main text colour on one theme cannot show the
    /// defect there. The two kinds of content are reached two ways: the TextBlock a presenter makes for a string is
    /// styled from the presenter's resources, and a TextBlock given as content from the control's style, so the
    /// check covers both. Disabled as well, because a trigger that colours only the presenter leaves the panel's
    /// TextBlock behind.
    /// </remarks>
    [Fact]
    public void EveryLabel_TakesTheColourItsTemplateGivesIt()
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var unread = new SortedSet<string>(StringComparer.Ordinal);
        var unreadShapes = new SortedSet<string>(StringComparer.Ordinal);
        var read = 0;
        AcrossEveryPreset((styles, preset) =>
        {
            foreach (var (name, style) in styles)
            {
                var menu = typeof(MenuItem).IsAssignableFrom(style.TargetType);
                foreach (var kind in menu ? AllKinds : [Kind.Text, Kind.Panel])
                {
                    foreach (var enabled in new[] { true, false })
                    {
                        var (host, control, label) = Make(style, kind);
                        control.IsEnabled = enabled;
                        Lay(host);
                        var shape = kind switch
                        {
                            Kind.Panel => " in a panel",
                            Kind.AccessKey => " with an access key",
                            _ => "",
                        } + (enabled ? "" : ", disabled");
                        if (label() is not { } text
                            || Ancestors(text).OfType<ContentPresenter>().FirstOrDefault() is not { } presenter)
                        {
                            unread.Add(name);
                            unreadShapes.Add($"{name}{shape}{(label() is null ? "" : " (no presenter)")}");
                            continue;
                        }
                        read++;
                        var given = (TextElement.GetForeground(presenter) as SolidColorBrush)?.Color;
                        var shown = (text.Foreground as SolidColorBrush)?.Color;
                        if (given == shown) continue;
                        offenders.Add(
                            $"{name}{shape} on {preset.Id}: the template gives {given}, the label shows {shown}");
                    }
                }
            }
        });

        // Vacuity floor: 912 label layouts when measured, the 16 styles with a presenter in two kinds of content and
        // two states, and the two menu items, the plain one and the danger one, in three kinds, on twelve themes.
        Assert.True(read >= 912, $"only {read} labels were laid out across the themes, out of 912 measured.");
        var expected = NoLabel.Union(NoPresenter).ToHashSet(StringComparer.Ordinal);
        Assert.True(unread.SetEquals(expected),
            "the styles with a label this could not read through a presenter are not the ones NoLabel and NoPresenter "
            + $"list: read none for {string.Join(", ", unread.Except(expected))}; read one for "
            + $"{string.Join(", ", expected.Except(unread))}. Unread: {string.Join("; ", unreadShapes)}");
        Assert.True(offenders.Count == 0,
            "these labels do not take their template's colour:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every label reads at 4.5:1 on what it sits on, on every theme, at rest and checked, and so does every colour a
    /// view gives one of these controls itself.
    /// </summary>
    /// <remarks>
    /// The colours were chosen for the labels but never drawn on them, so nothing had measured them there. The
    /// background is every solid fill between the label and the control's host, laid over the page, a card and the
    /// top of a card's sheen in turn, and a fill that covers everything is measured once. An inner card is added for a
    /// style a view puts in one, or in a template or a resource whose place the markup does not show, or that no view
    /// names at all: the filter chips' tints over Soft Blossom's pink inner card would need darker words than any card
    /// of theirs asks for. The colours a view sets are the ones named in a <c>Foreground</c> attribute on a button,
    /// chip, checkbox or menu item in the views, such as Dashboard's blue "Go to" and "Fix this" ghost buttons.
    /// </remarks>
    [Fact]
    public void EveryLabel_ReadsAtAaOnWhatItSitsOn()
    {
        var overrides = ForegroundsTheViewsGive();
        // Vacuity floor: 3 style and colour pairs in the views when measured, the link buttons' InfoText and two
        // checkboxes'.
        Assert.True(overrides.Count >= 3, $"only {overrides.Count} colours given in the views were found, out of 3.");
        var (named, onInnerCards) = WhereTheViewsPutEachStyle();
        // Vacuity floor: 25 styles named on these controls in the views when measured.
        Assert.True(named.Count >= 25, $"only {named.Count} styles were found named in the views, out of 25.");

        var failing = new SortedSet<string>(StringComparer.Ordinal);
        var details = new List<string>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var unread = new SortedSet<string>(StringComparer.Ordinal);
        var measured = 0;
        AcrossEveryPreset((styles, preset) =>
        {
            var cards = new (string Name, Color Color)[]
            {
                ("the page", preset.Background), ("a card", preset.Surface),
                ("the top of a card", ThemeService.CardSheenTop(preset)),
            };
            var withInnerCard = cards.Append(("an inner card", preset.Surface2)).ToArray();
            unknown.UnionWith(overrides.Where(o => styles.All(s => s.Name != o.Name)).Select(o => o.Name));
            var cases = styles.Select(s => (s.Name, StyleName: s.Name, s.Style, Foreground: (string?)null))
                .Concat(overrides.Where(o => styles.Any(s => s.Name == o.Name))
                    .Select(o => (Name: $"{o.Name} with {o.Foreground}", StyleName: o.Name,
                                  styles.First(s => s.Name == o.Name).Style,
                                  Foreground: (string?)o.Foreground)));
            foreach (var (name, styleName, style, foreground) in cases)
            {
                var surfaces = onInnerCards.Contains(styleName) || !named.Contains(styleName) ? withInnerCard : cards;
                foreach (var isChecked in new[] { false, true })
                {
                    var (host, control, label) = Make(style, Kind.Text);
                    if (isChecked && control is not ToggleButton) continue;
                    if (control is ToggleButton toggle) toggle.IsChecked = isChecked;
                    if (foreground is not null) control.SetResourceReference(Control.ForegroundProperty, foreground);
                    Lay(host);
                    if (label() is not { } text)
                    {
                        unread.Add(styleName);
                        continue;
                    }

                    var key = $"{name}{(isChecked ? " checked" : "")} on {preset.Id}";
                    foreach (var (surface, under) in surfaces)
                    {
                        var (ink, paper, covered) = Drawn(text, host, under);
                        measured++;
                        var ratio = Contrast(ink, paper);
                        if (ratio < 4.5)
                        {
                            failing.Add(key);
                            var over = covered ? "" : " over " + surface;
                            details.Add($"{key}: {ink} on {paper}{over} is {ratio:F2}:1");
                        }
                        if (covered) break;
                    }
                }
            }
        });

        // Vacuity floor: 1,092 measurements when measured.
        Assert.True(measured >= 1092,
            $"only {measured} labels were measured across the themes and surfaces, out of 1,092 measured.");
        Assert.True(unknown.Count == 0,
            "a view gives its own colour to a control in a style App.xaml does not define, so this cannot lay it out: "
            + string.Join(", ", unknown));
        Assert.True(unread.SetEquals(NoLabel),
            "the styles whose label this could not read are not the ones NoLabel lists: read none for "
            + $"{string.Join(", ", unread.Except(NoLabel))}; read one for {string.Join(", ", NoLabel.Except(unread))}");
        Assert.True(failing.Count == 0, "these labels read under 4.5:1:\n  " + string.Join("\n  ", details));
    }

    /// <summary>
    /// A right-click menu is drawn in the theme's colours, and its words, greyed-out words and danger red read at
    /// 4.5:1 on it and on the highlight under the pointer, on every theme (#3198).
    /// </summary>
    /// <remarks>
    /// The menus were WPF-UI's, on its <c>#2C2C2C</c> whatever the theme, so on the light themes the theme's dark words
    /// sat on a dark grey at 1.07 to 1.53:1. The menu takes the dropdown lists' look now: the card surface and its
    /// border, the separators in the border colour and a lighter highlight. The highlight is read from the item
    /// template's own trigger, so a change of its colour is measured here rather than taken on trust.
    /// </remarks>
    [Fact]
    public void EveryMenu_IsDrawnInTheThemesColours_AndItsItemsRead()
    {
        var failing = new List<string>();
        var themes = 0;
        AcrossEveryPreset((_, preset) =>
        {
            themes++;
            var resources = Application.Current.Resources;
            Color Colour(string key) => ((SolidColorBrush)resources[key]).Color;

            var plain = new MenuItem { Header = Label };
            var off = new MenuItem { Header = Label, IsEnabled = false };
            var danger = new MenuItem { Header = Label, Style = (Style)resources["DangerMenuItem"] };
            var line = new Separator();
            var menu = new ContextMenu();
            foreach (var item in new Control[] { off, line, plain, danger }) menu.Items.Add(item);
            Lay(menu);

            var face = Descendants<Border>(menu).First();
            Assert.Equal(Colour("Surface1"), ((SolidColorBrush)face.Background).Color);
            Assert.Equal(Colour("Border2"), ((SolidColorBrush)face.BorderBrush).Color);
            var rule = Assert.Single(Descendants<Border>(line));
            Assert.Equal(Colour("Border1"), ((SolidColorBrush)rule.Background).Color);

            var lit = plain.Template.Triggers.OfType<Trigger>()
                .Single(t => t.Property == MenuItem.IsHighlightedProperty)
                .Setters.OfType<Setter>().Single(s => s.TargetName == "Bd" && s.Property == Border.BackgroundProperty);
            var highlight = Colour((string)((DynamicResourceExtension)lit.Value).ResourceKey);

            foreach (var (name, item, key) in new[]
                     {
                         ("an item", plain, "TextPrimary"), ("a greyed-out item", off, "TextMuted"),
                         ("the danger item", danger, "DangerText"),
                     })
            {
                var text = Descendants<TextBlock>(item).First(Shows);
                var ink = ((SolidColorBrush)text.Foreground).Color;
                if (ink != Colour(key)) failing.Add($"{preset.Id}: {name} is {ink}, where {key} is {Colour(key)}");
                var papers = item.IsEnabled
                    ? new[] { ("the menu", Colour("Surface1")), ("the highlight", highlight) }
                    : [("the menu", Colour("Surface1"))];
                foreach (var (surface, paper) in papers)
                {
                    var ratio = Contrast(ink, paper);
                    if (ratio < 4.5) failing.Add($"{preset.Id}: {name} reads {ratio:F2}:1 on {surface}");
                }
            }
        });

        Assert.Equal(ThemePreset.Defaults.Count, themes);
        Assert.True(failing.Count == 0, "these menu items do not read on the theme's menu:\n  "
            + string.Join("\n  ", failing));
    }

    /// <summary>
    /// Runs <paramref name="check"/> once per built-in theme with every style it covers, and puts the app's resources
    /// back afterwards.
    /// </summary>
    private static void AcrossEveryPreset(Action<List<(string Name, Style Style)>, ThemePreset> check)
    {
        var dir = Directory.CreateTempSubdirectory("TemplateLabel_");
        try
        {
            StaHelper.Run(() =>
            {
                AppResources.Ensure();
                var resources = Application.Current.Resources;
                var saved = resources.Keys.Cast<object>().ToDictionary(key => key, key => resources[key]);
                try
                {
                    var styles = resources.Keys.Cast<object>()
                        .Select(key => (Key: key, Style: resources[key] as Style))
                        .Where(s => s.Style?.TargetType is { } t
                                    && (typeof(ButtonBase).IsAssignableFrom(t) || typeof(MenuItem).IsAssignableFrom(t)))
                        .Select(s => (Name: s.Key is Type type ? "implicit " + type.Name : s.Key.ToString()!, s.Style!))
                        .OrderBy(s => s.Name, StringComparer.Ordinal)
                        .ToList();
                    foreach (var preset in ThemePreset.Defaults.Values)
                    {
                        new ThemeService(dir.FullName, windowsPrefersDark: () => true).Apply(preset);
                        check(styles, preset);
                    }
                }
                finally
                {
                    foreach (var added in resources.Keys.Cast<object>().Where(key => !saved.ContainsKey(key)).ToList())
                        resources.Remove(added);
                    foreach (var (key, value) in saved)
                        if (!ReferenceEquals(resources[key], value)) resources[key] = value;
                }
            });
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The kinds of content a label comes in: a string, a TextBlock in a panel, a string with an access key.
    /// </summary>
    private enum Kind { Text, Panel, AccessKey }

    private static readonly Kind[] AllKinds = [Kind.Text, Kind.Panel, Kind.AccessKey];

    /// <summary>
    /// A control in <paramref name="style"/> with the label as its content, in the host it is laid out in: a menu for
    /// a menu item, a plain border for the rest. The access key goes before the label's second word.
    /// </summary>
    private static (FrameworkElement Host, Control Control, Func<TextBlock?> Label) Make(Style style, Kind kind)
    {
        object Content() => kind switch
        {
            Kind.Panel => new StackPanel { Children = { new TextBlock { Text = Label } } },
            Kind.AccessKey => Label.Replace(" ", " _", StringComparison.Ordinal),
            _ => Label,
        };

        Control control;
        FrameworkElement host;
        if (typeof(MenuItem).IsAssignableFrom(style.TargetType))
        {
            var item = new MenuItem { Header = Content(), Style = style };
            var menu = new ContextMenu();
            menu.Items.Add(item);
            (control, host) = (item, menu);
        }
        else
        {
            var content = (ContentControl)Activator.CreateInstance(style.TargetType)!;
            content.Content = Content();
            content.Style = style;
            (control, host) = (content, new Border { Child = content });
        }
        return (host, control, () => Descendants<TextBlock>(control).FirstOrDefault(Shows));
    }

    /// <summary>
    /// Whether <paramref name="block"/> shows the label. Read through its content rather than <c>Text</c>, because an
    /// <c>AccessText</c>, which draws a menu item's header, puts its words in the TextBlock's inlines and leaves
    /// <c>Text</c> empty, and keeps the access key's underscore there without drawing it.
    /// </summary>
    private static bool Shows(TextBlock block) =>
        new TextRange(block.ContentStart, block.ContentEnd).Text.Replace("_", "", StringComparison.Ordinal) == Label;

    /// <summary>
    /// The label's colour and the colour under it, each laid over what is beneath, and whether a solid fill between
    /// them covers <paramref name="under"/> entirely.
    /// </summary>
    private static (Color Ink, Color Paper, bool Covered) Drawn(TextBlock label, FrameworkElement host, Color under)
    {
        var fills = new List<(SolidColorBrush Brush, DependencyObject Owner)>();
        for (var at = VisualTreeHelper.GetParent(label); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            var fill = at switch { Border b => b.Background, Panel p => p.Background, _ => null };
            if (fill is SolidColorBrush solid) fills.Add((solid, at));
            else Assert.True(fill is null, $"a {fill?.GetType().Name} sits behind the label, which this cannot read");
            if (ReferenceEquals(at, host)) break;
        }

        var paper = under;
        var covered = false;
        foreach (var (brush, owner) in Enumerable.Reverse(fills))
        {
            var alpha = brush.Color.A / 255.0 * OpacityUpTo(owner, host);
            paper = Over(brush.Color, alpha, paper);
            covered |= alpha >= 1.0;
        }
        var ink = (SolidColorBrush)label.Foreground;
        return (Over(ink.Color, ink.Color.A / 255.0 * OpacityUpTo(label, host), paper), paper, covered);
    }

    private static double OpacityUpTo(DependencyObject from, FrameworkElement host)
    {
        var opacity = 1.0;
        for (var at = from; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement element) opacity *= element.Opacity;
            if (ReferenceEquals(at, host)) break;
        }
        return opacity;
    }

    private static Color Over(Color top, double alpha, Color bottom) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));

    /// <summary>
    /// WCAG 2.x contrast between two opaque colours, kept here rather than shared, as the theme tests do.
    /// </summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Each style and theme colour a view names together: a <c>Foreground="{DynamicResource ...}"</c> attribute on a
    /// button, chip, checkbox or menu item, with the style it names or, with none, the implicit one for its type.
    /// </summary>
    private static List<(string Name, string Foreground)> ForegroundsTheViewsGive()
    {
        var appDir = TestPaths.AppProject();
        var files = Directory.GetFiles(Path.Combine(appDir, "Views"), "*.xaml")
            .Append(Path.Combine(appDir, "MainWindow.xaml"));
        var pairs = new SortedSet<(string Name, string Foreground)>();
        foreach (var file in files)
        {
            var markup = XamlComment().Replace(File.ReadAllText(file), "");
            foreach (var tag in ControlStartTag().Matches(markup).Cast<Match>())
            {
                var attributes = tag.Groups["attrs"].Value;
                if (ForegroundResource().Match(attributes) is not { Success: true } foreground) continue;
                var name = StyleResource().Match(attributes) is { Success: true } style
                    ? style.Groups["key"].Value
                    : "implicit " + tag.Groups["type"].Value;
                pairs.Add((name, foreground.Groups["key"].Value));
            }
        }
        return [.. pairs];
    }

    /// <summary>
    /// Every style the views name on a button, chip, checkbox or menu item, and those of them a view may put on an
    /// inner card: inside a <c>CardInner</c> border or anything filled with <c>Surface2</c>, or inside a template, a
    /// style or a resource, whose place the markup does not show.
    /// </summary>
    private static (HashSet<string> Named, HashSet<string> OnInnerCards) WhereTheViewsPutEachStyle()
    {
        var appDir = TestPaths.AppProject();
        var named = new HashSet<string>(StringComparer.Ordinal);
        var onInnerCards = new HashSet<string>(StringComparer.Ordinal);

        void Walk(XElement element, bool innerCard, bool unplaced)
        {
            var name = element.Name.LocalName;
            var style = (string?)element.Attribute("Style") ?? "";
            var background = (string?)element.Attribute("Background") ?? "";
            innerCard |= style.Contains("CardInner", StringComparison.Ordinal)
                         || background.Contains("Surface2", StringComparison.Ordinal);
            unplaced |= name.EndsWith(".Resources", StringComparison.Ordinal)
                        || name is "DataTemplate" or "ControlTemplate" or "Style" or "Setter";
            if (ControlNames.Contains(name))
            {
                var key = StyleResource().Match(" Style=\"" + style + "\"") is { Success: true } match
                    ? match.Groups["key"].Value
                    : "implicit " + name;
                named.Add(key);
                if (innerCard || unplaced) onInnerCards.Add(key);
            }
            foreach (var child in element.Elements()) Walk(child, innerCard, unplaced);
        }

        foreach (var file in Directory.GetFiles(Path.Combine(appDir, "Views"), "*.xaml")
                     .Append(Path.Combine(appDir, "MainWindow.xaml")))
        {
            Walk(XDocument.Load(file).Root!, innerCard: false, unplaced: false);
        }
        return (named, onInnerCards);
    }

    private static readonly HashSet<string> ControlNames =
        new(["Button", "RadioButton", "CheckBox", "ToggleButton", "RepeatButton", "MenuItem"], StringComparer.Ordinal);

    private static void Lay(FrameworkElement host)
    {
        host.Measure(new Size(600, 200));
        host.Arrange(new Rect(0, 0, 600, 200));
        host.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var at = VisualTreeHelper.GetParent(child); at is not null; at = VisualTreeHelper.GetParent(at))
            yield return at;
    }

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

    [GeneratedRegex(
        @"<(?<type>Button|RadioButton|CheckBox|ToggleButton|RepeatButton|MenuItem)\b(?<attrs>(?:[^>""]|""[^""]*"")*)>",
        RegexOptions.Singleline)]
    private static partial Regex ControlStartTag();

    [GeneratedRegex(@"\sForeground=""\{DynamicResource (?<key>\w+)\}""")]
    private static partial Regex ForegroundResource();

    [GeneratedRegex(@"\sStyle=""\{StaticResource (?<key>\w+)\}""")]
    private static partial Regex StyleResource();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();
}
