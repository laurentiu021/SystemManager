// SysManager · ToastService — global glass toast notifications
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Threading;
using SysManager.Helpers;

namespace SysManager.Services;

public sealed class ToastService : IToastService
{
    private static ToastService? _instance;
    public static ToastService Instance => _instance ??= new ToastService();

    private Action<string, string>? _toastRequested;

    // A toast raised before anything listened, kept for the first handler.
    private (string Title, string Detail, int AutoHideMs)? _undelivered;

    /// <summary>Raised on the UI thread with a toast's title and detail.</summary>
    /// <remarks>
    /// The first handler added is given the toast raised before any was, when there was one (#2802). The shell window
    /// subscribes only after its InitializeComponent, and the Dashboard, built inside it, reports the previous
    /// session's crash at once: that toast was raised to nobody, and the crash marker it came from was already gone,
    /// so it was never shown. The first toast is the one kept, since a start raises that one.
    /// </remarks>
    public event Action<string, string>? ToastRequested
    {
        add
        {
            _toastRequested += value;
            if (_undelivered is not { } toast) return;
            _undelivered = null;
            Raise(toast.Title, toast.Detail, toast.AutoHideMs);
        }
        remove => _toastRequested -= value;
    }

    public event Action? DismissRequested;

    private DispatcherTimer? _autoDismiss;

    /// <summary>
    /// Raises a toast with <paramref name="title"/> and <paramref name="detail"/>, dismissed automatically
    /// after <paramref name="autoHideMs"/> milliseconds unless that is zero or less.
    /// </summary>
    /// <remarks>
    /// Posted rather than marshalled synchronously. This is reached from 39 call sites, most of them
    /// the last line of an async scan on a background thread, and a caller has nothing to gain by
    /// waiting for a notification to have been raised. It was the most widely reached of the ten
    /// blocking marshals in #2152.
    /// <para>The no-window early return is kept deliberately rather than folded into
    /// <see cref="UiThread.Post(Action, DispatcherPriority)"/>. Post runs inline when there is no dispatcher, which
    /// is right for a property assignment and wrong here: there is nothing to show a toast in, and running the body
    /// would construct a <see cref="DispatcherTimer"/> on a thread whose dispatcher never pumps. No
    /// window means no toast, which is what this did before.</para>
    /// </remarks>
    public void Show(string title, string detail, int autoHideMs = 5000)
    {
        if (Application.Current?.Dispatcher is null) return;

        UiThread.Post(() => Raise(title, detail, autoHideMs));
    }

    /// <summary>
    /// Shows the toast through the handlers, on the UI thread, or keeps it for the first handler when there is none
    /// yet and none is kept already.
    /// </summary>
    internal void Raise(string title, string detail, int autoHideMs)
    {
        if (_toastRequested is not { } show)
        {
            _undelivered ??= (title, detail, autoHideMs);
            return;
        }

        _autoDismiss?.Stop();
        show(title, detail);

        if (autoHideMs > 0)
        {
            _autoDismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoHideMs) };
            _autoDismiss.Tick += (_, _) =>
            {
                _autoDismiss.Stop();
                DismissRequested?.Invoke();
            };
            _autoDismiss.Start();
        }
    }

    public void Dismiss()
    {
        _autoDismiss?.Stop();
        DismissRequested?.Invoke();
    }
}
