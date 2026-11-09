// SysManager · CutTextTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SysManager.Helpers;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// <see cref="CutText"/> opens a text's own words in its tooltip only while the text is cut, and any other tooltip
/// always (#3214).
/// </summary>
/// <remarks>
/// A text is laid out here on its own, with no Application, at a width chosen to cut it or not. The tooltip's opening
/// is the event WPF raises before it shows one; marking it handled is what keeps the tooltip shut.
/// </remarks>
public class CutTextTests
{
    private const string Long = "speed.cloudflare.com, a name longer than its column";

    private static TextBlock Laid(string text, double width, object? toolTip = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = toolTip ?? text,
        };
        block.Measure(new Size(width, 100));
        block.Arrange(new Rect(0, 0, width, 100));
        return block;
    }

    [StaFact]
    public void AText_IsCut_OnlyWhenItsWordsAreWiderThanItIsShown()
    {
        Assert.True(CutText.IsCut(Laid(Long, 60)), "a long text at 60 px read as whole");
        Assert.False(CutText.IsCut(Laid("On", 60)), "a short text at 60 px read as cut");
        Assert.False(CutText.IsCut(Laid("", 60)), "an empty text read as cut");
    }

    [StaFact]
    public void ATextLaidOutWhole_ButCutByItsParent_IsCut()
    {
        var block = new TextBlock { Text = Long };
        var holder = new Grid { Width = 60 };
        holder.Children.Add(block);
        holder.Measure(new Size(60, 100));
        holder.Arrange(new Rect(0, 0, 60, 100));

        Assert.True(CutText.IsCut(block), "a text its parent cuts read as whole");
    }

    [StaFact]
    public void ItsOwnWordsAsATooltip_OpenOnlyWhileTheTextIsCut()
    {
        Assert.True(CutText.ShouldShow(Laid(Long, 60)));
        Assert.False(CutText.ShouldShow(Laid("On", 60)));
    }

    [StaFact]
    public void AnyOtherTooltip_OpensWhetherOrNotTheTextIsCut()
    {
        Assert.True(CutText.ShouldShow(Laid("On", 60, "Blocked by the privacy preset")));
        Assert.True(CutText.ShouldShow(Laid("On", 60, new ToolTip { Content = "On" })));
    }

    [StaFact]
    public void SetOnAText_ItKeepsTheTooltipShutOverWordsThatFit_AndOpenOverWordsThatAreCut()
    {
        var whole = Laid("On", 60);
        var cut = Laid(Long, 60);
        CutText.SetToolTip(whole, true);
        CutText.SetToolTip(cut, true);

        Assert.True(CutText.GetToolTip(whole));
        Assert.True(Opening(whole).Handled, "the tooltip opened over a text that fits");
        Assert.False(Opening(cut).Handled, "the tooltip was kept shut over a text that is cut");

        CutText.SetToolTip(whole, false);
        Assert.False(Opening(whole).Handled, "turned off, it still kept the tooltip shut");
    }

    /// <summary>Raises on <paramref name="text"/> the event WPF raises before it opens the text's tooltip.</summary>
    private static ToolTipEventArgs Opening(TextBlock text)
    {
        // Its one constructor is internal: the event is WPF's to raise, and this stands in for the tooltip service.
        var args = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)!;
        args.RoutedEvent = FrameworkElement.ToolTipOpeningEvent;
        args.Source = text;
        text.RaiseEvent(args);
        return args;
    }
}
