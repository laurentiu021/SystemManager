// SysManager · ToastService — global glass toast notifications
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Threading;

namespace SysManager.Services;

public sealed class ToastService
{
    private static ToastService? _instance;
    public static ToastService Instance => _instance ??= new ToastService();

    public event Action<string, string>? ToastRequested;
    public event Action? DismissRequested;

    private DispatcherTimer? _autoDismiss;

    public void Show(string title, string detail, int autoHideMs = 5000)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;

        dispatcher.Invoke(() =>
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
