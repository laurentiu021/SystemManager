// SysManager · UiAutomationContractTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SysManager.Tests;

public partial class UiAutomationContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly IReadOnlyDictionary<string, string> DescriptiveAccessibleNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["btn-cleanup-clean-temp"] = "Clean TEMP — clean temporary files",
            ["btn-cleanup-empty-recycle-bin"] = "Empty Recycle Bin",
            // On System Fixes since #1493, where the repairs belong; Quick Cleanup keeps the two
            // component-store buttons below, which reclaim space rather than repair anything.
            // Large Files moved out of Deep Cleanup into its own Storage tab (#1523); its Scan button is
            // the one action on the page, so it carries an id like every other tab's primary action.
            ["btn-large-files-scan"] = "Scan — list the biggest files in this location",
            ["btn-fixes-sfc"] = "Run — repair damaged Windows files with SFC",
            ["btn-fixes-dism"] = "Run — repair the Windows component store with DISM",
            // The two shell fixes need no administrator rights (#1490), which is why they sit in their
            // own section on that tab rather than under the elevation banner's claim.
            ["btn-fixes-restart-explorer"] = "Restart — restart Windows Explorer",
            ["btn-fixes-rebuild-icon-cache"] = "Rebuild — rebuild the icon and thumbnail cache",
            ["btn-cleanup-analyze-store"] = "Check component store — report what Windows considers reclaimable",
            ["btn-cleanup-clean-store"] = "Clean up component store — remove the superseded components Windows keeps",
            ["btn-cleanup-cancel"] = "Cancel the running operation",
            ["btn-system-health-scan"] = "Scan system health",
            ["btn-system-health-smart"] = "Run SMART check — disk health",
            ["btn-system-health-memtest"] = "Run MemTest (reboot) — schedule a memory test on next reboot",
            ["btn-logs-refresh"] = "Refresh logs",
            ["btn-logs-export-csv"] = "Export CSV — the event log",
            ["btn-services-refresh"] = "Refresh services",
            ["btn-services-clear-marks"] = "Clear all marked services",
            ["btn-ping-start"] = "Start ping monitoring",
            ["btn-ping-stop"] = "Stop ping monitoring",
            ["btn-ping-clear"] = "Clear ping history",
            ["btn-dashboard-scan-system"] = "Scan system",
            ["btn-dashboard-why-slow"] = "Why is it slow? — look for what is slowing this PC down right now",
            ["btn-drivers-list"] = "List drivers",
            ["btn-uninstaller-uninstall-selected"] = "Uninstall selected applications",
            ["btn-windows-update-install-module"] = "Install PSWindowsUpdate for update history",
            ["btn-windows-update-check-module"] = "Check now — check whether PSWindowsUpdate is installed",
            ["btn-windows-update-list"] = "List updates — available Windows updates",
            ["btn-windows-update-history"] = "Show Windows Update history",
            ["btn-windows-update-pending-reboot"] = "Pending reboot? — check whether a reboot is pending",
            ["btn-windows-update-install-selected"] = "Install selected Windows updates",
            ["btn-app-updates-scan"] = "Scan for updates",
            ["btn-system-health-memory-errors"] = "Check memory errors",
            ["btn-logs-open-event-viewer"] = "Open Event Viewer",
            // Renamed for #1647: this button sits beside "Open Event Viewer" on a tab titled
            // after the Windows Event Log, but opens SysManager's own diagnostic folder. The
            // accessible name now says whose logs, so a screen-reader user is not misled either.
            ["btn-logs-open-folder"] = "SysManager's own logs — open SysManager's own log folder",
            ["btn-ping-add-target"] = "Add target"
        };

    [Fact]
    public void AssertedButtons_HaveUniqueStableAutomationIds()
    {
        var uiTestSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(TestPaths.SolutionDir(), "SysManager.UITests"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        var referencedIds = FindButtonByIdCall()
            .Matches(uiTestSource)
            .Select(match => match.Groups["id"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.DoesNotContain("FindButton(", uiTestSource, StringComparison.Ordinal);

        var sourceXaml = EnumerateSourceXaml()
            .Select(XDocument.Load)
            .ToArray();
        var actionElements = sourceXaml
            .SelectMany(document => document.Descendants())
            .Select(element => new
            {
                Element = element,
                Id = (string?)element.Attribute("AutomationProperties.AutomationId")
            })
            .Where(item => item.Id?.StartsWith("btn-", StringComparison.Ordinal) is true)
            .ToArray();
        var buttonIds = actionElements
            .Select(item => item.Id!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referencedIds);
        Assert.Equal(actionElements.Length, DescriptiveAccessibleNames.Count);
        Assert.All(actionElements, item =>
        {
            Assert.Equal(Presentation + "Button", item.Element.Name);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    (string?)item.Element.Attribute("AutomationProperties.Name")),
                $"Button '{item.Id}' has no explicit accessible name.");
        });
        foreach (var (id, expectedName) in DescriptiveAccessibleNames)
        {
            var action = Assert.Single(actionElements, item => item.Id == id);
            Assert.Equal(
                expectedName,
                (string?)action.Element.Attribute("AutomationProperties.Name"));
        }
        Assert.Equal(buttonIds.Distinct(StringComparer.Ordinal), buttonIds);
        Assert.Equal(referencedIds, buttonIds);

        var currentViewHost = Assert.Single(
            sourceXaml.SelectMany(document => document.Descendants()),
            element =>
                (string?)element.Attribute("AutomationProperties.AutomationId")
                == "CurrentViewHost");
        Assert.Equal(Presentation + "UserControl", currentViewHost.Name);
        Assert.Equal("{Binding SelectedNav.View}", (string?)currentViewHost.Attribute("Content"));
    }

    private static IEnumerable<string> EnumerateSourceXaml()
    {
        var projectDirectory = TestPaths.AppProject();
        return Directory
            .EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(projectDirectory, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)));
    }

    [GeneratedRegex("FindButtonById\\s*\\(\\s*\"(?<id>btn-[a-z0-9-]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FindButtonByIdCall();

    /// <summary>
    /// The elevation button is the same control on every page that needs administrator rights, so it must
    /// be announced with the same words everywhere — and with the words that are printed on it.
    /// <para>Across the 30 buttons labelled "Run as administrator" only 15 were announced that way: 5 said
    /// "Restart SysManager as administrator", 4 "Relaunch as administrator", 1 "Restart as administrator",
    /// and 5 carried no accessible name at all. A screen-reader user heard a different verb than the one on
    /// screen, and a voice-control user saying what they could read could not activate the app's most
    /// consequential control. WCAG 2.5.3 (Label in Name) is the rule; 30 copies of one control was the
    /// reason it had to be mechanical rather than remembered.</para>
    /// <para><b>There is now ONE copy.</b> The banner moved into <c>Views/AdminBanner.xaml</c>, so the 30
    /// buttons became one and agreement is structural rather than checked. The floor drops from 20 to 1 for
    /// that reason and no other — deliberately, not to make a failing assertion pass. What still needs
    /// asserting is that the one button's accessible name matches the words printed on it, which is what this
    /// guard does. What stops the copies coming back is
    /// <c>ArchitectureTests.TheElevationBanner_LivesInOneControl_AndItsHostsExposeWhatItBinds</c>; without
    /// that, a floor of 1 here would pass just as happily on a tree that had drifted back to 30.</para>
    /// </summary>
    [Fact]
    public void EveryElevationButton_IsAnnouncedWithTheWordsPrintedOnIt()
    {
        const string label = "Run as administrator";
        var views = TestPaths.AppDir("Views");

        var offenders = new List<string>();
        var checkedButtons = 0;

        foreach (var view in Directory.EnumerateFiles(views, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            foreach (var button in XDocument.Load(view)
                         .Descendants(Presentation + "Button")
                         .Where(b => (string?)b.Attribute("Content") == label))
            {
                checkedButtons++;
                var spoken = (string?)button.Attribute("AutomationProperties.Name");

                // No name at all is also a failure: WPF would fall back to the label, which happens to be
                // right, but the contract is stated explicitly on every other elevation button.
                if (spoken != label)
                    offenders.Add($"{Path.GetFileName(view)}: announced as \"{spoken ?? "(no name)"}\"");
            }
        }

        // Vacuity floor: if the elevation button were renamed, this would inspect nothing and pass. One,
        // because the banner is one control now — see the remarks. The sibling guard in ArchitectureTests is
        // what keeps that true; this one only asserts the label and the name agree.
        Assert.True(checkedButtons >= 1,
            $"only {checkedButtons} elevation buttons were found — if the label changed, update this guard "
            + "rather than letting it inspect nothing.");

        Assert.True(offenders.Count == 0,
            $"these elevation buttons are labelled \"{label}\" but announced differently, so a screen "
            + "reader says one thing while the screen says another and voice control cannot activate "
            + $"them:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>
    /// EVERY control with visible text is announced with the words printed on it (WCAG 2.5.3, Label
    /// in Name) — not only the elevation button above.
    /// <para>When <c>AutomationProperties.Name</c> does not contain the visible <c>Content</c>, a
    /// voice-control user who says the printed label cannot activate the control, and a screen reader
    /// announces different wording than a sighted user reads. Measured before this guard existed: of
    /// 324 controls carrying literal visible text, 273 had a name and <b>36 of those names did not
    /// contain the label</b> — "Enable 0.5 ms timer" announced as "Enable high-resolution timer",
    /// "Ask a question" as "Open GitHub Discussions", "Clean TEMP" as "Clean temporary files".</para>
    /// <para>The elevation guard above could not see any of them: it is scoped to the one literal
    /// "Run as administrator". A guard that fixes a single instance leaves the class open, which is
    /// how 36 accumulated after that one was closed.</para>
    /// <para>A name may still ADD detail — leading with the visible words and appending context is the
    /// fix applied across all 36 — it just may not REPLACE them.</para>
    /// <para>A later pass tightened <see cref="SpeaksTheLabel"/> from "the label's words appear in
    /// order" to "they appear as a contiguous phrase", because the loose reading was weaker than the
    /// sentence above promises: 41 further names passed it while still being unsayable — "Clear History"
    /// announced as "Clear alert history", "Empty Recycle Bin" as "Empty the Recycle Bin". All 41 were
    /// repaired in the same change.</para>
    /// <para>A glyph-only label ("X", "▲") is a symbol rather than text, so a descriptive name is the
    /// CORRECT treatment and flagging it would be wrong.</para>
    /// <para>It read only a literal <c>Content</c> beside a literal name, and the same rule was broken where it did
    /// not look (#2883). A name built from a binding is said as its <c>StringFormat</c>, so System Health's chkdsk
    /// row showed "Scan" and was announced "Run chkdsk on drive C:". Text inside a control is its label too, so
    /// "Run as admin for more sensors" was announced "Run as administrator for more temperature sensors". And a
    /// control with no words of its own is labelled by the text beside it: the switch before "Disable Xbox Game Bar /
    /// Game DVR overlay" was announced "Disable Xbox Game Bar overlay", the box after "Ping every" "Ping interval in
    /// seconds". So it now reads all three. A format's words are compared with its values left as gaps, so a label
    /// cannot match across a value. A name bound with no format is the value alone and is not read, nor is a label
    /// with a bound part, which is shown at run time.</para>
    /// </summary>
    [Fact]
    public void EveryLabelledControl_IsAnnouncedWithTheWordsPrintedOnIt()
    {
        var views = TestPaths.AppDir("Views");
        var offenders = new List<string>();
        var inspected = new Dictionary<LabelSource, int>();

        foreach (var view in Directory.EnumerateFiles(views, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            XDocument document;
            try { document = XDocument.Load(view); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var element in document.Descendants())
            {
                if (ShownLabel(element) is not { } shown) continue;
                if (SpokenWords(element) is not { } spoken) continue;

                var labelWords = LabelWords(shown.Label);
                if (labelWords.Count == 0 || labelWords.TrueForAll(w => w.Length <= 1)) continue;

                inspected[shown.Source] = inspected.GetValueOrDefault(shown.Source) + 1;
                if (SpeaksTheLabel(labelWords, spoken)) continue;

                var name = (string?)element.Attribute("AutomationProperties.Name");
                offenders.Add($"{Path.GetFileName(view)}: <{element.Name.LocalName}> shows \"{shown.Label}\" "
                              + $"({shown.Source}) but announces \"{name}\"");
            }
        }

        // Vacuity floors, one for each place a label is read from, so that one of them reading nothing is not hidden
        // by the others. 289, 9 and 49 when the bound names and the other two places were added (#2883).
        var floors = new Dictionary<LabelSource, int>
        {
            [LabelSource.Content] = 280,
            [LabelSource.TextInside] = 8,
            [LabelSource.TextBeside] = 45,
        };
        var thin = floors.Where(f => inspected.GetValueOrDefault(f.Key) < f.Value)
            .Select(f => $"{f.Key}: {inspected.GetValueOrDefault(f.Key)} inspected, from {f.Value} measured")
            .ToList();
        Assert.True(thin.Count == 0,
            "too few labelled controls were inspected, so part of the views is no longer read — fix this guard "
            + $"rather than trusting its pass:\n  {string.Join("\n  ", thin)}");

        Assert.True(offenders.Count == 0,
            "these controls are announced with different words than they display, so voice control "
            + "cannot activate them by their visible label and a screen reader disagrees with the "
            + "screen (WCAG 2.5.3). Lead the accessible name with the visible text and append any "
            + $"extra context after it:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>Where a control's visible label is read from.</summary>
    private enum LabelSource
    {
        /// <summary>Its <c>Content</c>, or a menu item's <c>Header</c>.</summary>
        Content,

        /// <summary>The text elements inside it.</summary>
        TextInside,

        /// <summary>The text beside a switch or an input that shows no words of its own.</summary>
        TextBeside,
    }

    /// <summary>The controls whose content is their label.</summary>
    private static readonly string[] ContentLabelled =
        ["Button", "CheckBox", "RadioButton", "ToggleButton", "RepeatButton", "Hyperlink", "MenuItem"];

    /// <summary>The switches, which are labelled by the text after them when they show none.</summary>
    private static readonly string[] Switches = ["CheckBox", "RadioButton", "ToggleButton"];

    /// <summary>The inputs, which are labelled by the text before them.</summary>
    private static readonly string[] Inputs = ["TextBox", "PasswordBox", "ComboBox", "Slider", "DatePicker"];

    /// <summary>What sits beside a label without being read: a coloured dot, a glyph.</summary>
    private static readonly string[] Decorations = ["Ellipse", "Rectangle", "Path", "Image"];

    /// <summary>
    /// The words a sighted user reads as the control's label, and where they come from, or null when the XAML does not
    /// say them: none at all, a glyph, or a value bound at run time.
    /// </summary>
    private static (string Label, LabelSource Source)? ShownLabel(XElement control)
    {
        var tag = control.Name.LocalName;
        if (ContentLabelled.Contains(tag, StringComparer.Ordinal))
        {
            var content = ((string?)control.Attribute(tag == "MenuItem" ? "Header" : "Content"))?.Trim();
            if (content is not null) return content.StartsWith('{') ? null : (content, LabelSource.Content);

            var inside = TextInside(control);
            if (inside.Runtime) return null;
            if (inside.Text is { } text) return (text, LabelSource.TextInside);
        }

        var beside = Switches.Contains(tag, StringComparer.Ordinal) ? TextBeside(control, after: true)
            : Inputs.Contains(tag, StringComparer.Ordinal) ? TextBeside(control, after: false)
            : null;
        return beside is null ? null : (beside, LabelSource.TextBeside);
    }

    /// <summary>
    /// The literal text of the text elements inside <paramref name="element"/>, joined; Runtime when one of them shows
    /// a bound value. Property elements are not content, save the content and inline ones: a tooltip, a context menu
    /// or a style holds text the control does not show.
    /// </summary>
    private static (string? Text, bool Runtime) TextInside(XElement element)
    {
        var parts = new List<string>();
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Value)) parts.Add(text.Value.Trim());
                continue;
            }
            if (node is not XElement child) continue;

            var tag = child.Name.LocalName;
            if (tag.Contains('.') && !tag.EndsWith(".Content", StringComparison.Ordinal)
                                  && !tag.EndsWith(".Inlines", StringComparison.Ordinal)) continue;
            if (tag is "Style" or "DataTemplate" or "ControlTemplate") continue;

            var (inner, runtime) = tag is "TextBlock" or "Run" ? TextOf(child) : TextInside(child);
            if (runtime) return (null, true);
            if (inner is not null) parts.Add(inner);
        }
        return (parts.Count == 0 ? null : string.Join(" ", parts), false);
    }

    /// <summary>The fonts the views draw glyphs with.</summary>
    private static readonly string[] IconFonts = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>A TextBlock's or a Run's literal text; none for a glyph, Runtime for a bound value.</summary>
    private static (string? Text, bool Runtime) TextOf(XElement textElement)
    {
        var font = (string?)textElement.Attribute("FontFamily") ?? "";
        if (IconFonts.Any(icon => font.Contains(icon, StringComparison.Ordinal))) return (null, false);

        var text = (string?)textElement.Attribute("Text");
        if (text is null) return TextInside(textElement);
        return text.TrimStart().StartsWith('{') ? (null, true) : (text.Trim(), false);
    }

    /// <summary>
    /// The text of the TextBlock next to <paramref name="control"/> in its panel, after it or before it, passing over a
    /// decoration; null when the neighbour is anything else or shows a bound value.
    /// </summary>
    private static string? TextBeside(XElement control, bool after)
    {
        if (control.Attribute("Content") is not null || TextInside(control) is (not null, _) or (_, true)) return null;

        var neighbours = after ? control.ElementsAfterSelf() : control.ElementsBeforeSelf().Reverse();
        foreach (var neighbour in neighbours)
        {
            var tag = neighbour.Name.LocalName;
            if (tag.Contains('.') || Decorations.Contains(tag, StringComparer.Ordinal)) continue;
            if (tag != "TextBlock") return null;

            var (text, runtime) = TextOf(neighbour);
            if (runtime) return null;
            if (text is null) continue;
            return text;
        }
        return null;
    }

    /// <summary>
    /// The words a screen reader says for the control's name: the literal name, or the binding's
    /// <c>StringFormat</c> with each value as a gap no label word matches. Null when there is no name, or the name is
    /// a bound value with no format.
    /// </summary>
    private static List<string>? SpokenWords(XElement control)
    {
        var name = ((string?)control.Attribute("AutomationProperties.Name"))?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!name.StartsWith('{')) return LabelWords(name);

        if (NameFormat().Match(name) is not { Success: true } format) return null;
        var pieces = FormatValue().Split(format.Groups["format"].Value.Replace("{}", "", StringComparison.Ordinal));
        var spoken = new List<string>();
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i > 0) spoken.Add("{value}");
            spoken.AddRange(LabelWords(pieces[i]));
        }
        return spoken;
    }

    [GeneratedRegex(@"StringFormat\s*=\s*'(?<format>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex NameFormat();

    [GeneratedRegex(@"\{\d+(?:,[^}]*)?(?::[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex FormatValue();

    /// <summary>The words of a label, lower-cased and stripped of punctuation, for comparison.</summary>
    private static List<string> LabelWords(string text)
        => [.. NonWordRun().Replace(text.ToLowerInvariant(), " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// True when the label's words appear as a CONTIGUOUS run inside the accessible name.
    /// <para>This used to accept the words merely in order, anywhere in the name — a weaker rule than
    /// the guard's own summary promises ("leading with the visible words and appending context").
    /// Forty-one names satisfied the loose reading while still failing WCAG 2.5.3 in the way that
    /// matters: "Clear History" announced as "Clear alert history", "Export CSV" as "Export logs to
    /// CSV", "Empty Recycle Bin" as "Empty the Recycle Bin". Every word is present and in order, yet a
    /// speech-recognition user who says the printed phrase matches nothing, because the matcher looks
    /// for the phrase and not for a scatter of its words.</para>
    /// <para>Contiguity is checked over the normalised WORD lists rather than the raw strings on
    /// purpose: that keeps the existing case- and punctuation-insensitivity, so "Enable / Disable" is
    /// satisfied by "Enable / Disable — the selected task", and a name stays free to re-case the label
    /// while continuing the sentence. A bound value in the name is a "{value}" entry, which no word of a label
    /// equals.</para>
    /// </summary>
    private static bool SpeaksTheLabel(List<string> label, List<string> spoken)
    {
        if (label.Count == 0 || label.Count > spoken.Count) return false;

        for (var start = 0; start <= spoken.Count - label.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < label.Count; offset++)
            {
                if (string.Equals(spoken[start + offset], label[offset], StringComparison.Ordinal))
                    continue;
                matches = false;
                break;
            }
            if (matches) return true;
        }
        return false;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordRun();

    /// <summary>
    /// The event-log severity column must not convey severity by colour alone. It previously held
    /// a bare coloured Ellipse under an empty header — no text, no tooltip, no accessible name —
    /// so severity was unavailable to anyone with a colour-vision deficiency and to a screen
    /// reader. Asserted against the real XAML because the defect lived entirely in the cell
    /// template: no view-model or model assertion could have caught it.
    /// </summary>
    [Fact]
    public void LogsSeverityColumn_ConveysSeverityAsTextNotColourAlone()
    {
        var logsView = TestPaths.AppFile("Views", "LogsView.xaml");
        var document = XDocument.Load(logsView);

        var severityColumn = Assert.Single(
            document.Descendants(Presentation + "DataGridTemplateColumn"),
            column => (string?)column.Attribute("Header") == "Severity");

        var cellRoot = severityColumn
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements()
            .Single();

        // The word is rendered, so a colour-blind sighted user reads it and a screen reader reads it as the cell's
        // text. The coloured dot may accompany it; it must not replace it. A name on the cell's panel reached
        // no one, since a StackPanel has no automation peer (#2785).
        Assert.Contains(
            cellRoot.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding SeverityLabel}");

        // The column must also be identifiable and sortable, which an empty header prevented.
        Assert.Equal("Severity", (string?)severityColumn.Attribute("SortMemberPath"));
    }
    /// <summary>
    /// Every input a user can focus but that carries no text of its own must be given an accessible name.
    /// </summary>
    /// <remarks>
    /// The sibling guard above covers controls whose <c>Content</c> IS their label — a Button, a CheckBox.
    /// A TextBox, ComboBox or Slider has no such text, so unless it is named it announces as its control
    /// type alone: "edit", "combo box". The label a sighted user reads is a separate TextBlock beside it,
    /// which a screen reader has no reason to associate with the control.
    /// <para>#1544 asked for <c>AutomationProperties.LabeledBy</c>, which is still unused across the app, and
    /// the measurement is why this guard does not require it: <b>132 of 133</b> real input instances already
    /// carry an explicit <c>AutomationProperties.Name</c>, which is the stronger form — it does not depend on
    /// an element relationship surviving a layout change. The remaining one is
    /// <c>PART_EditableTextBox</c> inside a ComboBox template, which is named by the ComboBox that templates
    /// it and would be wrong to name separately. So the app was already right and had nothing keeping it
    /// that way; this is the keeping.</para>
    /// <para>A bound name counts. Row controls inside a DataTemplate are named from their item
    /// (<c>EveryRowControl_AnnouncesTheRowItIsOn</c> holds them to naming the row), and a
    /// <c>{Binding}</c> there is a real name produced at runtime.</para>
    /// <para>Two exclusions. Anything inside a <c>ControlTemplate</c> or a <c>Style</c> is a template part,
    /// named by whatever it templates — that is the <c>PART_</c> case, and naming it would announce the part
    /// instead of the control. And <c>ProgressBar</c> is included rather than waved through: all of them are
    /// named today, so the bar stays where the code already is.</para>
    /// </remarks>
    [Fact]
    public void EveryInputWithoutItsOwnText_CarriesAnAccessibleName()
    {
        var appDir = TestPaths.AppProject();
        string[] inputs = ["TextBox", "PasswordBox", "ComboBox", "Slider", "DatePicker", "ProgressBar"];
        var offenders = new List<string>();
        var inspected = 0;

        var files = Directory
            .EnumerateFiles(Path.Combine(appDir, "Views"), "*.xaml", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(appDir, "MainWindow.xaml"))
            // App.xaml too, and not for completeness: it is the only file where the template-part exclusion
            // below has anything to exclude. Scoped to the views alone, that rule skipped zero elements and
            // was decoration; here it earns its place on PART_EditableTextBox, and a future unnamed input
            // added to a SHARED template gets caught rather than hiding in the one file nobody scanned.
            .Append(Path.Combine(appDir, "App.xaml"))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            XDocument document;
            try { document = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var element in document.Descendants()
                         .Where(e => inputs.Contains(e.Name.LocalName, StringComparer.Ordinal)))
            {
                // A template part is named by the control it templates, not by itself.
                if (element.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Style"))
                    continue;

                inspected++;

                // The literal attribute name, the way the sibling guard reads it. XDocument parses
                // AutomationProperties.Name as ONE local name in the default namespace, not as "Name" in an
                // "AutomationProperties" namespace — a first version looked for the latter, matched nothing,
                // and reported the whole app unnamed.
                var named = new[] { "AutomationProperties.Name", "AutomationProperties.LabeledBy",
                                    "AutomationProperties.AutomationId" }
                    .Any(attribute => !string.IsNullOrWhiteSpace((string?)element.Attribute(attribute)));
                if (named) continue;

                var hint = (string?)element.Attribute(XNamespace.Get(
                    "http://schemas.microsoft.com/winfx/2006/xaml") + "Name");
                offenders.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}"
                              + $"{(hint is null ? "" : " " + hint)}> announces only its control type");
            }
        }

        // Vacuity floor. A collapse means the XAML parse or the template-part exclusion started skipping
        // everything, and a clean pass would prove nothing. The count is printed so a legitimate change to
        // the population reads as a number to re-measure rather than as a mystery.
        Assert.True(inspected >= 110,
            $"only {inspected} input controls were inspected — fix this guard rather than trusting its pass.");

        Assert.True(offenders.Count == 0,
            "these inputs carry no text of their own and no accessible name, so a screen reader announces "
            + "the control type and nothing else — the label beside them on screen is not something it can "
            + "associate with them. Add AutomationProperties.Name with the words the label shows:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// No DockPanel docks a control that takes focus right or at the bottom ahead of another one.
    /// </summary>
    /// <remarks>
    /// Tab follows the order the XAML declares controls in. A DockPanel draws a child docked right to the right of
    /// everything declared after it, and one docked at the bottom below it, so a control docked that way ahead of
    /// another is reached first and drawn after: Tab runs against the screen. DNS &amp; Hosts docked Reset to DHCP,
    /// Apply and Undo right ahead of the preset picker, so Tab went Reset to DHCP, Apply, Undo, then the picker, the
    /// reverse of the row, and a keyboard user entering the card landed first on the button that changes the PC's DNS.
    /// Bulk Installer's Search came before its box, Boot Analyzer's Refresh before the Cancel drawn left of it, the
    /// PATH editor's add row before the folders above it, and Speed Test's Run before the server picker (#2883). Each
    /// declares its controls in the order they are drawn now, laid out the same.
    /// <para>Read from the XAML, so it holds in the blocking suite: where a DockPanel draws a child follows from the
    /// declaration alone. A control hidden or disabled now counts, because it is shown at another time, and so do the
    /// rows of a list, which are there at run time.</para>
    /// </remarks>
    [Fact]
    public void NoDockPanel_DocksAFocusableControlAheadOfAnother()
    {
        const string Backwards = """
            <DockPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Button DockPanel.Dock="Right" Content="Apply"/>
                <ComboBox/>
            </DockPanel>
            """;
        const string InOrder = """
            <DockPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ComboBox DockPanel.Dock="Left"/>
                <StackPanel DockPanel.Dock="Right"><Button Content="Cancel"/><Button Content="Refresh"/></StackPanel>
                <TextBlock Text="Status"/>
            </DockPanel>
            """;
        Assert.Single(BackwardsDocked(XElement.Parse(Backwards)));
        Assert.Empty(BackwardsDocked(XElement.Parse(InOrder)));

        var appDir = TestPaths.AppProject();
        var files = Directory.EnumerateFiles(Path.Combine(appDir, "Views"), "*.xaml", SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(appDir, "MainWindow.xaml"));
        var offenders = new List<string>();
        var inspected = 0;

        foreach (var file in files)
        {
            foreach (var panel in XDocument.Load(file).Descendants(Presentation + "DockPanel"))
            {
                if (panel.Elements().Any(child => FirstFocusable(child) is not null)) inspected++;
                offenders.AddRange(BackwardsDocked(panel).Select(problem => $"{Path.GetFileName(file)}: {problem}"));
            }
        }

        // Vacuity floor: the DockPanels holding a control Tab stops on, any of which a second control can turn around.
        Assert.True(inspected >= DockPanelsHoldingAControl,
            $"only {inspected} DockPanels holding a control that takes focus were read, from "
            + $"{DockPanelsHoldingAControl} measured — fix this guard rather than trusting its pass.");

        Assert.True(offenders.Count == 0,
            "these DockPanels dock a control right or at the bottom ahead of another, so Tab reaches it first while "
            + "the screen shows it after. Declare the controls in the order they are drawn, in a Grid or in one "
            + $"docked panel:\n  {string.Join("\n  ", offenders)}");
    }

    /// <summary>The DockPanels holding a control that takes focus: 57 when measured (#2883).</summary>
    private const int DockPanelsHoldingAControl = 55;

    /// <summary>The controls Tab stops on. Shared with the layout guards that ask whether a panel holds one.</summary>
    internal static readonly string[] TakesFocus =
    [
        "Button", "ToggleButton", "RepeatButton", "CheckBox", "RadioButton", "TextBox", "PasswordBox", "ComboBox",
        "Slider", "DatePicker", "ListBox", "ListView", "DataGrid", "TreeView", "Hyperlink",
    ];

    /// <summary>
    /// Each child of <paramref name="dockPanel"/> docked right or at the bottom with a control in it, followed by
    /// another child with one.
    /// </summary>
    private static IEnumerable<string> BackwardsDocked(XElement dockPanel)
    {
        var children = dockPanel.Elements().Where(child => !child.Name.LocalName.Contains('.')).ToList();
        for (var i = 0; i < children.Count; i++)
        {
            var side = (string?)children[i].Attribute("DockPanel.Dock");
            if (side is not ("Right" or "Bottom") || FirstFocusable(children[i]) is not { } first) continue;

            var later = children.Skip(i + 1).Select(FirstFocusable).FirstOrDefault(control => control is not null);
            if (later is not null)
                yield return $"{Described(first)}, docked {side.ToLowerInvariant()}, is ahead of {Described(later)}";
        }
    }

    /// <summary>
    /// The first control in <paramref name="element"/> that Tab stops on, the rows of a list's template included; not a
    /// control template's parts, a style, resources, a context menu or a tooltip, which are not in the page's order.
    /// </summary>
    private static XElement? FirstFocusable(XElement element) => element.DescendantsAndSelf().FirstOrDefault(node =>
        TakesFocus.Contains(node.Name.LocalName, StringComparer.Ordinal)
        && (string?)node.Attribute("Focusable") != "False"
        && (string?)node.Attribute("IsTabStop") != "False"
        && (string?)node.Attribute("KeyboardNavigation.IsTabStop") != "False"
        && !node.AncestorsAndSelf().TakeWhile(ancestor => ancestor != element).Any(ancestor =>
            ancestor.Name.LocalName is "ControlTemplate" or "Style"
            || ancestor.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
            || ancestor.Name.LocalName.EndsWith(".ContextMenu", StringComparison.Ordinal)
            || ancestor.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal)));

    /// <summary>A control as an offender line names it: its accessible name, else its text, else its kind.</summary>
    private static string Described(XElement control) =>
        $"<{control.Name.LocalName}> \"{(string?)control.Attribute("AutomationProperties.Name")
            ?? (string?)control.Attribute("Content") ?? "(no name)"}\"";
}
