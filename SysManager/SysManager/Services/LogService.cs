// SysManager · LogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace SysManager.Services;

public static partial class LogService
{
    public static Logger? Logger { get; private set; }

    /// <summary>
    /// Per-file ceiling for the rolling log. With <see cref="RetainedFileCount"/> this bounds the
    /// whole folder at roughly 140 MB, and keeps any single file small enough to attach to a bug
    /// report — which is the documented way a user sends us evidence.
    /// </summary>
    internal const long MaxLogFileBytes = 10L * 1024 * 1024;

    /// <summary>How many rolled files to keep. Combines with <see cref="MaxLogFileBytes"/>.</summary>
    internal const int RetainedFileCount = 14;

    /// <summary>
    /// First line of every log file. Held as a constant because two other things depend on its exact
    /// shape: a bug report arrives as an attached log (SUPPORT.md), so the file has to name the build
    /// it came from, and the release workflow greps this line to prove the published binary reports
    /// the tag it was built from — the managed assembly version is not in the Win32 version resource
    /// and cannot be read out of a single-file bundle without running it.
    /// </summary>
    internal const string StartupMessage = "SysManager {Version} started";

    /// <summary>
    /// Where the rolling log is written. Settable only through <see cref="Init(string?)"/>, and only
    /// before the sink exists.
    /// </summary>
    /// <remarks>
    /// This was <c>static readonly</c>, which made it the last entry on the user-data-path ratchet: a
    /// resolved path in static state cannot be pointed at a temp directory by any test, because
    /// <see cref="Environment.GetFolderPath"/> resolves through the Win32 known-folder function and
    /// ignores the <c>LOCALAPPDATA</c> environment variable. That is not a hypothetical — a service
    /// holding its path this way had tests that wrote into the user's real speed-test history.
    /// <para>The usual fix, a constructor-injected <c>string? configDir = null</c>, does not apply here:
    /// this is a static class because Serilog's sink is configured once per process, so there is no
    /// instance to hang a parameter on. The seam is <see cref="Init(string?)"/> instead.</para>
    /// </remarks>
    public static string LogDir { get; private set; } = ResolveLogDir();

    /// <summary>
    /// Decides the log directory for a call to <see cref="Init(string?)"/>, or refuses. Also produces the
    /// default, so there is one entry point rather than a resolver beside a parameterless helper.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Init(string?)"/> and pure, so the refusal can be tested without building
    /// a real Serilog sink and assigning the global <c>Log.Logger</c> — a test that did that would leak
    /// into every other test in the run.
    /// <para>Both parameters are optional so this is also what produces the default, which is what
    /// <c>StaticPathMethods_AcceptARedirect</c> requires of any static member that can resolve a path
    /// under the user profile: a default is fine, having no parameter to override is not. A separate
    /// parameterless <c>DefaultLogDir()</c> failed that rule, and giving it a parameter nobody would pass
    /// would have satisfied the letter of it and nothing else.</para>
    /// <para>Redirecting after the sink exists is refused rather than ignored. Ignoring it would leave
    /// <see cref="LogDir"/> naming one directory while the sink wrote to another, and every reader of
    /// that property — the crash dialog, the About tab, a support bundle — would point a user at an
    /// empty folder. Failing loudly at the one call site is cheaper than that.</para>
    /// </remarks>
    internal static string ResolveLogDir(string? requested = null, bool loggerExists = false)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (loggerExists)
            {
                throw new InvalidOperationException(
                    "The log directory cannot be changed once the sink is writing: LogDir would name one "
                    + "directory while the log went to another. Pass it on the first Init call instead.");
            }

            return requested;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager", "logs");
    }

    // Dynamically build regex from the actual user profile parent directory
    // (e.g. C:\Users) so it works even if Windows is installed on a non-standard
    // drive or the Users folder has a custom path.
    private static readonly Regex UserPathRegex = BuildUserPathRegex();

    private static Regex BuildUserPathRegex()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            var usersDir = Path.GetDirectoryName(userProfile);
            if (!string.IsNullOrEmpty(usersDir))
            {
                var escaped = Regex.Escape(usersDir + @"\");
                return new Regex($@"(?i)({escaped})[^\\]+", RegexOptions.Compiled);
            }
        }
        // Fallback: match any drive letter followed by \Users\<username>.
        // This branch is a constant pattern, so it is source-generated.
        return FallbackUserPathRegex();
    }

    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\]+")]
    private static partial Regex FallbackUserPathRegex();

    /// <summary>
    /// Builds the rolling log sink and publishes it as both <see cref="Logger"/> and Serilog's global
    /// <see cref="Log.Logger"/>. Also the only way to set <see cref="LogDir"/>.
    /// </summary>
    /// <param name="logDir">
    /// Where to write. Null keeps the per-user default. Supplied only by a test that must not touch the
    /// real log directory; the app's two call sites in <c>App.OnStartup</c> pass nothing, and they are
    /// mutually exclusive, so this runs exactly once per process.
    /// </param>
    public static void Init(string? logDir = null)
    {
        LogDir = ResolveLogDir(logDir, Logger is not null);
        Directory.CreateDirectory(LogDir);
        // Scrub the user name centrally, on the way to the file. SanitizePath already existed and
        // was applied at 15 call sites, while 60 others logged a raw path — so whether the user's
        // name reached the log depended on which service happened to fail. Sanitizing at the sink
        // covers every existing call site, every future one, and (unlike a per-call-site fix or a
        // property enricher) also the exception text, which Serilog renders separately from the
        // message properties.
        Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new UserPathScrubbingSink(
                Path.Combine(LogDir, "sysmanager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount))
            .CreateLogger();
        Log.Logger = Logger;
        // The same source the About tab and the updater read, so a log, a crash marker and a release
        // can all be matched to one another rather than guessed at.
        Logger.Information(StartupMessage, UpdateService.CurrentVersion.ToString(3));
    }

    /// <summary>
    /// Writes to the rolling log file with the Windows user name removed from the whole rendered
    /// line — message, properties, and exception text alike.
    /// <para>Sanitizing here rather than at each call site means a new <c>Log.Information(... path
    /// ...)</c> anywhere in the app is covered by default, instead of relying on whoever writes it
    /// to remember <see cref="SanitizePath"/>. It also catches paths inside exception messages,
    /// which no call-site fix can reach because the exception is rendered by the sink.</para>
    /// </summary>
    private sealed class UserPathScrubbingSink : ILogEventSink, IDisposable
    {
        private const string Template =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        // Both are stateless and thread-safe once constructed, and Emit runs on every log call —
        // building them per event allocated a formatter and re-parsed a constant template each
        // time, for no benefit.
        private static readonly MessageTemplateTextFormatter Formatter =
            new(Template, CultureInfo.InvariantCulture);

        private static readonly MessageTemplate LineTemplate =
            new MessageTemplateParser().Parse("{Line}");

        private readonly Logger _inner;

        public UserPathScrubbingSink(
            string path, RollingInterval rollingInterval, int retainedFileCountLimit)
        {
            _inner = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    path,
                    rollingInterval: rollingInterval,
                    retainedFileCountLimit: retainedFileCountLimit,
                    // Bound each file AND roll when it fills. Without both, the sink took Serilog's
                    // defaults — 1 GB per file with rollOnFileSizeLimit false — so a single daily
                    // file could grow to a gigabyte with nothing rolling below it, and the only
                    // bound on the folder was the 14-FILE count. That matters because the whole
                    // support path is "attach the log" (SUPPORT.md, the bug-report template): a file
                    // too large to upload breaks the evidence trail exactly when it is needed, and
                    // Debug is a real volume tier here (290 Log.Debug call sites, some in loops).
                    fileSizeLimitBytes: MaxLogFileBytes,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Message:lj}{NewLine}")
                .CreateLogger();
        }

        public void Emit(LogEvent logEvent)
        {
            // Render the event exactly as the file sink would, scrub the finished line, then write
            // it as a single pre-formatted message. Rendering first is what makes the exception
            // text reachable; scrubbing the rendered string is also cheaper than walking a
            // structured tree, and cannot miss a nested or destructured value.
            string scrubbed;
            using (var writer = new StringWriter())
            {
                Formatter.Format(logEvent, writer);
                scrubbed = SanitizePath(writer.ToString().TrimEnd('\r', '\n'));
            }

            // Written through Verbose so the inner logger never re-filters an event the outer
            // logger already allowed, and as a literal to keep any braces in the text from being
            // read as a new message template.
            _inner.Write(new LogEvent(
                logEvent.Timestamp,
                LogEventLevel.Verbose,
                exception: null,
                LineTemplate,
                [new LogEventProperty("Line", new ScalarValue(scrubbed))]));
        }

        public void Dispose() => _inner.Dispose();
    }

    public static void Shutdown()
    {
        Logger?.Information("SysManager shutting down");
        Logger?.Dispose();
    }

    /// <summary>
    /// Replaces the Windows username in file paths with [user] to avoid
    /// logging personal data.
    /// </summary>
    public static string SanitizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return UserPathRegex.Replace(path, "$1[user]");
    }
}
