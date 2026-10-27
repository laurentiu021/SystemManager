// SysManager · CrashDialogMessageTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the text of the last-resort crash dialog (<c>App.BuildCrashMessage</c>).
/// <para>The dialog used to show <c>Exception.Message</c> alone. For the commonest fault that reads
/// "Object reference not set to an instance of an object." — a sentence the target persona cannot act
/// on, with no mention that a log exists. The app knew exactly where the evidence was and did not
/// say, so the report arrived as "it just closed" with nothing attached.</para>
/// <para>The message is built from string literals plus one static property precisely so it can be
/// asserted here without raising a real dispatcher exception, and so the crash handler cannot fault
/// on DI or the theme — either of which may be the component that just failed.</para>
/// </summary>
public class CrashDialogMessageTests
{
    [Fact]
    public void ItSaysWhereTheDetailsWereSaved()
    {
        // The whole point of the change: the user is told the log exists and where it is.
        var text = App.BuildCrashMessage(new InvalidOperationException("something specific broke"));

        Assert.Contains(LogService.LogDir, text);
        Assert.Contains("Technical details were saved to", text);
    }

    [Fact]
    public void ItStillShowsTheUnderlyingError()
    {
        // The framework message is often the only clue a knowledgeable helper gets; keep it.
        var text = App.BuildCrashMessage(new InvalidOperationException("something specific broke"));

        Assert.Contains("something specific broke", text);
    }

    [Fact]
    public void ItSaysTheAppIsStillRunning()
    {
        // OnUi sets e.Handled = true and continues, so a dialog implying the app died would be a lie
        // — and would push the user into force-closing a working app.
        var text = App.BuildCrashMessage(new InvalidOperationException("x"));

        Assert.Contains("still running", text);
    }

    [Fact]
    public void ItPrefersTheInnerExceptionWhenTheOuterOneIsAWrapper()
    {
        // A wrapper's own message is frequently generic ("One or more errors occurred."); the inner
        // one is what actually describes the fault.
        var wrapped = new InvalidOperationException(
            "An error occurred.", new UnauthorizedAccessException("Access to the registry key is denied."));

        var text = App.BuildCrashMessage(wrapped);

        Assert.Contains("Access to the registry key is denied.", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyExceptionMessageFallsBackToSomethingReadable(string message)
    {
        // A dialog whose body is blank looks like a bug in the error handler itself.
        var text = App.BuildCrashMessage(new InvalidOperationException(message));

        Assert.Contains("An unexpected error occurred.", text);
        Assert.Contains(LogService.LogDir, text);
    }

    [Fact]
    public void ANullExceptionDoesNotThrow()
    {
        // Defensive: the crash handler must never fault while reporting a crash.
        var text = App.BuildCrashMessage(null);

        Assert.Contains("An unexpected error occurred.", text);
        Assert.Contains(LogService.LogDir, text);
    }

    [Fact]
    public void ItNamesTheFolderNotAFileSoTheUserCanFindItAfterRotation()
    {
        // Serilog rolls the log daily, so naming one file would point at a stale name tomorrow.
        var text = App.BuildCrashMessage(new InvalidOperationException("x"));

        Assert.Contains(LogService.LogDir, text);
        Assert.DoesNotContain(".log", text);
    }

    // ---- the startup variant (#2863) ----

    /// <summary>
    /// The startup message must NOT claim the app is still running, which is the sentence the running
    /// variant opens with.
    /// </summary>
    /// <remarks>
    /// That claim was shown in exactly the case where it was least true: a fault while the window was
    /// being built left no window and no tray icon, so "still running" described a process the user could
    /// only reach through Task Manager.
    /// </remarks>
    [Fact]
    public void TheStartupMessageDoesNotSayTheAppIsStillRunning()
    {
        var text = App.BuildStartupFailureMessage(new InvalidOperationException("a view model threw"));

        Assert.DoesNotContain("still running", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not start", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStartupMessageSaysWhereTheDetailsWentAndThatRetryingIsWorthIt()
    {
        var text = App.BuildStartupFailureMessage(new InvalidOperationException("a view model threw"));

        Assert.Contains(LogService.LogDir, text);
        Assert.Contains("a view model threw", text);
        // The next launch works now that the process ends, so saying so is actionable rather than a
        // dead end for someone who cannot read a stack trace.
        Assert.Contains("again", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStartupMessagePrefersTheInnerExceptionAndSurvivesANull()
    {
        var wrapped = App.BuildStartupFailureMessage(
            new InvalidOperationException("generic wrapper", new ArgumentException("the real cause")));
        Assert.Contains("the real cause", wrapped);

        var none = App.BuildStartupFailureMessage(null);
        Assert.Contains("An unexpected error occurred.", none);
        Assert.Contains(LogService.LogDir, none);
    }

    [Fact]
    public void TheStartupMessageNamesTheFolderNotAFile()
    {
        var text = App.BuildStartupFailureMessage(new InvalidOperationException("x"));

        Assert.Contains(LogService.LogDir, text);
        Assert.DoesNotContain(".log", text);
    }
}
