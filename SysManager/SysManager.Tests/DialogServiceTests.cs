// SysManager · DialogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="DialogService"/>. The MessageBox paths need a live WPF
/// Application and belong to UI automation, so what is pinned here is the headless
/// contract: with no Application present the service must report a safe answer rather
/// than acting on the user's behalf or throwing.
/// </summary>
public class DialogServiceTests
{
    [Fact]
    public void Confirm_WithNoApplication_ReturnsFalse()
    {
        // Not treating an unanswerable prompt as consent is the whole point.
        Assert.False(new DialogService().Confirm("message", "title"));
    }

    [Fact]
    public void AskCloseOrMinimize_WithNoApplication_ReturnsCancel()
    {
        // Cancel leaves the caller's state untouched. Returning Exit here would let a
        // headless run close the app, and MinimizeToTray would silently hide it — both
        // are decisions the user never made.
        Assert.Equal(CloseChoice.Cancel, new DialogService().AskCloseOrMinimize("message", "title"));
    }

    [Fact]
    public void CloseChoice_DefaultValueIsCancel()
    {
        // A default(CloseChoice) reaching a caller — from a mock with no configured return,
        // for example — must mean "do nothing", not "exit".
        Assert.Equal(CloseChoice.Cancel, default(CloseChoice));
    }

    [Fact]
    public void CloseBehavior_DefaultValueIsAsk()
    {
        // Same reasoning for the persisted behavior: an unset value means "not chosen yet",
        // so the user gets asked rather than having an action picked for them.
        Assert.Equal(CloseBehavior.Ask, default(CloseBehavior));
    }

    [Fact]
    public void Instance_RejectsNull()
    {
        var previous = DialogService.Instance;
        try
        {
            Assert.Throws<ArgumentNullException>(() => DialogService.Instance = null!);
        }
        finally
        {
            DialogService.Instance = previous;
        }
    }
}
