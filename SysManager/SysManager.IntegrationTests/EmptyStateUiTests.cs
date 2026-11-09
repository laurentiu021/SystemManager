// SysManager · EmptyStateUiTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysManager.Views;

namespace SysManager.IntegrationTests;

/// <summary>
/// The shared empty state, full and compact (#3221).
/// </summary>
/// <remarks>
/// Compact is for one in a card beside or in place of a chart or a console: the glyph and 24 px above and below took
/// more than such a card has at the default window, so Resource History's "No temperature data" asked for 158 px in a
/// chart area of 95, and its page scrolled to give it that. Laid out with the app's resources at a card's width.
/// </remarks>
public sealed class EmptyStateUiTests
{
    private const string Glyph = "\uE9CA";

    [Fact]
    public void TheFullState_ShowsItsGlyph_WithRoomAllRound()
    {
        StaHelper.Run(() =>
        {
            var state = Laid(compact: false, glyph: Glyph);

            Assert.Equal(Visibility.Visible, GlyphOf(state).Visibility);
            Assert.Equal(new Thickness(24), PanelOf(state).Margin);
        });
    }

    [Fact]
    public void TheCompactState_LeavesTheGlyphOut_AndHalfTheRoomAboveAndBelow()
    {
        StaHelper.Run(() =>
        {
            var full = Laid(compact: false, glyph: Glyph);
            var state = Laid(compact: true, glyph: Glyph);

            Assert.Equal(Visibility.Collapsed, GlyphOf(state).Visibility);
            Assert.Equal(new Thickness(24, 12, 24, 12), PanelOf(state).Margin);
            // The glyph with the 8 px under it, and 12 px above and below, are what it gives back.
            var glyph = GlyphOf(full);
            Assert.Equal(full.DesiredSize.Height - glyph.ActualHeight - 8 - 24, state.DesiredSize.Height, 1);
        });
    }

    [Fact]
    public void AStateWithNoGlyph_ShowsNone_FullOrCompact()
    {
        StaHelper.Run(() =>
        {
            Assert.Equal(Visibility.Collapsed, GlyphOf(Laid(compact: false, glyph: "")).Visibility);
            Assert.Equal(Visibility.Collapsed, GlyphOf(Laid(compact: true, glyph: "")).Visibility);
        });
    }

    /// <summary>An empty state with a two-line message, laid out at the width of a card on the default page.</summary>
    private static EmptyState Laid(bool compact, string glyph)
    {
        AppResources.Ensure();
        var state = new EmptyState
        {
            Glyph = glyph,
            Title = "No temperature data",
            Message = "CPU/GPU temperatures need supported sensors, and the CPU's also needs administrator rights.",
            Compact = compact,
        };
        state.Measure(new Size(900, double.PositiveInfinity));
        state.Arrange(new Rect(state.DesiredSize));
        state.UpdateLayout();
        return state;
    }

    private static StackPanel PanelOf(EmptyState state) => Assert.IsType<StackPanel>(state.Content);

    private static TextBlock GlyphOf(EmptyState state) =>
        Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(PanelOf(state), 0));
}
