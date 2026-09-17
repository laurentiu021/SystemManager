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
    /// The four body-text tokens resolve their own size, weight and colour AND the two rendering options the
    /// keyless <c>&lt;Style TargetType="TextBlock"&gt;</c> hands out.
    /// </summary>
    /// <remarks>
    /// This is the assertion that unblocks #1634's sweep. Until these four carried
    /// <c>BasedOn="{StaticResource {x:Type TextBlock}}"</c>, swapping a bare <c>FontSize="11"</c> for
    /// <c>Style="{StaticResource Caption}"</c> looked like a no-op and was not: the element gave up
    /// <c>TextFormattingMode=Ideal</c> and <c>TextRenderingMode=ClearType</c>, because a keyed style replaces
    /// the keyless one rather than merging with it. 172 elements were waiting on that.
    /// <para>The colour column is the part worth reading twice. <c>SectionTitle</c> no longer restates
    /// <c>TextPrimary</c> — it inherits it — so this asserts the inheritance actually delivers it rather than
    /// a WPF default black. The other three deliberately differ from the inherited colour and keep their own
    /// setter; asserting the difference is what stops a later "tidy-up" dropping those too on the theory that
    /// BasedOn covers them.</para>
    /// </remarks>
    [Theory]
    [InlineData("SectionTitle", 14, "SemiBold", "TextPrimary")]
    [InlineData("Caption", 11, "Normal", "TextMuted")]
    [InlineData("Subtle", 12, "Normal", "TextSecondary")]
    [InlineData("SectionLabel", 11, "SemiBold", "TextMuted")]
    public void EveryBodyTextToken_ResolvesItsOwnLookAndTheImplicitRendering(
        string key, int expectedSize, string expectedWeight, string expectedColourKey)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var block = WithStyle(key);

            Assert.Equal((double)expectedSize, block.FontSize);
            Assert.Equal(
                expectedWeight == "SemiBold" ? FontWeights.SemiBold : FontWeights.Normal,
                block.FontWeight);

            var expectedColour = (Brush)Application.Current!.Resources[expectedColourKey];
            Assert.Equal(expectedColour.ToString(), block.Foreground.ToString());
            Assert.NotEqual(Brushes.Black.ToString(), block.Foreground.ToString());

            // The two BasedOn is here for. Before it, both fell back to the WPF default on any element
            // taking one of these tokens.
            Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(block));
            Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(block));
        });
    }

    /// <summary>
    /// <c>Display</c> is deliberately left off <c>BasedOn</c>, and that stays a decision rather than an
    /// oversight until someone looks at it on a running machine.
    /// </summary>
    /// <remarks>
    /// #1634 proposed adding it on the grounds that all 59 tab titles would move from
    /// <c>TextFormattingMode=Display</c> to <c>Ideal</c>. Measured here, that premise is wrong: WPF's default
    /// for <c>TextFormattingMode</c> IS <c>Ideal</c>, so the titles already render that way and BasedOn would
    /// not touch it. The only difference it would make to <c>Display</c> is <c>TextRenderingMode</c>, from
    /// <c>Auto</c> to <c>ClearType</c> — the same one-property change the other four just took.
    /// <para>Left out anyway, because the decision was made on the larger claim and the smaller one deserves
    /// its own look at 28px rather than being folded in as a rounding error. These two assertions are what
    /// make that a decision instead of an oversight: the test fails the moment someone completes the set,
    /// which is the prompt to check it on a machine that runs the app.</para>
    /// <para>What it must NOT lose meanwhile is its colour: <c>Display</c> restates <c>TextPrimary</c>
    /// itself, and that is load-bearing precisely because it inherits nothing.</para>
    /// </remarks>
    [Fact]
    public void Display_StillStandsAloneAndStillResolvesItsColour()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var block = WithStyle("Display");

            Assert.Equal(28d, block.FontSize);
            Assert.Equal(FontWeights.SemiBold, block.FontWeight);
            Assert.Equal(ExpectedForeground().ToString(), block.Foreground.ToString());
            Assert.NotEqual(Brushes.Black.ToString(), block.Foreground.ToString());

            Assert.Equal(
                TextFormattingMode.Ideal,
                TextOptions.GetTextFormattingMode(block));
            Assert.Equal(
                TextRenderingMode.Auto,
                TextOptions.GetTextRenderingMode(block));
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
