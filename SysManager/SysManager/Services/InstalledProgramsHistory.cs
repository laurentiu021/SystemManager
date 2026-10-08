// SysManager · InstalledProgramsHistory — which programs appeared or left between two looks
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Text.Json;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>One program as the list of installed programs names it.</summary>
public sealed record ProgramName(string Name, string Publisher);

/// <summary>A program that appeared in, or left, the list of installed programs between two looks.</summary>
/// <param name="Name">The program's name now, or as it was when it left.</param>
/// <param name="Publisher">Its publisher, or empty.</param>
/// <param name="Kind">
/// <see cref="ChangeKind.ProgramAppeared"/>, <see cref="ChangeKind.ProgramDisappeared"/>, or
/// <see cref="ChangeKind.ProgramUpdated"/> for one name replaced by another of the same program.
/// </param>
/// <param name="Since">The earlier look: it was not there, or was.</param>
/// <param name="Until">The look that found the difference.</param>
/// <param name="Before">For an update, the name it had before; otherwise empty.</param>
public sealed record ProgramChange(string Name, string Publisher, ChangeKind Kind, DateTime Since, DateTime Until,
                                   string Before = "");

/// <summary>What a look at the installed programs found.</summary>
/// <param name="Changes">Every difference kept, oldest first, including those from earlier looks.</param>
/// <param name="FirstLook">True when there was no earlier look to compare with, so this one only starts the record.</param>
/// <param name="Readable">
/// False when this look could not compare: the record, or Windows' list of installed programs, could not be read, and
/// nothing was written. What earlier looks found is still in <paramref name="Changes"/> when the record itself could be.
/// </param>
public sealed record ProgramsLook(IReadOnlyList<ProgramChange> Changes, bool FirstLook, bool Readable)
{
    public static readonly ProgramsLook Unreadable = new([], FirstLook: false, Readable: false);
}

/// <summary>
/// Keeps the list of installed programs from one look at Recent Changes to the next, and what changed between (#1507).
/// </summary>
public interface IInstalledProgramsHistory
{
    /// <summary>
    /// Compares the programs installed now with the list kept at the last look, keeps the differences, and keeps the
    /// list for the next look.
    /// </summary>
    ProgramsLook Observe(DateTime now);
}

/// <summary>The history behind <see cref="IInstalledProgramsHistory"/>, in <c>installed-programs.json</c>.</summary>
/// <remarks>
/// <para>The record Windows Installer programs do not need: Windows keeps their install times itself. Every other
/// installer leaves only an entry in the list, so the list is kept at each look and a program that is new in it is
/// reported with the time between the two looks, which is all that can be known.</para>
/// <para>Only what Windows' own list shows is kept: hidden components and updates come and go with the programs they
/// belong to. A name replaced by another of the same program and publisher, "Python 3.12.1" by "Python 3.12.2", is an
/// update, not one program removed and another installed.</para>
/// <para>Reads the file before it writes, under <see cref="_lock"/>, and writes nothing when the file is there but
/// could not be read; one that does not parse is set aside first, as the activity log does.</para>
/// <para>Nor when Windows' list could not be read whole. Compared, every program missing from it would be reported
/// removed, and kept, all of them installed again at the next look.</para>
/// </remarks>
public sealed class InstalledProgramsHistory : IInstalledProgramsHistory
{
    /// <summary>How many differences are kept, newest last. Enough for months of ordinary installs.</summary>
    internal const int MaxChanges = 500;

    private readonly string _filePath;
    private readonly Func<UninstallList> _readInstalled;
    private readonly Lock _lock = new();

    /// <summary>The history production uses: the real list of installed programs, kept in the user's profile.</summary>
    public InstalledProgramsHistory() : this(null, UninstallEntries.ReadAll) { }

    /// <summary>A history kept under <paramref name="configDir"/>, reading the installed programs from <paramref name="readInstalled"/>.</summary>
    internal InstalledProgramsHistory(string? configDir, Func<UninstallList> readInstalled)
    {
        var dir = configDir ?? Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysManager");
        _filePath = Path.Join(dir, "installed-programs.json");
        _readInstalled = readInstalled ?? throw new ArgumentNullException(nameof(readInstalled));
    }

    /// <summary>The file the history is kept in.</summary>
    internal string FilePath => _filePath;

    public ProgramsLook Observe(DateTime now)
    {
        lock (_lock)
        {
            var json = StoreFile.ReadText(_filePath);
            if (json is null) return ProgramsLook.Unreadable;

            Stored? stored = null;
            if (json.Length > 0)
            {
                stored = Parse(json);
                if (stored is null && !StoreFile.SetAside(_filePath)) return ProgramsLook.Unreadable;
            }

            var installed = _readInstalled();
            if (!installed.Complete) return new ProgramsLook(stored?.Changes ?? [], FirstLook: false, Readable: false);

            var current = installed.Entries
                .Where(e => e.IsListed)
                .Select(e => new ProgramName(e.Name, e.Publisher))
                .DistinctBy(p => (p.Name.ToUpperInvariant(), p.Publisher.ToUpperInvariant()))
                .ToList();

            var changes = stored?.Changes ?? [];
            if (stored is not null)
                changes = [.. changes, .. Compare(stored.Programs, current, stored.TakenAt, now)];
            changes = [.. changes.Where(c => now - c.Until <= RecentChangesService.KeptFor).TakeLast(MaxChanges)];

            Save(new Stored(now, current, changes));
            return new ProgramsLook(changes, FirstLook: stored is null, Readable: true);
        }
    }

    /// <summary>The differences between two lists of programs, taken at <paramref name="since"/> and <paramref name="until"/>.</summary>
    internal static IReadOnlyList<ProgramChange> Compare(IReadOnlyList<ProgramName> before, IReadOnlyList<ProgramName> after,
                                                         DateTime since, DateTime until)
    {
        var appeared = after.Where(a => !before.Any(b => Same(a, b))).ToList();
        var left = before.Where(b => !after.Any(a => Same(a, b))).ToList();
        var changes = new List<ProgramChange>();

        foreach (var program in appeared)
        {
            // The same program under a new name: same publisher, same name once the version numbers are taken out.
            var replaced = left.FirstOrDefault(l => program.Publisher.Length > 0
                && string.Equals(l.Publisher, program.Publisher, StringComparison.OrdinalIgnoreCase)
                && string.Equals(WithoutVersion(l.Name), WithoutVersion(program.Name), StringComparison.OrdinalIgnoreCase));
            if (replaced is not null)
            {
                left.Remove(replaced);
                changes.Add(new ProgramChange(program.Name, program.Publisher, ChangeKind.ProgramUpdated, since, until, replaced.Name));
            }
            else
            {
                changes.Add(new ProgramChange(program.Name, program.Publisher, ChangeKind.ProgramAppeared, since, until));
            }
        }
        changes.AddRange(left.Select(l => new ProgramChange(l.Name, l.Publisher, ChangeKind.ProgramDisappeared, since, until)));
        return changes;

        static bool Same(ProgramName a, ProgramName b) =>
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Publisher, b.Publisher, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A name with its version numbers taken out: "Python 3.12.1 (64-bit)" is "Python (64-bit)".</summary>
    internal static string WithoutVersion(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !IsVersion(word.Trim('(', ')', ','))));

    private static bool IsVersion(string word) =>
        word.Length > 0 && word.Any(char.IsDigit) && word.All(c => char.IsDigit(c) || c is '.' or 'v' or 'V' or '-');

    /// <summary>What the file holds.</summary>
    internal sealed record Stored(DateTime TakenAt, List<ProgramName> Programs, List<ProgramChange> Changes);

    /// <summary>The stored history, or null when the text is not one.</summary>
    internal static Stored? Parse(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);
            if (stored is null || stored.Programs is null) return null;
            return stored with
            {
                Programs = [.. stored.Programs.Where(p => p is { Name.Length: > 0 }).Select(p => p with { Publisher = p.Publisher ?? "" })],
                Changes = [.. (stored.Changes ?? []).Where(c => c is { Name.Length: > 0 })
                    .Select(c => c with { Publisher = c.Publisher ?? "", Before = c.Before ?? "" })],
            };
        }
        catch (JsonException ex)
        {
            Log.Debug("Installed programs history unreadable: {Error}", ex.Message);
            return null;
        }
    }

    private void Save(Stored stored)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("Installed programs history not saved: {Error}", ex.Message);
        }
    }
}
