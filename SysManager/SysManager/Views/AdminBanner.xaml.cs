// SysManager · AdminBanner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Controls;
using SysManager.Helpers;

namespace SysManager.Views;

/// <summary>
/// The elevation banner every privileged tab shows at the top of the page: grey with a "Run as
/// administrator" button when the session is not elevated, golden and stating what is now possible when
/// it is. Single source of truth for the golden admin-control contract, so a contrast, glyph, focus or
/// screen-reader fix lands once instead of once per tab.
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

    /// <summary>
    /// True when SysManager is running elevated as an account DIFFERENT from the one signed in to the
    /// active console session — the family-PC case where "Run as administrator" used a parent's
    /// password, and a per-user switch changed here would otherwise land in the administrator's own
    /// profile instead of the signed-in person's (#2826).
    /// </summary>
    /// <remarks>
    /// Read directly here rather than bound ambiently per view model like <c>IsElevated</c>: it is a fact
    /// about the PROCESS, identical at every one of this control's 30-plus call sites, so exposing it on
    /// the banner itself applies everywhere with no per-tab wiring. Fixed for the life of the process, the
    /// same assumption <see cref="AdminHelper.ElevationProbe"/>'s own remarks make for elevation itself, so
    /// this needs no change notification.
    /// </remarks>
    public bool IsElevatedAsAnotherUser => AdminHelper.IsElevatedAsAnotherUser();
}
