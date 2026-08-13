// SysManager · DialogServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="DialogService"/>. The MessageBox paths need a live WPF
/// Application and belong to UI automation, so what is pinned here is the headless
/// contract: with no Application present the service must report a safe answer rather
/// than acting on the user's behalf or throwing.
/// </summary>
// Serialized: Instance_RejectsNull touches the static DialogService.Instance. The assignment is
// rejected, so nothing is actually swapped — but the read-then-restore still races a parallel class
// that IS mid-swap, which would restore that class's substitute after it had finished with it.
[Collection("ProcessWideStatics")]
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

    // ── The focused button ──
    // Which button WPF focuses cannot be observed without a live Application, and the dialogs are
    // deliberately out of scope for unit tests (see the class summary). But the safe default is a
    // one-argument difference that compiles either way and stays invisible until someone presses Enter
    // over a file-shredder prompt — so the shipped source is what gets pinned, the same way the
    // XAML-binding guards work. All 76 confirmation call sites in the app go through Confirm.

    [Theory]
    [InlineData("public bool Confirm(", "MessageBoxResult.No")]
    [InlineData("public CloseChoice AskCloseOrMinimize(", "MessageBoxResult.Cancel")]
    public void EveryPromptFocusesItsSafeAnswer(string methodSignature, string expectedDefault)
    {
        var source = File.ReadAllText(ServiceSourcePath("DialogService.cs"));

        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{methodSignature} not found — update this guard rather than deleting it");
        var body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];

        Assert.Contains("MessageBox.Show", body);

        // MessageBox.Show without a defaultResult focuses the FIRST button, which is Yes.
        Assert.Contains(expectedDefault, body);
    }

    // Walks up from the test binaries to the app project — source is not copied to the output.
    private static string ServiceSourcePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "Services")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // else the assertions above would silently test nothing
        var path = Path.Combine(dir!.FullName, "SysManager", "Services", fileName);
        Assert.True(File.Exists(path), $"{fileName} not found at {path}");
        return path;
    }
}
