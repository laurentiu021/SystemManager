// SysManager · FirstRunService — remembers that the Welcome screen has been seen on this PC
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// Whether the one-time Welcome screen has been seen on this PC (#1635).
/// </summary>
/// <remarks>
/// The app used to open straight onto the Dashboard's live sensors, with nothing anywhere saying what it is or that
/// it is safe. The Welcome screen says both once, and this is what keeps it to once.
/// </remarks>
public interface IFirstRunService
{
    /// <summary>Whether the Welcome screen has been seen here before, so it is not shown again.</summary>
    bool HasSeenWelcome();

    /// <summary>Records that the Welcome screen has been seen. Returns false when the record could not be written.</summary>
    bool MarkWelcomeSeen();
}

/// <summary>The record behind <see cref="IFirstRunService"/>, in <c>welcome.json</c>.</summary>
/// <remarks>
/// Local AppData rather than Roaming, and left out of Profile Export / Import: "seen here" is about this PC, and a
/// profile carried to a new one should not keep its first launch from saying what the app is.
/// <para>The file being there is the record. Whatever it holds, only the app writes it, and it writes it when the
/// Welcome screen goes, so a file that does not parse still means the screen was seen; reading it as unseen would
/// show it again on every launch for as long as the file stayed damaged. What it holds is when, which is all there
/// is to say.</para>
/// </remarks>
public sealed class FirstRunService : IFirstRunService
{
    /// <summary>The file name, beside the other per-user state.</summary>
    internal const string FileName = "welcome.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly TimeProvider _clock;

    /// <summary>The record production uses, in the user's profile.</summary>
    public FirstRunService() : this(null) { }

    /// <summary>A record kept under <paramref name="configDir"/>, so a test points it at a temp folder.</summary>
    internal FirstRunService(string? configDir, TimeProvider? clock = null)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _filePath = Path.Join(dir, FileName);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The file the record is kept in.</summary>
    internal string FilePath => _filePath;

    public bool HasSeenWelcome() => File.Exists(_filePath);

    public bool MarkWelcomeSeen()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicFile.WriteAllText(_filePath, Serialize(_clock.GetUtcNow()));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Welcome screen not recorded as seen: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>The record as the file holds it.</summary>
    private sealed record Stored(DateTimeOffset SeenUtc);

    /// <summary>The record for a Welcome screen seen at <paramref name="seenUtc"/>, as indented JSON.</summary>
    internal static string Serialize(DateTimeOffset seenUtc) =>
        JsonSerializer.Serialize(new Stored(seenUtc), JsonOptions);
}
