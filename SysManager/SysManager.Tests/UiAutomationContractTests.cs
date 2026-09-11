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
            ["btn-cleanup-sfc"] = "SFC /scannow — run SFC system file check",
            ["btn-cleanup-dism"] = "Run DISM RestoreHealth",
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
        var solutionDirectory = FindSolutionDirectory();
        var uiTestSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Combine(solutionDirectory.FullName, "SysManager.UITests"),
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

        var sourceXaml = EnumerateSourceXaml(solutionDirectory)
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

    private static DirectoryInfo FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SysManager.UITests"))
                && Directory.Exists(Path.Combine(directory.FullName, "SysManager", "Views")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SysManager solution directory from the test output.");
    }

    private static IEnumerable<string> EnumerateSourceXaml(DirectoryInfo solutionDirectory)
    {
        var projectDirectory = Path.Combine(solutionDirectory.FullName, "SysManager");
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
        var views = Path.Combine(FindSolutionDirectory().FullName, "SysManager", "Views");

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
    /// <para>Two exclusions, both deliberate. A glyph-only label ("X", "▲") is a symbol rather than
    /// text, so a descriptive name is the CORRECT treatment and flagging it would be wrong. A name
    /// built from a <c>{Binding}</c> is produced at runtime, so the literal in the XAML is not what
    /// gets announced.</para>
    /// </summary>
    [Fact]
    public void EveryLabelledControl_IsAnnouncedWithTheWordsPrintedOnIt()
    {
        var views = Path.Combine(FindSolutionDirectory().FullName, "SysManager", "Views");
        string[] controls = ["Button", "CheckBox", "RadioButton", "ToggleButton"];
        var offenders = new List<string>();
        var checkedControls = 0;

        foreach (var view in Directory.EnumerateFiles(views, "*.xaml", SearchOption.TopDirectoryOnly))
        {
            XDocument document;
            try { document = XDocument.Load(view); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var element in document.Descendants()
                         .Where(e => controls.Contains(e.Name.LocalName, StringComparer.Ordinal)))
            {
                var label = ((string?)element.Attribute("Content"))?.Trim();
                var spoken = ((string?)element.Attribute("AutomationProperties.Name"))?.Trim();

                if (string.IsNullOrEmpty(label) || label.StartsWith('{')) continue;
                if (string.IsNullOrEmpty(spoken) || spoken.StartsWith('{')) continue;

                var labelWords = LabelWords(label);
                if (labelWords.Count == 0 || labelWords.TrueForAll(w => w.Length <= 1)) continue;

                checkedControls++;
                if (SpeaksTheLabel(labelWords, LabelWords(spoken))) continue;

                offenders.Add($"{Path.GetFileName(view)}: shows \"{label}\" but announces \"{spoken}\"");
            }
        }

        // Vacuity floor: the views carry hundreds of labelled controls, so a scan that inspected
        // almost nothing means the parsing broke rather than the code being clean.
        Assert.True(checkedControls >= 150,
            $"only {checkedControls} labelled controls were inspected — fix this guard rather than "
            + "trusting its pass.");

        Assert.True(offenders.Count == 0,
            "these controls are announced with different words than they display, so voice control "
            + "cannot activate them by their visible label and a screen reader disagrees with the "
            + "screen (WCAG 2.5.3). Lead the accessible name with the visible text and append any "
            + $"extra context after it:\n  {string.Join("\n  ", offenders)}");
    }

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
    /// while continuing the sentence.</para>
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
        var logsView = Path.Combine(
            FindSolutionDirectory().FullName, "SysManager", "Views", "LogsView.xaml");
        var document = XDocument.Load(logsView);

        var severityColumn = Assert.Single(
            document.Descendants(Presentation + "DataGridTemplateColumn"),
            column => (string?)column.Attribute("Header") == "Severity");

        var cellRoot = severityColumn
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements()
            .Single();

        // A screen reader needs a name on the cell content, and a colour-blind sighted user needs
        // the word rendered. The coloured dot may accompany them; it must not replace them.
        Assert.Equal(
            "{Binding SeverityLabel}",
            (string?)cellRoot.Attribute("AutomationProperties.Name"));
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
        var appDir = Path.Combine(FindSolutionDirectory().FullName, "SysManager");
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

}
