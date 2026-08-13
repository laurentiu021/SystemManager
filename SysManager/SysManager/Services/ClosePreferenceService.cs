// SysManager · ClosePreferenceService — remembers what the window's X button should do
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;

namespace SysManager.Services;

/// <summary>
/// What the window close button should do, once the user has been asked.
/// </summary>
public enum CloseBehavior
{
    /// <summary>Not chosen yet — ask on the next close.</summary>
    Ask,

    /// <summary>Keep running in the notification area.</summary>
    MinimizeToTray,

    /// <summary>Exit the application.</summary>
    Exit
}

/// <summary>
/// What closing the window must actually do, resolved from the remembered preference and the user's
/// answer to the prompt.
/// </summary>
public enum CloseAction
{
    /// <summary>Leave the window open — the user cancelled.</summary>
    KeepOpen,

    /// <summary>Hide the window and keep running in the notification area.</summary>
    HideToTray,

    /// <summary>
    /// End the process. This is a SHUTDOWN, not just a window close: <c>App</c> sets
    /// <c>ShutdownMode.OnExplicitShutdown</c> so SysManager can live in the tray, which means closing
    /// the last window does not exit on its own.
    /// </summary>
    ExitApplication
}

/// <summary>
/// Resolves what the close button does. Pure and separate from the window so the decision is
/// assertable — the defect this exists to prevent was a MISSING shutdown on the Exit branch, which no
/// test could see while the logic lived inside <c>MainWindow.OnClosing</c>.
/// </summary>
public static class CloseDecision
{
    /// <summary>
    /// Resolves the remembered <paramref name="behavior"/> — consulting <paramref name="answer"/> only
    /// when nothing has been remembered yet — into the action the window must take.
    /// </summary>
    /// <param name="behavior">The stored preference.</param>
    /// <param name="answer">
    /// The user's answer to the prompt, or <c>null</c> when no prompt was shown because the preference
    /// was already known.
    /// </param>
    public static CloseAction Resolve(CloseBehavior behavior, CloseChoice? answer) => behavior switch
    {
        CloseBehavior.MinimizeToTray => CloseAction.HideToTray,
        CloseBehavior.Exit => CloseAction.ExitApplication,
        // Ask: the answer decides. A null answer here means the prompt could not be shown, and the safe
        // reading of "we do not know what the user wants" is to leave the window open rather than to
        // exit — losing a window is recoverable, exiting unasked is not.
        _ => answer switch
        {
            CloseChoice.MinimizeToTray => CloseAction.HideToTray,
            CloseChoice.Exit => CloseAction.ExitApplication,
            _ => CloseAction.KeepOpen,
        },
    };

    /// <summary>
    /// The preference to persist for an answered prompt, or <c>null</c> when nothing should be saved
    /// (the user cancelled, so they have not chosen anything yet and must be asked again).
    /// </summary>
    public static CloseBehavior? PreferenceToSave(CloseChoice answer) => answer switch
    {
        CloseChoice.MinimizeToTray => CloseBehavior.MinimizeToTray,
        CloseChoice.Exit => CloseBehavior.Exit,
        _ => null,
    };
}

/// <summary>
/// Persists the user's answer to the close prompt so it is asked once rather than on
/// every close. Stored next to the other per-user state in
/// <c>%LOCALAPPDATA%\SysManager</c>, following the same load/persist shape as
/// <see cref="VolumePresetService"/>: never throws, degrades to <see cref="CloseBehavior.Ask"/>
/// when the file is missing, unreadable, or malformed.
/// </summary>
public sealed class ClosePreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates the service. <paramref name="configDir"/> is overridable for tests.</summary>
    public ClosePreferenceService(string? configDir = null)
    {
        var dir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _path = Path.Combine(dir, "close-preference.json");
    }

    /// <summary>The stored state to serialize. A record so the JSON shape stays explicit.</summary>
    private sealed record Stored(string Behavior);

    /// <summary>
    /// Loads the saved behavior. Returns <see cref="CloseBehavior.Ask"/> when nothing has
    /// been saved yet or the file cannot be trusted — asking again is always safe, whereas
    /// guessing could exit an app the user wanted kept running.
    /// </summary>
    public CloseBehavior Load()
    {
        try
        {
            if (!File.Exists(_path)) return CloseBehavior.Ask;
            return Parse(File.ReadAllText(_path));
        }
        catch (IOException ex) { Log.Debug("Close preference load failed: {Error}", ex.Message); return CloseBehavior.Ask; }
        catch (UnauthorizedAccessException ex) { Log.Debug("Close preference load denied: {Error}", ex.Message); return CloseBehavior.Ask; }
    }

    /// <summary>
    /// Saves the chosen behavior. <see cref="CloseBehavior.Ask"/> clears the stored choice,
    /// so a future settings toggle can restore the prompt without deleting files by hand.
    /// </summary>
    public void Save(CloseBehavior behavior)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (behavior == CloseBehavior.Ask)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            AtomicFile.WriteAllText(_path, Serialize(behavior));
        }
        catch (IOException ex) { Log.Debug("Close preference save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Close preference save denied: {Error}", ex.Message); }
    }

    // ── Pure helpers (unit-testable, no file IO) ───────────────────────────

    /// <summary>Serializes the behavior to indented JSON.</summary>
    public static string Serialize(CloseBehavior behavior) =>
        JsonSerializer.Serialize(new Stored(behavior.ToString()), JsonOptions);

    /// <summary>
    /// Parses the stored behavior; returns <see cref="CloseBehavior.Ask"/> for null, blank,
    /// malformed, or unrecognized input. An unknown value is treated as "not chosen" rather
    /// than trusted, so a hand-edited or future-version file cannot force an unexpected action.
    /// </summary>
    public static CloseBehavior Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CloseBehavior.Ask;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored?.Behavior is null) return CloseBehavior.Ask;
            return Enum.TryParse<CloseBehavior>(stored.Behavior, ignoreCase: true, out var parsed)
                && parsed != CloseBehavior.Ask
                ? parsed
                : CloseBehavior.Ask;
        }
        catch (JsonException ex) { Log.Debug("Close preference parse failed: {Error}", ex.Message); return CloseBehavior.Ask; }
    }
}
