// SysManager · MarkdownTextBlockTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;
using System.Windows.Documents;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="MarkdownTextBlock"/> attached property.
/// Verifies that markdown syntax is converted to proper WPF Inlines.
/// </summary>
public class MarkdownTextBlockTests
{
    [StaFact]
    public void NullMarkdown_ClearsInlines()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "hello");
        MarkdownTextBlock.SetMarkdown(tb, null);
        Assert.Empty(tb.Inlines);
    }

    [StaFact]
    public void EmptyMarkdown_ClearsInlines()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "");
        Assert.Empty(tb.Inlines);
    }

    [StaFact]
    public void PlainText_ProducesRun()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "Hello world");
        Assert.Single(tb.Inlines);
        Assert.IsType<Run>(tb.Inlines.FirstInline);
        Assert.Equal("Hello world", ((Run)tb.Inlines.FirstInline).Text);
    }

    [StaFact]
    public void Heading_ProducesBold()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "## Fixed");
        Assert.Single(tb.Inlines);
        Assert.IsType<Bold>(tb.Inlines.FirstInline);
        var bold = (Bold)tb.Inlines.FirstInline;
        Assert.Equal("Fixed", ((Run)bold.Inlines.FirstInline).Text);
    }

    [StaFact]
    public void BulletDash_ProducesBulletSymbol()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "- Item one");
        // Should produce: Run("  • ") + Run("Item one")
        Assert.Equal(2, tb.Inlines.Count);
        var first = (Run)tb.Inlines.FirstInline;
        Assert.Equal("  • ", first.Text);
    }

    [StaFact]
    public void BulletAsterisk_ProducesBulletSymbol()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "* Item two");
        Assert.Equal(2, tb.Inlines.Count);
        var first = (Run)tb.Inlines.FirstInline;
        Assert.Equal("  • ", first.Text);
    }

    [StaFact]
    public void BoldInline_ProducesBoldRun()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "This is **important** text");
        // Should produce: Run("This is ") + Bold("important") + Run(" text")
        Assert.Equal(3, tb.Inlines.Count);
        var inlines = tb.Inlines.ToList();
        Assert.IsType<Run>(inlines[0]);
        Assert.IsType<Bold>(inlines[1]);
        Assert.IsType<Run>(inlines[2]);
        Assert.Equal("important", ((Run)((Bold)inlines[1]).Inlines.FirstInline).Text);
    }

    [StaFact]
    public void CodeInline_ProducesConsolasRun()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "Use `dotnet build` here");
        var inlines = tb.Inlines.ToList();
        Assert.Equal(3, inlines.Count);
        Assert.IsType<Run>(inlines[1]);
        var codeRun = (Run)inlines[1];
        Assert.Equal("dotnet build", codeRun.Text);
        Assert.Equal("Consolas", codeRun.FontFamily.Source);
    }

    [StaFact]
    public void MultipleLines_ProducesLineBreaks()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "Line 1\nLine 2\nLine 3");
        // Should produce: Run + LineBreak + Run + LineBreak + Run
        Assert.Equal(5, tb.Inlines.Count);
        var inlines = tb.Inlines.ToList();
        Assert.IsType<LineBreak>(inlines[1]);
        Assert.IsType<LineBreak>(inlines[3]);
    }

    [StaFact]
    public void BlankLine_ProducesExtraLineBreak()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "Paragraph 1\n\nParagraph 2");
        // Line 1: Run("Paragraph 1")
        // Blank: LineBreak (paragraph break)
        // Line 2: LineBreak + Run("Paragraph 2")
        var inlines = tb.Inlines.ToList();
        Assert.True(inlines.Count >= 3);
        Assert.True(inlines.Count(i => i is LineBreak) >= 2);
    }

    // Regression: fenced code blocks used to leak their ``` markers (and the language tag) as literal
    // text because there was no rule for them; the content also wasn't rendered monospace.
    [StaFact]
    public void FencedCodeBlock_SwallowsFences_AndRendersContentMonospace()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "```powershell\nGet-FileHash file.exe\n```");
        var runs = tb.Inlines.OfType<Run>().ToList();
        // No inline should contain the fence marker or the language tag.
        Assert.DoesNotContain(runs, r => r.Text.Contains("```"));
        Assert.DoesNotContain(runs, r => r.Text.Contains("powershell"));
        // The code line renders, in the monospace code font.
        var codeRun = runs.FirstOrDefault(r => r.Text.Contains("Get-FileHash"));
        Assert.NotNull(codeRun);
        Assert.Equal("Consolas", codeRun!.FontFamily.Source);
    }

    // Regression: a horizontal-rule line (---) used to render as three literal dashes.
    [StaFact]
    public void HorizontalRule_DoesNotLeakLiteralDashes()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "Above\n\n---\n\nBelow");
        var runs = tb.Inlines.OfType<Run>().ToList();
        Assert.DoesNotContain(runs, r => r.Text.Trim() == "---");
        // The surrounding content still renders.
        Assert.Contains(runs, r => r.Text.Contains("Above"));
        Assert.Contains(runs, r => r.Text.Contains("Below"));
    }

    // A real sentence containing a dash must NOT be swallowed as a horizontal rule.
    [StaFact]
    public void DashInSentence_IsNotTreatedAsRule()
    {
        var tb = new TextBlock();
        MarkdownTextBlock.SetMarkdown(tb, "a - b");
        Assert.Contains(tb.Inlines.OfType<Run>(), r => r.Text.Contains("a - b"));
    }
}
