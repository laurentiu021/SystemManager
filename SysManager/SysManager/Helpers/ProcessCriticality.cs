// SysManager · ProcessCriticality — the Windows-process name sets Process Manager's kill rules and
// tooltip both key on, verified against where the image actually runs from
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// Which Windows process names are genuinely boot-critical or high-consequence to end, and whether a
/// running image actually sits under <c>%SystemRoot%</c> before either set is allowed to answer for it.
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
/// <para><see cref="IsUnderSystemRoot"/> is the other half: a program named <c>svchost.exe</c> running
/// from <c>%AppData%</c> is not the real service host, and the name match alone used to let it inherit
/// both the refusal and the tooltip's reassurance. An empty or unreadable path is treated as
/// legitimate — most real system processes cannot be read without elevation, and the gap this guards is
/// a copy OUTSIDE Windows, not an inability to verify a real one.</para>
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
    /// under <c>%SystemRoot%</c>.
    /// </summary>
    internal static bool IsBootCritical(string bareName, string? imagePath) =>
        BootCriticalProcesses.Contains(bareName) && IsUnderSystemRoot(imagePath);

    /// <summary>
    /// True for a name in <see cref="HighConsequenceProcesses"/> whose image, if known, actually runs
    /// from under <c>%SystemRoot%</c>.
    /// </summary>
    internal static bool IsHighConsequence(string bareName, string? imagePath) =>
        HighConsequenceProcesses.Contains(bareName) && IsUnderSystemRoot(imagePath);

    /// <summary>
    /// True when <paramref name="imagePath"/> is under <c>%SystemRoot%</c>, OR is null/blank/unreadable.
    /// </summary>
    /// <remarks>
    /// Fails toward trusting the name, not toward distrusting it: most real system processes cannot be
    /// read without elevation (<c>Process.MainModule</c> throws), so an empty path is what the overwhelming
    /// majority of genuine boot-critical processes look like from here. What this exists to catch is a
    /// path that resolves to somewhere definite and WRONG — a user-writable folder — not an inability to
    /// resolve one at all (#2792).
    /// </remarks>
    internal static bool IsUnderSystemRoot(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return true;
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return SystemPaths.IsInsideSubtree(imagePath, systemRoot);
    }
}
