// SysManager · AdminBanner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;

namespace SysManager.Views;

/// <summary>
/// The elevation banner every privileged tab shows at the top of the page: grey with a "Run as
/// administrator" button when the session is not elevated, golden and stating what is now possible when
/// it is. Single source of truth for the golden admin-control contract, so a contrast, glyph, focus or
/// screen-reader fix lands once instead of thirty times.
/// </summary>
/// <remarks>
/// Replaced 60 hand-copied <c>Border</c> blocks across 30 views. Only the two sentences varied; the
/// brushes, radius, padding, glyph and the stripe's negative margin were identical in all of them —
/// verified by extraction before the change, not assumed.
/// <para>Same shape as <see cref="DevelopmentBanner"/> and <see cref="EmptyState"/>: a UserControl with
/// dependency properties for the parts that differ per call site, and <c>Margin</c> left to the caller so
/// both page-layout strategies still work.</para>
/// </remarks>
public partial class AdminBanner : UserControl
{
    public AdminBanner() => InitializeComponent();

    /// <summary>Why this tab needs administrator rights — shown while the session is NOT elevated.</summary>
    public static readonly DependencyProperty NotElevatedMessageProperty =
        DependencyProperty.Register(nameof(NotElevatedMessage), typeof(string), typeof(AdminBanner),
                                    new PropertyMetadata(string.Empty));

    /// <summary>What is now possible — shown while the session IS elevated.</summary>
    public static readonly DependencyProperty ElevatedMessageProperty =
        DependencyProperty.Register(nameof(ElevatedMessage), typeof(string), typeof(AdminBanner),
                                    new PropertyMetadata(string.Empty));

    public string NotElevatedMessage
    {
        get => (string)GetValue(NotElevatedMessageProperty);
        set => SetValue(NotElevatedMessageProperty, value);
    }

    public string ElevatedMessage
    {
        get => (string)GetValue(ElevatedMessageProperty);
        set => SetValue(ElevatedMessageProperty, value);
    }
}
