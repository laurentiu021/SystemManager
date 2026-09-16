// SysManager · TypographyTokenTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysManager.IntegrationTests;

/// <summary>
/// Proves the typography tokens resolve to the values the raw <c>FontSize</c> attributes had, so
/// repointing a view to one is genuinely appearance-neutral.
/// </summary>
/// <remarks>
/// #1634 replaced 54 raw <c>FontSize="13"</c> and 9 raw <c>FontSize="16" FontWeight="SemiBold"</c> with
/// <c>Body</c> and <c>MetricCompact</c>. The claim "nothing renders differently" rests entirely on
/// <c>BasedOn="{StaticResource {x:Type TextBlock}}"</c>: an explicit style REPLACES the keyless
/// <c>&lt;Style TargetType="TextBlock"&gt;</c>, so without it those TextBlocks would silently lose their
/// <c>TextPrimary</c> foreground and <c>ClearType</c> rendering. Reading the resolved values off a real
/// TextBlock is the only way to check that, since XAML compiles either way.
/// <para>Applied on the STA thread with the app's own resource dictionaries loaded — <c>DynamicResource</c>
/// lookups return nothing without them, which would make every assertion here pass on null.</para>
/// </remarks>
public class TypographyTokenTests
{
    private static TextBlock WithStyle(string key)
    {
        var style = (Style)Application.Current!.Resources[key];
        Assert.NotNull(style);
        var block = new TextBlock { Style = style };
        // Force the style to apply, so the resolved values are the ones WPF would render.
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block;
    }

    /// <summary>
    /// The colour the implicit <c>&lt;Style TargetType="TextBlock"&gt;</c> hands out, read from the theme
    /// resource it points at.
    /// </summary>
    /// <remarks>
    /// NOT read off a bare <c>new TextBlock()</c>. Measured: a TextBlock created in code and never added to
    /// a tree does not pick up the application-level implicit style at all — it reports the WPF default
    /// black. So a baseline taken that way would have compared every token against black and passed only
    /// for the broken case, which is exactly backwards. That the styled blocks below DO resolve this brush
    /// is the evidence BasedOn works.
    /// </remarks>
    private static Brush ExpectedForeground() =>
        (Brush)Application.Current!.Resources["TextPrimary"];

    [Fact]
    public void Body_IsThirteen_AndKeepsWhatTheImplicitStyleGives()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var block = WithStyle("Body");

            Assert.Equal(13d, block.FontSize);
            // Weight deliberately untouched: half the 54 call sites set their own, and a token that
            // declared one would read as if it controlled them while doing nothing.
            Assert.Equal(FontWeights.Normal, block.FontWeight);
            // The three things BasedOn is there for.
            Assert.Equal(ExpectedForeground().ToString(), block.Foreground.ToString());
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(block));
            Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(block));
        });
    }

    [Fact]
    public void MetricCompact_IsSixteenSemiBold_AndKeepsWhatTheImplicitStyleGives()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var block = WithStyle("MetricCompact");

            Assert.Equal(16d, block.FontSize);
            Assert.Equal(FontWeights.SemiBold, block.FontWeight);
            Assert.Equal(ExpectedForeground().ToString(), block.Foreground.ToString());
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(block));
        });
    }

    /// <summary>
    /// A local attribute still beats the token, which is what makes the repointing safe: the call sites
    /// that set their own weight or colour keep them.
    /// </summary>
    [Fact]
    public void ALocalAttribute_StillBeatsTheToken()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var style = (Style)Application.Current!.Resources["Body"];
            var block = new TextBlock
            {
                Style = style,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Magenta,
            };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Assert.Equal(13d, block.FontSize);                       // from the token
            Assert.Equal(FontWeights.SemiBold, block.FontWeight);    // from the element
            Assert.Equal(Brushes.Magenta, block.Foreground);         // from the element
        });
    }

    /// <summary>
    /// Every Metric rung resolves a colour, including <c>Metric</c> itself — which did not until #1634.
    /// </summary>
    /// <remarks>
    /// <c>Metric</c> was the one keyed TextBlock style that neither set <c>Foreground</c> nor was
    /// <c>BasedOn</c> the implicit style, so it handed its user the WPF default brush. It never showed
    /// because all ten call sites name a colour; the eleventh would have been black on a dark surface.
    /// </remarks>
    [Theory]
    [InlineData("MetricCompact", 16)]
    [InlineData("MetricSmall", 20)]
    [InlineData("Metric", 22)]
    [InlineData("MetricLarge", 26)]
    [InlineData("MetricHero", 30)]
    public void EveryMetricRung_ResolvesAColourAndItsOwnSize(string key, int expectedSize)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var block = WithStyle(key);

            Assert.Equal((double)expectedSize, block.FontSize);
            Assert.Equal(FontWeights.SemiBold, block.FontWeight);
            Assert.Equal(ExpectedForeground().ToString(), block.Foreground.ToString());
            Assert.NotEqual(Brushes.Black.ToString(), block.Foreground.ToString());
        });
    }
}
