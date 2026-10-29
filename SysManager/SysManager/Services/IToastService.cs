// SysManager · IToastService — abstraction for ToastService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Services;

/// <summary>
/// Abstraction for <see cref="ToastService"/>, so a view model can take it through its constructor
/// instead of reaching for <see cref="ToastService.Instance"/> directly (#2860).
/// </summary>
public interface IToastService
{
    /// <inheritdoc cref="ToastService.Show"/>
    void Show(string title, string detail, int autoHideMs = 5000);

    /// <inheritdoc cref="ToastService.Dismiss"/>
    void Dismiss();
}
