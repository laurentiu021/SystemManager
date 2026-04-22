// SysManager · LogService
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using Serilog;
using Serilog.Core;

namespace SysManager.Services;

public static class LogService
{
    public static Logger? Logger { get; private set; }

    public static string LogDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager", "logs");

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
        Logger.Information("SysManager started. Log dir: {Dir}", LogDir);
    }

    public static void Shutdown()
    {
        Logger?.Information("SysManager shutting down");
        Logger?.Dispose();
    }
}
