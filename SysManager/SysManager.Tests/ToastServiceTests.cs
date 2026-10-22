// SysManager · ToastServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for how <see cref="ToastService"/> hands a toast to the window that shows it (#2802).
/// </summary>
/// <remarks>
/// The shell window subscribes only after its InitializeComponent, and the Dashboard, built inside it, reports the
/// previous session's crash at once: that toast was raised to nobody, and the crash marker it came from was already
/// gone. Each test has a service of its own, raised directly with no auto-hide, so no dispatcher or timer is involved.
/// </remarks>
public class ToastServiceTests
{
    [Fact]
    public void AToastRaisedBeforeAnythingListens_GoesToTheFirstHandler()
    {
        var toasts = new ToastService();
        List<(string, string)> shown = [];

        toasts.Raise("SysManager closed unexpectedly last time", "details", autoHideMs: 0);
        toasts.ToastRequested += (title, detail) => shown.Add((title, detail));

        Assert.Equal([("SysManager closed unexpectedly last time", "details")], shown);
    }

    [Fact]
    public void AKeptToast_IsGivenOnce()
    {
        var toasts = new ToastService();
        var first = 0;
        var second = 0;

        toasts.Raise("title", "detail", autoHideMs: 0);
        toasts.ToastRequested += (_, _) => first++;
        toasts.ToastRequested += (_, _) => second++;

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void TheFirstToastBeforeAnythingListens_IsTheOneKept()
    {
        // A start raises that one: the previous session's crash.
        var toasts = new ToastService();
        List<string> shown = [];

        toasts.Raise("first", "", autoHideMs: 0);
        toasts.Raise("second", "", autoHideMs: 0);
        toasts.ToastRequested += (title, _) => shown.Add(title);

        Assert.Equal(["first"], shown);
    }

    [Fact]
    public void WithAHandler_AToastIsShownAtOnce()
    {
        var toasts = new ToastService();
        List<string> shown = [];
        toasts.ToastRequested += (title, _) => shown.Add(title);

        toasts.Raise("now", "", autoHideMs: 0);

        Assert.Equal(["now"], shown);
    }

    [Fact]
    public void AHandlerRemovedAgain_IsShownNothingMore()
    {
        var toasts = new ToastService();
        var shown = 0;
        Action<string, string> show = (_, _) => shown++;
        toasts.ToastRequested += show;
        toasts.ToastRequested -= show;

        toasts.Raise("after", "", autoHideMs: 0);

        Assert.Equal(0, shown);
    }
}
