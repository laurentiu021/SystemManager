// SysManager · ToastService — global glass toast notifications
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Threading;
using SysManager.Helpers;

namespace SysManager.Services;

public sealed class ToastService
{
    private static ToastService? _instance;
    public static ToastService Instance => _instance ??= new ToastService();

    public event Action<string, string>? ToastRequested;
    public event Action? DismissRequested;

    private DispatcherTimer? _autoDismiss;

    /// <remarks>
    /// Posted rather than marshalled synchronously. This is reached from 39 call sites, most of them
    /// the last line of an async scan on a background thread, and a caller has nothing to gain by
    /// waiting for a notification to have been raised. It was the most widely reached of the ten
    /// blocking marshals in #2152.
    /// <para>The no-window early return is kept deliberately rather than folded into
    /// <see cref="UiThread.Post"/>. Post runs inline when there is no dispatcher, which is right for a
    /// property assignment and wrong here: there is nothing to show a toast in, and running the body
    /// would construct a <see cref="DispatcherTimer"/> on a thread whose dispatcher never pumps. No
    /// window means no toast, which is what this did before.</para>
    /// </remarks>
    public void Show(string title, string detail, int autoHideMs = 5000)
    {
        if (Application.Current?.Dispatcher is null) return;

        UiThread.Post(() =>
        {
            _autoDismiss?.Stop();
            ToastRequested?.Invoke(title, detail);

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
        });
    }

    public void Dismiss()
    {
        _autoDismiss?.Stop();
        DismissRequested?.Invoke();
    }
}
