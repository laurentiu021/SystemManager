// SysManager · StoreFile
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using Serilog;

namespace SysManager.Helpers;

/// <summary>
/// The read half of a load-modify-save, for a file that holds everything a store has saved.
/// </summary>
/// <remarks>
/// Most stores read their whole file, change one item and write the whole file back. They used to turn a read that
/// failed into the same value as "nothing saved yet", so the write that followed replaced everything the file held
/// with that one change (#2521). <see cref="AtomicFile"/> makes the write safe. This makes the read safe to write over:
/// <see cref="ReadText"/> tells a missing file from one that could not be read, and <see cref="SetAside"/> keeps a
/// file that does not parse, rather than destroying it.
/// </remarks>
internal static class StoreFile
{
    /// <summary>
    /// The file's text, "" when there is no file yet, or null when the file is there and could not be read.
    /// </summary>
    /// <remarks>
    /// Null is the one answer a writer must not build on. Nothing was read, so nothing may be written in its place.
    /// </remarks>
    public static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException ex) { Log.Warning("Could not read {File}: {Error}", Path.GetFileName(path), ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Could not read {File}: {Error}", Path.GetFileName(path), ex.Message); }
        return null;
    }

    /// <summary>
    /// Moves a file that does not parse out of the way, to "&lt;name&gt;.unreadable", so a fresh one can be written
    /// in its place without destroying it. Returns false when it could not be moved, and then nothing may be
    /// written over it.
    /// </summary>
    /// <remarks>
    /// A file the store cannot parse holds nothing the store can use. Keeping it blocked would stop the store working
    /// for good, and writing over it would destroy what may still be recoverable by hand. The first free name is
    /// used: "&lt;name&gt;.unreadable", then "&lt;name&gt;.unreadable-2" and on, so an earlier one is never replaced.
    /// </remarks>
    public static bool SetAside(string path)
    {
        try
        {
            var aside = path + ".unreadable";
            for (var n = 2; File.Exists(aside); n++)
                aside = $"{path}.unreadable-{n}";

            File.Move(path, aside);
            Log.Warning("{File} could not be parsed and was kept as {Aside}", Path.GetFileName(path), Path.GetFileName(aside));
            return true;
        }
        catch (IOException ex) { Log.Warning("Could not set {File} aside: {Error}", Path.GetFileName(path), ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Could not set {File} aside: {Error}", Path.GetFileName(path), ex.Message); }
        return false;
    }
}
