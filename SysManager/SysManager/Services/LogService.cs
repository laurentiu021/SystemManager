// SysManager · LogService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;

namespace SysManager.Services;

public static class LogService
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
        // Fallback: match any drive letter followed by \Users\<username>
        return new Regex(@"(?i)([A-Z]:\\Users\\)[^\\]+", RegexOptions.Compiled);
    }

    public static void Init()
    {
        Directory.CreateDirectory(LogDir);
        Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDir, "sysmanager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Logger = Logger;
        Logger.Information("SysManager started");
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
