// SysManager · ProcessCriticality — the Windows-process name sets Process Manager's kill rules and
// tooltip both key on, verified against where the image actually runs from
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Helpers;

/// <summary>
/// Which Windows process names are genuinely boot-critical or high-consequence to end, and whether a
/// running image actually sits where Windows keeps that program before either set is allowed to answer for it.
/// </summary>
/// <remarks>
/// One copy, read by <c>ProcessManagerViewModel</c> (the refusal and the confirmation tiers) and
/// <see cref="Models.ProcessEntry.SafetyTooltip"/> (what the chip's tooltip says), so the two cannot
/// drift the way <c>IsKernelCritical</c>'s refusal message and the chip's tooltip had: the tooltip told
/// every System-tier row "ending it will not crash your PC", which was untrue for every name in
/// <see cref="BootCriticalProcesses"/> and understated for every name in
/// <see cref="HighConsequenceProcesses"/> (#2792).
/// <para>Matched by name, not by <c>ProcessDescriptionEntry.Safety</c>: that field is PROVENANCE from
/// the bundled description database and 59 of its 108 entries carry "System", including Notepad and
/// Calculator — keying the refusal on it once made the app refuse to end Notepad with a false
/// blue-screen warning. This set is the narrow, specific list of names that are genuinely unkillable or
/// genuinely high-consequence, independent of the database.</para>
/// <para><see cref="IsInSystemFolder"/> and <see cref="IsWhereWindowsKeepsItsPrograms"/> are the other half: a
/// program named <c>svchost.exe</c> running from <c>%AppData%</c> is not the real service host, and the name match
/// alone used to let it inherit both the refusal and the tooltip's reassurance. An empty or unreadable path is treated
/// as legitimate — most real system processes cannot be read without elevation, and the gap this guards is a copy
/// somewhere Windows does not keep its programs, not an inability to verify a real one.</para>
/// <para>Exact folders, not the whole Windows folder: some folders inside it, such as <c>Windows\Temp</c>,
/// <c>Windows\Tasks</c> and <c>System32\Tasks</c>, are ones any program can write to, and a copy there inherited
/// the refusal and the Windows label until both checks named the folders they accept (#3437).</para>
/// </remarks>
internal static class ProcessCriticality
{
    /// <summary>
    /// Windows components whose death takes the machine down with it: the kill the tab refuses outright,
    /// because "killing it would cause a system crash (BSOD)" has to stay true for every name in it.
    /// </summary>
    internal static readonly HashSet<string> BootCriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "winlogon", "wininit", "csrss", "smss", "services",
        "lsass", "lsaiso", "fontdrvhost", "dwm", "logonui",
        "svchost", "ctfmon", "userinit"
    };

    /// <summary>
    /// Windows components that are killable, but whose death costs more than "a feature may look
    /// broken until you sign out" — so the ordinary Windows-component warning would be untrue for them.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Security: ending <c>MsMpEng</c> or <c>SecurityHealthService</c> is an antivirus-disable
    /// step, not a cosmetic one.</item>
    /// <item>Servicing: ending <c>TrustedInstaller</c> or <c>msiexec</c> mid-operation can leave a
    /// half-applied update or a corrupt component store — damage that survives a restart.</item>
    /// </list>
    /// Still a confirmation and not a refusal: unlike the boot-critical set these really can be ended.
    /// </remarks>
    internal static readonly HashSet<string> HighConsequenceProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Security — Defender's engine, its network inspection service, and the Security Centre.
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray",
        // Servicing / installers — killing these mid-write is what corrupts state.
        "TrustedInstaller", "msiexec", "WmiPrvSE", "WUDFHost",
    };

    /// <summary>
    /// True for a name in <see cref="BootCriticalProcesses"/> whose image, if known, actually runs from
    /// the System folder itself.
    /// </summary>
    internal static bool IsBootCritical(string bareName, string? imagePath) =>
        BootCriticalProcesses.Contains(bareName) && IsInSystemFolder(imagePath);

    /// <summary>
    /// True for a name in <see cref="HighConsequenceProcesses"/> whose image, if known, actually runs
    /// where Windows keeps its programs.
    /// </summary>
    /// <remarks>
    /// Every name here is a Windows entry of the description database, so the tier asks what the Windows label asks:
    /// Defender's engine runs from its platform folder, not the System folder.
    /// </remarks>
    internal static bool IsHighConsequence(string bareName, string? imagePath) =>
        HighConsequenceProcesses.Contains(bareName) && IsWhereWindowsKeepsItsPrograms(imagePath);

    /// <summary>
    /// True when <paramref name="imagePath"/> sits directly in the System folder, OR cannot be placed: null, blank, not
    /// a
    /// full path, or a device path of another kind.
    /// </summary>
    /// <remarks>
    /// For the refusal, so it fails toward the name: most real system processes cannot be read without elevation
    /// (<c>Process.MainModule</c> throws), so an empty path is what the overwhelming majority of genuine boot-critical
    /// processes look like from here, and a path the check cannot place is no evidence of a copy either. Only a
    /// definite
    /// drive or network path outside the System folder lifts the refusal (#2792). Directly in it, because folders below
    /// it, such as <c>System32\Tasks</c>, are ones any program can write to.
    /// </remarks>
    internal static bool IsInSystemFolder(string? imagePath) =>
        DefinitePath(imagePath) is not { } path || SystemPaths.IsDirectlyInside(path, Environment.SystemDirectory);

    /// <summary>
    /// The publisher IDs that end the folder name of a package Microsoft publishes: those of "Microsoft Corporation"
    /// and of "Microsoft Windows".
    /// </summary>
    /// <remarks>
    /// A package's folder under WindowsApps ends in its publisher's ID, a hash of the name of the publisher that signed
    /// the package, so nobody else can install a package that ends in either. Read off this PC's own packages: Notepad,
    /// Paint, the Snipping Tool and Terminal end in the first, Widgets in the second.
    /// </remarks>
    private static readonly string[] MicrosoftPublisherIds = ["8wekyb3d8bbwe", "cw5n1h2txyewy"];

    /// <summary>
    /// True when <paramref name="imagePath"/> is where Windows keeps its own programs, OR is null or blank.
    /// </summary>
    /// <remarks>
    /// The folders inside Windows that its programs run from (<see cref="WindowsFolders"/>, each one exactly, not what
    /// lies below it), and places only Windows or an administrator can write to, as whole trees: Windows' own app
    /// packages in <c>Windows\SystemApps</c>, a package Microsoft publishes under <c>%ProgramFiles%\WindowsApps</c>,
    /// Windows' own folders under Program Files, WSL as the Store installs it, and Defender's platform folder under
    /// ProgramData. Windows 11 installs Notepad, Paint, the Snipping Tool, Terminal and Widgets as Microsoft packages,
    /// Windows Media Player runs from Program Files, and Defender updates itself into ProgramData, so checking the
    /// System folder alone called each of them "Not recognised" (#3166).
    /// <para>For the description database's Windows entries, so it fails the other way from
    /// <see cref="IsInSystemFolder"/> for a path it cannot place: blank is unreadable and trusts the name, but a path
    /// that is not a full one is never vouched for. Resolving it would judge it against the folder SysManager runs
    /// in.</para>
    /// </remarks>
    internal static bool IsWhereWindowsKeepsItsPrograms(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return true;
        if (DefinitePath(imagePath) is not { } path) return false;
        return SystemPaths.IsDirectlyInsideAny(path, WindowsFolders)
               || IsMicrosoftPackage(path)
               || SystemPaths.IsInsideAnySubtree(path, WindowsProgramTrees);
    }

    /// <summary>
    /// <paramref name="imagePath"/> as a drive or network path, or null when it is not one: blank, relative, or a
    /// device
    /// path other than a drive one.
    /// </summary>
    /// <remarks>
    /// <c>\\?\C:\…</c> and <c>\??\C:\…</c> name the same file as <c>C:\…</c>, and Windows reports some images
    /// that way, so the prefix is taken off. Any other device path (<c>\\.\</c>, <c>\\?\UNC\</c>, a volume GUID)
    /// cannot be placed by its spelling.
    /// </remarks>
    private static string? DefinitePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var path = imagePath;
        foreach (var prefix in DriveDevicePrefixes)
        {
            if (path.Length > prefix.Length + 2 && path.StartsWith(prefix, StringComparison.Ordinal)
                && char.IsAsciiLetter(path[prefix.Length]) && path[prefix.Length + 1] == ':'
                && path[prefix.Length + 2] == '\\')
            {
                path = path[prefix.Length..];
                break;
            }
        }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private static readonly string[] DriveDevicePrefixes = [@"\\?\", @"\??\"];

    /// <summary>Whether <paramref name="imagePath"/> is in a package Microsoft publishes, in WindowsApps.</summary>
    private static bool IsMicrosoftPackage(string imagePath)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length == 0) return false;

        var packages = Path.Combine(programFiles, "WindowsApps");
        if (!SystemPaths.IsInsideSubtree(imagePath, packages)) return false;

        // The first folder under WindowsApps is the package's own. A file straight inside WindowsApps belongs to none.
        var relative = Path.GetRelativePath(packages, Path.GetFullPath(imagePath));
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        if (separator <= 0) return false;

        var package = relative[..separator];
        return MicrosoftPublisherIds.Any(id => package.EndsWith("_" + id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Under(Environment.SpecialFolder folder, string child) =>
        Environment.GetFolderPath(folder) is { Length: > 0 } root ? Path.Combine(root, child) : null;

    /// <summary>
    /// The folders inside Windows that the database's Windows programs run from, each matched exactly: a file directly
    /// in one, never one in a folder below it.
    /// </summary>
    /// <remarks>
    /// Read off where each of the database's Windows entries lives on Windows 11, 64-bit and 32-bit copies alike. Built
    /// once: the check runs for every process of a look.
    /// </remarks>
    private static readonly string?[] WindowsFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } windows ? windows : null,
        Environment.SystemDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) is { Length: > 0 } x86 ? x86 : null,
        Path.Combine(Environment.SystemDirectory, "wbem"),
        Under(Environment.SpecialFolder.SystemX86, "wbem"),
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0"),
        Under(Environment.SpecialFolder.SystemX86, Path.Combine("WindowsPowerShell", "v1.0")),
        Under(Environment.SpecialFolder.Windows, "servicing"),
        Under(Environment.SpecialFolder.Windows, "ImmersiveControlPanel"),
    ];

    /// <summary>
    /// Folders only Windows or an administrator can write to, whose whole tree is Windows' own programs: Windows' own
    /// app
    /// packages, Windows Media Player, Defender and its platform folder, and WSL as the Store installs it.
    /// </summary>
    private static readonly string?[] WindowsProgramTrees =
    [
        Under(Environment.SpecialFolder.Windows, "SystemApps"),
        Under(Environment.SpecialFolder.ProgramFiles, "Windows Media Player"),
        Under(Environment.SpecialFolder.ProgramFilesX86, "Windows Media Player"),
        Under(Environment.SpecialFolder.ProgramFiles, "Windows Defender"),
        Under(Environment.SpecialFolder.CommonApplicationData,
            Path.Combine("Microsoft", "Windows Defender", "Platform")),
        Under(Environment.SpecialFolder.ProgramFiles, "WSL"),
    ];
}
