// SysManager · CrashMarkerServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Unit tests for <see cref="CrashMarkerService"/> — the record of an abnormal process exit.
/// <para>The gap this closes: a domain-level unhandled exception killed the process with no UI at
/// all, so nothing told the user (or the next launch) that the previous session ended abnormally.
/// The report arrived as "it just closed" while the details were already sitting in the log.</para>
/// <para>Every test injects a temp directory, so the developer's real marker in %LOCALAPPDATA% is
/// never read or written.</para>
/// </summary>
public class CrashMarkerServiceTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    public CrashMarkerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SysManagerCrashTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp dir must never fail a test run */ }
    }

    private CrashMarkerService NewService() => new(_dir);
    private string MarkerPath => Path.Combine(_dir, "last-crash.json");

    private void WriteMarker(CrashMarker marker) =>
        File.WriteAllText(MarkerPath, CrashMarkerService.Serialize(marker));

    private static CrashMarker Marker(DateTimeOffset when, string type = "System.NullReferenceException") =>
        new(when, "1.57.2", type, "Object reference not set to an instance of an object.");

    // ---------- the behaviour this exists for ----------

    [Fact]
    public void TakePending_WithNoMarker_IsNull()
    {
        // The overwhelmingly common case: the last run exited cleanly, so nothing is reported.
        Assert.Null(NewService().TakePending(Now));
    }

    [Fact]
    public void TakePending_AfterACrash_ReturnsTheMarker()
    {
        WriteMarker(Marker(Now.AddMinutes(-5)));

        var taken = NewService().TakePending(Now);

        Assert.NotNull(taken);
        Assert.Equal("System.NullReferenceException", taken!.ExceptionType);
        Assert.Equal("1.57.2", taken.Version);
    }

    [Fact]
    public void TakePending_ConsumesTheMarkerSoOneCrashNotifiesOnce()
    {
        // A marker that is never cleared would prompt on every launch forever — worse than the crash.
        WriteMarker(Marker(Now.AddMinutes(-5)));
        var service = NewService();

        Assert.NotNull(service.TakePending(Now));
        Assert.Null(service.TakePending(Now));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void TakePending_DeletesTheMarkerEvenWhenItIsTooOldToReport()
    {
        // Otherwise a stale marker would be re-read (and re-ignored) on every single launch.
        WriteMarker(Marker(Now.AddDays(-30)));

        Assert.Null(NewService().TakePending(Now));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void TakePending_DeletesAMalformedMarkerRatherThanRetryingForever()
    {
        File.WriteAllText(MarkerPath, "{ truncated");

        Assert.Null(NewService().TakePending(Now));
        Assert.False(File.Exists(MarkerPath));
    }

    // ---------- freshness ----------

    [Fact]
    public void IsFresh_AJustHappenedCrash_IsReported()
        => Assert.True(CrashMarkerService.IsFresh(Marker(Now.AddSeconds(-30)), Now));

    [Fact]
    public void IsFresh_AtExactlyTheRetentionEdge_IsStillReported()
        => Assert.True(CrashMarkerService.IsFresh(Marker(Now - CrashMarkerService.MaxAge), Now));

    [Fact]
    public void IsFresh_OlderThanRetention_IsNotReported()
    {
        // A crash the user has already moved on from is noise, not information.
        Assert.False(CrashMarkerService.IsFresh(
            Marker(Now - CrashMarkerService.MaxAge - TimeSpan.FromMinutes(1)), Now));
    }

    [Fact]
    public void IsFresh_AFutureDatedMarker_IsRejected()
    {
        // A clock change, or a file copied from another machine. Trusting it would report a crash
        // that has not happened.
        Assert.False(CrashMarkerService.IsFresh(Marker(Now.AddDays(1)), Now));
    }

    // ---------- parsing ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Parse_InvalidInput_IsNull(string? json)
        => Assert.Null(CrashMarkerService.Parse(json));

    [Fact]
    public void Parse_AMarkerWithNoTimestamp_IsDropped()
    {
        // Without a timestamp, freshness cannot be judged — so the marker is not trustworthy.
        var json = """{"Version":"1.57.2","ExceptionType":"System.Exception","Message":"boom"}""";

        Assert.Null(CrashMarkerService.Parse(json));
    }

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var original = Marker(Now.AddHours(-2));

        var parsed = CrashMarkerService.Parse(CrashMarkerService.Serialize(original));

        Assert.Equal(original, parsed);
    }

    // ---------- what the writer and the reader agree on ----------

    [Fact]
    public void TheCrashHandlerWritesSomethingThisServiceCanRead()
    {
        // The writer lives in App.OnDomain and the reader here; if they drifted, every crash would be
        // recorded into a file that parses to nothing and nobody would ever be told. Asserting they
        // agree is the point — hence the shared CrashMarker record rather than an anonymous object.
        var written = App.BuildCrashMarker(new InvalidOperationException("kaboom"), Now);

        var parsed = CrashMarkerService.Parse(written);

        Assert.NotNull(parsed);
        Assert.Equal("System.InvalidOperationException", parsed!.ExceptionType);
        Assert.Equal("kaboom", parsed.Message);
        Assert.Equal(Now, parsed.WhenUtc);
    }

    [Fact]
    public void TheCrashHandlerHandlesANullException()
    {
        // UnhandledExceptionEventArgs.ExceptionObject is typed as object and is not guaranteed to be
        // an Exception, so the cast in OnDomain can legitimately yield null.
        var written = App.BuildCrashMarker(null, Now);

        var parsed = CrashMarkerService.Parse(written);

        Assert.NotNull(parsed);
        Assert.Equal("(unknown)", parsed!.ExceptionType);
    }

    [Fact]
    public void TheMarkerCarriesNoStackTraceOrFilePath()
    {
        // It exists to answer "did the last run crash?", not to duplicate the log — and unlike the
        // log, this file is not scrubbed of the user name, so it must never carry a path.
        Exception caught;
        try { throw new IOException(@"Cannot open C:\Users\someone\Documents\x.txt"); }
        catch (IOException ex) { caught = ex; }

        var written = App.BuildCrashMarker(caught, Now);

        Assert.DoesNotContain("StackTrace", written, StringComparison.OrdinalIgnoreCase);
        // The message itself is preserved verbatim, but nothing beyond it is added.
        Assert.DoesNotContain("   at ", written);
    }

    // ---------- the sentence the user actually sees ----------

    [Fact]
    public void DescribeForUser_NamesNeitherTheExceptionTypeNorAPath()
    {
        // The target persona can act on neither. It points at the Logs tab instead, which is a place
        // in the app she can actually reach.
        var text = CrashMarkerService.DescribeForUser(Marker(Now.AddMinutes(-5)));

        Assert.DoesNotContain("NullReferenceException", text);
        Assert.DoesNotContain(@"\", text);
        Assert.Contains("closed unexpectedly", text);
        Assert.Contains("System Logs", text);
    }

    [Fact]
    public void DescribeForUser_MentionsWhenItHappened()
    {
        // "It crashed at some point" is not actionable; the user needs to recognise the session.
        var text = CrashMarkerService.DescribeForUser(Marker(new DateTimeOffset(2026, 8, 3, 9, 30, 0, TimeSpan.Zero)));

        Assert.Contains("August", text);
    }
}
