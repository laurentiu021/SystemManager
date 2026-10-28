// SysManager · ClipboardFeedbackTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ClipboardFeedback"/>: the one wording every "Copy" button across the app shows
/// for the busy-clipboard case, proved through the injectable seam so no test needs another process
/// actually holding the real OS clipboard (#2859).
/// </summary>
public class ClipboardFeedbackTests
{
    [Fact]
    public void TrySetText_WhenTheWriteSucceeds_ReturnsTrue()
        => Assert.True(ClipboardFeedback.TrySetText("hello", _ => { }));

    [Theory]
    [InlineData(typeof(ExternalException))]
    [InlineData(typeof(ThreadStateException))]
    [InlineData(typeof(InvalidOperationException))]
    public void TrySetText_OnEachDocumentedFailure_ReturnsFalse(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.False(ClipboardFeedback.TrySetText("hello", _ => throw ex));
    }

    [Fact]
    public void TrySetText_OnAnUndocumentedFailure_StillThrows()
        // Only the three documented Clipboard.SetText exceptions are caught — a defect in the caller's
        // own text-building code must not be swallowed and misreported as "the clipboard is busy".
        => Assert.Throws<ArgumentNullException>(
            () => ClipboardFeedback.TrySetText("hello", _ => throw new ArgumentNullException()));

    [Fact]
    public void TrySetTextAndDescribe_OnSuccess_ReturnsCopied()
    {
        // TrySetTextAndDescribe always calls the real clipboard (it has no seam parameter), so this can
        // only assert the two outcomes agree with each other, not force a specific one.
        var text = $"ClipboardFeedbackTests {Guid.NewGuid()}";
        var described = ClipboardFeedback.TrySetTextAndDescribe(text);

        Assert.True(described is ClipboardFeedback.Copied or ClipboardFeedback.Busy);
    }

    [Fact]
    public void BusyAndCopied_AreDifferentSentences()
        // The defect this guards: every tab's own wording collapsing to one undifferentiated string,
        // or the busy case silently reusing the success sentence.
        => Assert.NotEqual(ClipboardFeedback.Copied, ClipboardFeedback.Busy);
}
