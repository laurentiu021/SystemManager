// SysManager · SfcOutput
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SysManager.Helpers;

/// <summary>How an SFC scan ended, by the message <c>sfc.exe</c> ended it with.</summary>
/// <remarks>In the order a verdict picks them when more than one is printed: the worst first.</remarks>
internal enum SfcOutcome
{
    /// <summary>"Windows Resource Protection could not perform the requested operation."</summary>
    CouldNotPerform,

    /// <summary>"Windows Resource Protection could not start the repair service."</summary>
    ServiceDidNotStart,

    /// <summary>"Windows Resource Protection found corrupt files but was unable to fix some of them."</summary>
    UnableToFix,

    /// <summary>
    /// No integrity violations found, but SFC cannot guarantee it, because component metadata is corrupt.
    /// </summary>
    CannotGuarantee,

    /// <summary>"Another servicing or repair operation is currently running."</summary>
    AnotherOperationRunning,

    /// <summary>"There is a system repair pending which requires reboot to complete."</summary>
    RepairPending,

    /// <summary>"The maximum number of files have been queued for repair." A restart completes them.</summary>
    MaximumQueued,

    /// <summary>"The maximum number of files have been repaired." Another run repairs the rest.</summary>
    MaximumRepaired,

    /// <summary>"Windows Resource Protection found corrupt files and successfully repaired them."</summary>
    Repaired,

    /// <summary>"The system file repair changes will take effect after the next reboot."</summary>
    TakesEffectAfterRestart,

    /// <summary>"Windows Resource Protection did not find any integrity violations."</summary>
    NoViolations,
}

/// <summary>
/// Reads what <c>sfc.exe</c> prints: the encoding it writes, its progress line, and the sentences it ends a scan
/// with, in the language Windows shows them in (#2835).
/// </summary>
/// <remarks>
/// Redirected, SFC writes UTF-16LE, unlike DISM, chkdsk and the other console tools the app starts, which write the
/// OEM code page. Read as the OEM code page, every letter came with a NUL, so no verdict and no percentage was ever
/// found.
/// <para>SFC's sentences come from <c>sfc.exe.mui</c>, which follows the Windows display language, so English phrases
/// could match only on an English Windows. These are read from the same message table, by <c>FormatMessage</c> on
/// <c>sfc.exe</c> loaded as a data file, so a sentence is matched in whatever language SFC printed it. Each message is
/// matched by its first line of text: SFC breaks its longer messages over several lines, and each arrives on its own.
/// The table is read in Windows' language and in US English, beside the English text Windows 11 ships, so a table
/// that cannot be read still leaves the English sentences.</para>
/// </remarks>
internal static partial class SfcOutput
{
    /// <summary>The encoding <c>sfc.exe</c> writes when its output is redirected: UTF-16LE.</summary>
    internal static Encoding Encoding => Encoding.Unicode;

    /// <summary>
    /// Every outcome with its ID in <c>sfc.exe</c>'s message table and that message's first line in English, in
    /// <see cref="SfcOutcome"/> order.
    /// </summary>
    internal static readonly IReadOnlyList<(SfcOutcome Outcome, uint Id, string English)> Messages =
    [
        (SfcOutcome.CouldNotPerform, 0x4000_1007,
            "Windows Resource Protection could not perform the requested operation."),
        (SfcOutcome.ServiceDidNotStart, 0x4000_100E,
            "Windows Resource Protection could not start the repair service."),
        (SfcOutcome.UnableToFix, 0x4000_1009,
            "Windows Resource Protection found corrupt files but was unable to fix some of them."),
        (SfcOutcome.CannotGuarantee, 0x4000_1015,
            "Windows Resource Protection did not find any integrity violations but cannot"),
        (SfcOutcome.AnotherOperationRunning, 0x4000_1013,
            "Another servicing or repair operation is currently running."),
        (SfcOutcome.RepairPending, 0x4000_100D,
            "There is a system repair pending which requires reboot to complete.  Restart"),
        (SfcOutcome.MaximumQueued, 0x4000_1003,
            "The maximum number of files have been queued for repair. A reboot is required to"),
        (SfcOutcome.MaximumRepaired, 0x4000_1004,
            "The maximum number of files have been repaired. To repair the remaining corrupted"),
        (SfcOutcome.Repaired, 0x4000_1008,
            "Windows Resource Protection found corrupt files and successfully repaired them."),
        (SfcOutcome.TakesEffectAfterRestart, 0x4000_1002,
            "The system file repair changes will take effect after the next reboot."),
        (SfcOutcome.NoViolations, 0x4000_100A,
            "Windows Resource Protection did not find any integrity violations."),
    ];

    /// <summary>
    /// The first line of each message as English Windows prints it, to the outcome it ends a scan with.
    /// </summary>
    internal static IReadOnlyDictionary<string, SfcOutcome> English { get; } = Index(
        Messages.Select(m => (m.Outcome, m.English)));

    /// <summary>
    /// The first line of each message as this Windows prints it, in its own language and in English, to the outcome.
    /// Read from <c>sfc.exe</c> once, on first use.
    /// </summary>
    internal static IReadOnlyDictionary<string, SfcOutcome> Sentences => Loaded.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, SfcOutcome>> Loaded =
        new(() => Read(SystemPaths.ResolveSystemTool("sfc.exe")));

    private const uint UsEnglish = 0x0409;

    /// <summary>
    /// The sentences of the <c>sfc.exe</c> at <paramref name="sfcPath"/>: its message table in Windows' language,
    /// then in US English, then the English that ships with Windows 11.
    /// </summary>
    internal static IReadOnlyDictionary<string, SfcOutcome> Read(string sfcPath) =>
        Index(ReadTable(sfcPath, language: 0).Concat(ReadTable(sfcPath, UsEnglish))
            .Concat(Messages.Select(m => (m.Outcome, m.English))));

    /// <summary>
    /// The first line of each message in the table of the <c>sfc.exe</c> at <paramref name="sfcPath"/>, in
    /// <paramref name="language"/>, 0 being the one Windows picks for this user. Empty when the file cannot be loaded,
    /// and without the messages the table has no text for in that language.
    /// </summary>
    internal static IReadOnlyList<(SfcOutcome Outcome, string FirstLine)> ReadTable(string sfcPath, uint language)
    {
        var module = LoadLibraryEx(sfcPath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) return [];
        try
        {
            var buffer = new char[2048];
            List<(SfcOutcome, string)> lines = [];
            foreach (var (outcome, id, _) in Messages)
            {
                var length = FormatMessage(FormatMessageFromHModule | FormatMessageIgnoreInserts,
                    module, id, language, buffer, (uint)buffer.Length, IntPtr.Zero);
                if (length == 0) continue;
                // Most of the messages start with an empty line, which SFC prints as well.
                var first = new string(buffer, 0, (int)length)
                    .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (first is not null) lines.Add((outcome, first));
            }
            return lines;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    /// <summary>
    /// The outcomes the lines of a scan's output name, by their first lines in <paramref name="sentences"/>.
    /// </summary>
    internal static IReadOnlySet<SfcOutcome> OutcomesIn(
        IEnumerable<string> lines, IReadOnlyDictionary<string, SfcOutcome> sentences)
    {
        HashSet<SfcOutcome> seen = [];
        foreach (var line in lines)
        {
            if (sentences.TryGetValue(line.Trim(), out var outcome)) seen.Add(outcome);
        }
        return seen;
    }

    /// <summary>
    /// The percentage in SFC's progress line, "Verification 5% complete." in English, or null for any other line.
    /// </summary>
    /// <remarks>
    /// The line is translated like the rest, so it is read by its one number and its percent sign, on either side.
    /// None of SFC's other messages has both.
    /// </remarks>
    internal static int? ProgressIn(string line)
    {
        if (!line.Contains('%')) return null;
        var number = ProgressNumber().Match(line);
        return number.Success && int.TryParse(number.Value, out var percent) && percent is >= 0 and <= 100
            ? percent
            : null;
    }

    private static IReadOnlyDictionary<string, SfcOutcome> Index(IEnumerable<(SfcOutcome Outcome, string Line)> lines)
    {
        // The first line given for a sentence keeps it: a language that gave two outcomes the same first line would
        // be read as the worse of them, which comes first.
        Dictionary<string, SfcOutcome> index = new(StringComparer.Ordinal);
        foreach (var (outcome, line) in lines) index.TryAdd(line.Trim(), outcome);
        return index;
    }

    private static readonly char[] LineBreaks = ['\r', '\n'];

    private const uint FormatMessageIgnoreInserts = 0x0000_0200;
    private const uint FormatMessageFromHModule = 0x0000_0800;
    private const uint LoadLibraryAsDatafile = 0x0000_0002;
    private const uint LoadLibraryAsImageResource = 0x0000_0020;

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressNumber();

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr module);

    [LibraryImport("kernel32.dll", EntryPoint = "FormatMessageW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial uint FormatMessage(
        uint flags, IntPtr source, uint messageId, uint languageId, [Out] char[] buffer, uint size, IntPtr arguments);
}
