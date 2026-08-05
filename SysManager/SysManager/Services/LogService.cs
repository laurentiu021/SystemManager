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

    public static string LogDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager", "logs");

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

    public static void Init()
    {
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
                retainedFileCountLimit: 14))
            .CreateLogger();
        Log.Logger = Logger;
        Logger.Information("SysManager started");
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
