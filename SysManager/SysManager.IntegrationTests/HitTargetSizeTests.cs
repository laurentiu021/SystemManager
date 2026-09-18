// SysManager · HitTargetSizeTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.IntegrationTests;

/// <summary>
/// Every button style measures at least 24 x 24, including at the padding the row actions use.
/// </summary>
/// <remarks>
/// <c>ButtonBase</c> set no minimum size, so a button was exactly its padding plus its content — and 26
/// row-action buttons cut their padding to <c>4,2</c> or <c>6,1</c> with <c>FontSize</c> 10-12 to fit inside a
/// DataGrid row. Measured, not computed: the "X" that KILLS A PROCESS came out at 15.4 x 20.0 logical pixels
/// and the "?" on Ping at 16.4 x 15.3, against the 24 x 24 WCAG 2.5.8 AA asks for (#1556). The original
/// estimate in that issue was "roughly 20x20", which understated the width failure.
/// <para><b>Measured rather than asserted on the setters.</b> A test reading <c>MinWidth</c> off the style
/// would pass while a derived style, a template, or a <c>Padding</c> override made the rendered box smaller —
/// and the rendered box is the thing a user has to hit. This applies the real style to a real
/// <see cref="Button"/> on the STA thread with the app's dictionaries loaded and reads
/// <c>DesiredSize</c>.</para>
/// <para>The floor asserted here is the WCAG number, 24, deliberately lower than the 28 the style sets. The
/// guard exists to catch a REGRESSION below the accessible minimum, not to pin the exact value — pinning 28
/// would fail on a deliberate change to 32 that improves the same property.</para>
/// </remarks>
public class HitTargetSizeTests
{
    /// <summary>WCAG 2.5.8 AA: a target must be at least 24 by 24 CSS pixels.</summary>
    private const double MinimumTarget = 24d;

    /// <summary>
    /// The padding and font size combinations the row actions actually use, taken from the views.
    /// </summary>
    /// <remarks>
    /// Real shapes rather than invented ones. <c>4,2</c> at 12 is Process Manager's kill button and Disk
    /// Analyzer's drill-down; <c>6,1</c> at 10 is Ping's help glyph, the smallest control in the app; the
    /// single-character contents are what make a button narrow, so they are what the width has to survive.
    /// </remarks>
    public static TheoryData<string, double, double, string> RowActionShapes() => new()
    {
        { "PrimaryButton", 4, 2, "X" },
        { "SecondaryButton", 4, 2, "X" },
        { "GhostButton", 4, 2, "→" },
        { "DangerButton", 4, 2, "X" },
        { "GhostButton", 6, 1, "?" },
        { "SecondaryButton", 6, 2, "→" },
        { "DangerButton", 6, 1, "?" },
        // AdminButton is not used at row-action padding today, and is here because it measured 47.5 x 23.3
        // when probed at 4,2 — under the minimum on HEIGHT, which is easy to miss on a control that is wide.
        // All five styles are BasedOn ButtonBase, so all five inherit the floor; this is the one that proves
        // the inheritance rather than assuming it.
        { "AdminButton", 4, 2, "X" },
    };

    [Theory]
    [MemberData(nameof(RowActionShapes))]
    public void ARowActionButton_IsAtLeastTheAccessibleMinimum(
        string styleKey, double padX, double padY, string content)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var style = Application.Current!.Resources[styleKey] as Style;
            Assert.NotNull(style);

            var button = new Button
            {
                Style = style,
                Content = content,
                Padding = new Thickness(padX, padY, padX, padY),
                FontSize = padY <= 1 ? 10 : 12,
            };
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var size = button.DesiredSize;
            Assert.True(size.Width >= MinimumTarget && size.Height >= MinimumTarget,
                $"{styleKey} with Padding=\"{padX},{padY}\" and content \"{content}\" measures "
                + $"{size.Width:F1} x {size.Height:F1}, under the {MinimumTarget} x {MinimumTarget} WCAG "
                + "2.5.8 AA asks of a pointer target. The floor lives on ButtonBase in App.xaml — check "
                + "whether a derived style or this call site is overriding MinWidth/MinHeight downward.");
        });
    }

    /// <summary>
    /// An explicit <c>Width</c>/<c>Height</c> smaller than the floor does not win.
    /// </summary>
    /// <remarks>
    /// WPF resolves a size as <c>Max(MinWidth, Min(MaxWidth, Width))</c>, so the floor clamps an explicit
    /// size upward rather than being overridden by it. Asserted rather than assumed, because the opposite
    /// belief is the intuitive one — and one button in the app relies on the answer: the sidebar's
    /// clear-search glyph was <c>Width="20" Height="20"</c>, overlaid inside a TextBox that reserved exactly
    /// 22px of right padding for it. If the floor did NOT apply it would still be a 20 x 15 target; because
    /// it does, the reserved padding had to grow with it, which is the change that accompanies this test.
    /// </remarks>
    [Fact]
    public void AnExplicitlySizedSmallButton_IsStillRaisedToTheFloor()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var style = Application.Current!.Resources["GhostButton"] as Style;
            Assert.NotNull(style);

            var button = new Button
            {
                Style = style,
                Content = "",
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 10,
                Width = 20,
                Height = 20,
            };
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var size = button.DesiredSize;
            Assert.True(size.Width >= MinimumTarget && size.Height >= MinimumTarget,
                $"a button with an explicit Width/Height of 20 measures {size.Width:F1} x {size.Height:F1}. "
                + "If the explicit size wins over MinWidth/MinHeight, then the floor on ButtonBase does not "
                + "protect any button that sets its own size — and the sidebar's clear-search glyph is one.");
        });
    }

    /// <summary>
    /// The ordinary buttons are unaffected: the floor never binds on them.
    /// </summary>
    /// <remarks>
    /// The other half of the change. A minimum size is only free if it does not reshape the buttons that were
    /// already large enough, and "already large enough" was measured at 60 x 33 and up. If a future floor were
    /// raised past that, this fails and says so, rather than the density change being discovered on screen.
    /// </remarks>
    [Theory]
    [InlineData("PrimaryButton")]
    [InlineData("SecondaryButton")]
    [InlineData("GhostButton")]
    [InlineData("DangerButton")]
    public void AnOrdinaryButton_IsStillSizedByItsContent(string styleKey)
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            var style = Application.Current!.Resources[styleKey] as Style;
            Assert.NotNull(style);

            var button = new Button { Style = style, Content = "Check for updates" };
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var size = button.DesiredSize;
            Assert.True(size.Width > 100,
                $"{styleKey} with a full label measures {size.Width:F1} wide — the minimum-size floor is now "
                + "wider than the content, so it is reshaping buttons rather than only rescuing small ones.");
            Assert.True(size.Height is > 28 and < 44,
                $"{styleKey} measures {size.Height:F1} tall. Above 44 means the floor has grown into the "
                + "normal buttons and every toolbar in the app just got taller.");
        });
    }
}
