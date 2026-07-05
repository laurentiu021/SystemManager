// SysManager · MarkdownTextBlock — lightweight markdown-to-Inlines for TextBlock
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SysManager.Helpers;

/// <summary>
/// Attached property that parses simple GitHub-flavoured markdown and
/// populates a <see cref="TextBlock"/>'s <see cref="TextBlock.Inlines"/>
/// with formatted <see cref="Run"/>, <see cref="Bold"/>, and
/// <see cref="LineBreak"/> elements.
///
/// Supported syntax: ## headings, **bold**, - / * bullets, `code`,
/// blank-line paragraph breaks. Anything else is rendered as plain text.
/// </summary>
public static partial class MarkdownTextBlock
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.RegisterAttached(
            "Markdown",
            typeof(string),
            typeof(MarkdownTextBlock),
            new PropertyMetadata(null, OnMarkdownChanged));

    public static string? GetMarkdown(DependencyObject obj) => (string?)obj.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject obj, string? value) => obj.SetValue(MarkdownProperty, value);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();

        var md = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(md)) return;

        var lines = md.Split('\n').Select(l => l.TrimEnd('\r'));
        var isFirst = true;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // ``` fenced code block toggle — swallow the fence marker line itself
            // (with or without a language tag, e.g. ```powershell). While inside a
            // fence, every line renders verbatim as monospace, no inline parsing.
            if (trimmed.StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                if (!isFirst)
                    tb.Inlines.Add(new LineBreak());
                isFirst = false;
                tb.Inlines.Add(new Run(line) { FontFamily = CodeFontFamily });
                continue;
            }

            // Skip blank lines — insert a small paragraph break instead
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!isFirst)
                {
                    tb.Inlines.Add(new LineBreak());
                }
                continue;
            }

            // Horizontal rule (--- / *** / ___, 3+ of the same char) → render nothing
            // rather than leaking the literal dashes as text. Treated as a paragraph break.
            if (IsHorizontalRule(trimmed))
            {
                if (!isFirst)
                    tb.Inlines.Add(new LineBreak());
                continue;
            }

            if (!isFirst)
                tb.Inlines.Add(new LineBreak());
            isFirst = false;

            // ## Heading → bold line
            if (line.StartsWith('#'))
            {
                var text = line.TrimStart('#').Trim();
                tb.Inlines.Add(new Bold(new Run(text)));
                continue;
            }

            // - bullet or * bullet → indented bullet
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                var text = line.TrimStart().TrimStart('-', '*').Trim();
                tb.Inlines.Add(new Run("  • "));
                AddFormattedText(tb, text);
                continue;
            }

            // Regular line — parse inline formatting
            AddFormattedText(tb, line);
        }
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`")]
    private static partial Regex InlineFormattingRegex();

    // A markdown horizontal rule: 3+ of the SAME char among -, *, _ (strict, so a real
    // sentence like "a - b" is not mistaken for a rule).
    private static bool IsHorizontalRule(string trimmed)
        => trimmed.Length >= 3
           && (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_'));

    // PERF-007: Cache FontFamily to avoid allocating a new instance per code span render.
    private static readonly FontFamily CodeFontFamily = new("Consolas");

    /// <summary>
    /// Parses a single line for **bold** and `code` inline formatting
    /// and appends the resulting Inlines to the TextBlock.
    /// </summary>
    private static void AddFormattedText(TextBlock tb, string text)
    {
        // Merge bold and code patterns, process left-to-right
        var combined = InlineFormattingRegex().Matches(text);
        var pos = 0;

        foreach (Match m in combined)
        {
            // Plain text before this match
            if (m.Index > pos)
                tb.Inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups[1].Success)
            {
                // **bold**
                tb.Inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            }
            else if (m.Groups[2].Success)
            {
                // `code`
                tb.Inlines.Add(new Run(m.Groups[2].Value)
                {
                    FontFamily = CodeFontFamily,
                    Foreground = Application.Current?.TryFindResource("Accent") as Brush ?? Brushes.CornflowerBlue
                });
            }

            pos = m.Index + m.Length;
        }

        // Remaining plain text
        if (pos < text.Length)
            tb.Inlines.Add(new Run(text[pos..]));
    }
}
