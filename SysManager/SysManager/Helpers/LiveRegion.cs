// SysManager · LiveRegion
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SysManager.Helpers;

/// <summary>
/// Tells screen readers that a live line's text has changed, which WPF never does on its own (#2670).
/// </summary>
/// <remarks>
/// <c>AutomationProperties.LiveSetting</c> marks a line as a live region, but a screen reader only speaks one when the
/// app raises UI Automation's <c>LiveRegionChanged</c> event for it, and WPF raises that for no control: of the 34
/// calls to <see cref="AutomationPeer.RaiseAutomationEvent"/> in WPF's own assemblies, none is that event. So every
/// status line marked live stayed silent. Set on a line,
/// <code>helpers:LiveRegion.Announces="True"</code>
/// raises it each time the line's text changes to something to say. The <c>StatusLine</c> and
/// <c>SubtleStatusLine</c> styles set it beside the live setting itself.
/// <para>Only a change made while the line is loaded is announced. What it says before that is what the tab shows
/// when it opens, not news, and a tab that has been left keeps quiet. An empty text is not announced either. The event
/// goes out at <see cref="DispatcherPriority.Background"/>, after the bindings that change together have all landed and
/// the line is laid out, so a line that appears with its first message is announced, and several changes in one pass
/// are announced once, as the last of them. It goes out only for a line on screen whose live setting is on, so a
/// fast readout with the setting off says nothing.</para>
/// </remarks>
public static class LiveRegion
{
    /// <summary>Turns announcing on or off for the <see cref="TextBlock"/> it is set on.</summary>
    public static readonly DependencyProperty AnnouncesProperty = DependencyProperty.RegisterAttached(
        "Announces", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnAnnouncesChanged));

    /// <summary>
    /// For a test, which has no window to load a line in: the line it is set on counts as loaded. Nothing in the app
    /// sets it.
    /// </summary>
    internal static readonly DependencyProperty LoadedForTestProperty = DependencyProperty.RegisterAttached(
        "LoadedForTest", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    /// <summary>The line's text, followed through a binding to it, so a change calls back here.</summary>
    private static readonly DependencyProperty WatchedTextProperty = DependencyProperty.RegisterAttached(
        "WatchedText", typeof(string), typeof(LiveRegion), new PropertyMetadata(null, OnWatchedTextChanged));

    /// <summary>The announcement waiting for its pass, so changes in one pass become one announcement.</summary>
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(DispatcherOperation), typeof(LiveRegion), new PropertyMetadata(null));

    /// <summary>Set while the binding that watches the text is attached, whose first read is not a change.</summary>
    private static readonly DependencyProperty AttachingProperty = DependencyProperty.RegisterAttached(
        "Attaching", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    public static bool GetAnnounces(TextBlock line) => (bool)line.GetValue(AnnouncesProperty);
    public static void SetAnnounces(TextBlock line, bool value) => line.SetValue(AnnouncesProperty, value);

    /// <summary>
    /// Announces <paramref name="element"/> in the next pass, whether or not its text changed, for a message shown again
    /// with the same words, as the toast is.
    /// </summary>
    public static void Announce(FrameworkElement element)
    {
        if (element.GetValue(PendingProperty) is not null) return;
        element.SetValue(PendingProperty,
            element.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Raise(element))));
    }

    /// <summary>The announcement waiting for its pass on <paramref name="element"/>, or null when none is.</summary>
    internal static DispatcherOperation? PendingOf(FrameworkElement element) =>
        element.GetValue(PendingProperty) as DispatcherOperation;

    /// <summary>Whether a line whose text changed to <paramref name="text"/> has something to say.</summary>
    internal static bool IsNews(string? text) => !string.IsNullOrWhiteSpace(text);

    /// <summary>Whether an element with <paramref name="setting"/>, shown or not, is announced when its pass comes.</summary>
    internal static bool IsHeard(AutomationLiveSetting setting, bool shown) => shown && setting != AutomationLiveSetting.Off;

    private static bool IsLoaded(FrameworkElement element) =>
        element.IsLoaded || (bool)element.GetValue(LoadedForTestProperty);

    private static void OnAnnouncesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock line) return;

        if (e.NewValue is true)
        {
            line.SetValue(AttachingProperty, true);
            try
            {
                BindingOperations.SetBinding(line, WatchedTextProperty,
                    new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self, Mode = BindingMode.OneWay });
            }
            finally
            {
                line.ClearValue(AttachingProperty);
            }

            return;
        }

        BindingOperations.ClearBinding(line, WatchedTextProperty);
        (line.GetValue(PendingProperty) as DispatcherOperation)?.Abort();
        line.ClearValue(PendingProperty);
    }

    private static void OnWatchedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock line && !(bool)line.GetValue(AttachingProperty) && IsLoaded(line)
            && IsNews(e.NewValue as string))
            Announce(line);
    }

    private static void Raise(FrameworkElement element)
    {
        element.ClearValue(PendingProperty);
        if (!IsHeard(AutomationProperties.GetLiveSetting(element), element.IsVisible)) return;
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;

        UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
