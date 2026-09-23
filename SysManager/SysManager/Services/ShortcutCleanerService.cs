// SysManager · ShortcutCleanerService — scans for broken .lnk shortcuts
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Serilog;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans common locations for .lnk shortcuts whose targets no longer exist.
/// Supports deletion to Recycle Bin or permanent delete.
/// </summary>
public sealed partial class ShortcutCleanerService
{
    /// <summary>
    /// Scans all common shortcut locations for shortcuts whose target is CONFIRMED gone, plus a count of
    /// the ones it could not decide about.
    /// </summary>
    public Task<ShortcutScanReport> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(progress, ct), ct);

    private static ShortcutScanReport Scan(
        IProgress<string>? progress, CancellationToken ct)
    {
        List<BrokenShortcut> results = [];
        var unreachable = 0;
        var locations = GetScanLocations();

        foreach (var (label, path) in locations)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

            progress?.Report($"Scanning {label}...");

            foreach (var lnk in SafeFileWalk.Files(path, ct, LnkWalk))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var target = ResolveShortcutTarget(lnk);
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    // Skip URLs, shell objects, and special targets
                    if (target.StartsWith("::") || target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // "Broken" must mean the scan ESTABLISHED the target is gone, never that it failed to
                    // reach it — see ClassifyTarget. An undecided target is counted and left alone, because
                    // the rows this produces arrive pre-ticked for deletion (#2378).
                    switch (ClassifyTarget(target))
                    {
                        case TargetVerdict.Missing:
                            results.Add(new BrokenShortcut
                            {
                                Name = Path.GetFileNameWithoutExtension(lnk),
                                ShortcutPath = lnk,
                                TargetPath = target,
                                Location = label
                            });
                            break;

                        case TargetVerdict.Unreachable:
                            unreachable++;
                            break;
                    }
                }
                catch (IOException) { /* skip inaccessible shortcut */ }
                catch (UnauthorizedAccessException) { /* skip protected shortcut */ }
                catch (COMException) { /* skip corrupted shortcut */ }
            }
        }

        // A cancelled scan exits the loops above with partial results — they break on cancellation
        // rather than throwing — so returning normally would hand the caller a short list with no
        // indication that it is short. The view model's success path then claimed a finished scan and,
        // if nothing had been found yet, that the PC was clean (#2275). Throw so its existing cancel
        // branch runs instead; it already says "Scan cancelled." and was dead code until now. Both
        // sibling scanners end the same way, for the same reason.
        ct.ThrowIfCancellationRequested();

        return new ShortcutScanReport { Broken = results, UnreachableTargets = unreachable };
    }

    /// <summary>What the scan was able to establish about one shortcut's target.</summary>
    internal enum TargetVerdict
    {
        /// <summary>Confirmed gone: the location was readable and the target was not in it.</summary>
        Missing,

        /// <summary>Confirmed there.</summary>
        Present,

        /// <summary>Could not be established. Not the same as gone, and never offered for deletion.</summary>
        Unreachable,
    }

    /// <summary>
    /// Decides whether a shortcut's target is gone, present, or undecidable, with the two filesystem
    /// questions injected so the decision can be tested without an unplugged drive or an offline server.
    /// </summary>
    /// <remarks>
    /// <c>File.Exists</c> returns false for "it is not there" AND for "I could not find out", and the tab
    /// presented the second as the first — its own subtitle says "shortcuts that point at programs you have
    /// already removed" (#2378). Four ways to be alive and read as removed: a target on a drive that is not
    /// currently attached, a UNC path whose server is asleep, a locked BitLocker volume, and a path the
    /// current user cannot stat. This tab is usable without elevation, which is exactly when the last one
    /// happens.
    /// <para>The discriminator is the EXCEPTION, not the boolean: <c>File.GetAttributes</c> throws
    /// <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/> for a real absence and
    /// <see cref="UnauthorizedAccessException"/> / <see cref="IOException"/> ("the device is not ready",
    /// "the network path was not found") when it could not tell. A drive-letter root is additionally checked
    /// for readiness BEFORE any I/O, because an unmounted volume is the common case and asking the OS about a
    /// path on it is both slow and ambiguous.</para>
    /// <para>A UNC target is never reported as missing. An absent share and a sleeping NAS are
    /// indistinguishable from here without waiting out a network timeout per shortcut, and "Recent Items" is
    /// one of the scanned locations — so a machine that has ever opened a file from a share has a list full of
    /// them. Refusing to judge costs a dead network shortcut staying on the desktop; judging wrongly costs a
    /// live one being deleted.</para>
    /// </remarks>
    internal static TargetVerdict ClassifyTarget(
        string target,
        Func<string, FileAttributes>? readAttributes = null,
        Func<string, bool>? volumeIsReady = null)
    {
        readAttributes ??= File.GetAttributes;
        volumeIsReady ??= IsVolumeReady;

        // UNC: decide before touching the network. Present is still worth establishing — a reachable share
        // answers immediately — but a failure of any kind is undecided rather than gone.
        var isUnc = target.StartsWith(@"\\", StringComparison.Ordinal);

        if (!isUnc)
        {
            var root = SafeRoot(target);
            if (root.Length > 0 && !volumeIsReady(root)) return TargetVerdict.Unreachable;
        }

        try
        {
            readAttributes(target);
            return TargetVerdict.Present;
        }
        catch (FileNotFoundException) { return isUnc ? TargetVerdict.Unreachable : TargetVerdict.Missing; }
        catch (DirectoryNotFoundException) { return isUnc ? TargetVerdict.Unreachable : TargetVerdict.Missing; }
        catch (UnauthorizedAccessException) { return TargetVerdict.Unreachable; }
        catch (IOException) { return TargetVerdict.Unreachable; }
        catch (ArgumentException) { return TargetVerdict.Unreachable; }
        catch (NotSupportedException) { return TargetVerdict.Unreachable; }
    }

    /// <summary>The path's root, or "" when it has none or cannot be parsed.</summary>
    private static string SafeRoot(string path)
    {
        try { return Path.GetPathRoot(path) ?? ""; }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>
    /// Whether the volume at <paramref name="root"/> is attached and readable. False for an unplugged
    /// stick, an ejected card, an unmounted VHD, and a BitLocker volume still waiting for its password.
    /// </summary>
    private static bool IsVolumeReady(string root)
    {
        try { return new DriveInfo(root).IsReady; }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// How the shortcut scan walks: <c>*.lnk</c> only, no exclusions.
    /// </summary>
    /// <remarks>
    /// Through <see cref="SafeFileWalk"/> rather than
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> with
    /// <see cref="SearchOption.AllDirectories"/>, which throws mid-iteration the first time it hits a folder
    /// it cannot read (a protected Start-Menu subfolder, say) — that aborted the entire scan for a location
    /// and silently dropped every shortcut after it.
    /// </remarks>
    private static SafeWalkOptions LnkWalk { get; } = new() { SearchPattern = "*.lnk" };

    /// <summary>
    /// Deletes selected shortcuts. Returns count of successfully deleted items.
    /// </summary>
    public static int DeleteShortcuts(IEnumerable<BrokenShortcut> shortcuts, bool toRecycleBin)
    {
        int deleted = 0;
        foreach (var s in shortcuts.Where(x => x.IsSelected))
        {
            try
            {
                if (!File.Exists(s.ShortcutPath)) continue;

                if (toRecycleBin)
                {
                    // Only count it if the shell actually recycled the file.
                    if (MoveToRecycleBin(s.ShortcutPath))
                        deleted++;
                    else
                        Log.Warning("Recycle failed (shell reported error): {Path}", s.ShortcutPath);
                }
                else
                {
                    File.Delete(s.ShortcutPath);
                    deleted++;
                }
            }
            catch (IOException ex) { Log.Warning(ex, "Failed to delete shortcut: {Path}", s.ShortcutPath); }
            catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Access denied deleting shortcut: {Path}", s.ShortcutPath); }
        }
        return deleted;
    }

    private static List<(string Label, string Path)> GetScanLocations()
    {
        List<(string, string)> locations = [];

        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(userDesktop)) locations.Add(("Desktop", userDesktop));
        if (!string.IsNullOrEmpty(publicDesktop) && publicDesktop != userDesktop)
            locations.Add(("Public Desktop", publicDesktop));

        var userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        var commonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        if (!string.IsNullOrEmpty(userStartMenu)) locations.Add(("Start Menu", userStartMenu));
        if (!string.IsNullOrEmpty(commonStartMenu) && commonStartMenu != userStartMenu)
            locations.Add(("Common Start Menu", commonStartMenu));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var quickLaunch = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch");
            if (Directory.Exists(quickLaunch))
                locations.Add(("Quick Launch", quickLaunch));
        }

        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!string.IsNullOrEmpty(recent)) locations.Add(("Recent Items", recent));

        return locations;
    }

    private static string ResolveShortcutTarget(string lnkPath)
    {
        var link = (IShellLink)new ShellLink();
        var file = (IPersistFile)link;
        try
        {
            file.Load(lnkPath, 0);

            // Use an extended-length buffer rather than the legacy MAX_PATH (260). A target
            // longer than 260 chars would otherwise be truncated, fail the existence check,
            // and the shortcut would be wrongly reported as broken (a destructive false
            // positive — the user could delete a perfectly valid shortcut).
            var sb = new char[short.MaxValue];
            link.GetPath(sb, sb.Length, IntPtr.Zero, 0);
            var target = new string(sb).TrimEnd('\0');

            if (target.Contains('%'))
                target = Environment.ExpandEnvironmentVariables(target);

            return target;
        }
        finally
        {
            // LEAK-002: Only release the original COM object once. IPersistFile
            // is the same underlying COM object (QueryInterface), so releasing
            // both would double-decrement the ref count.
            System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003,
            pFrom = path + '\0' + '\0',
            fFlags = 0x0040 | 0x0010
        };
        // SHFileOperation returns non-zero (and/or sets fAnyOperationsAborted) on
        // failure WITHOUT throwing. Returning that result lets the caller avoid
        // counting a silently-failed recycle as a successful deletion.
        var rc = SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>Renders the broken-shortcut list as CSV with a header row.</summary>
    /// <remarks>
    /// Both paths matter and neither is optional: the shortcut path is what would be deleted, and the target
    /// path is the evidence for why — a file that no longer exists. An export naming only one of them cannot
    /// be checked by whoever reads it.
    /// </remarks>
    public static string ToCsv(IEnumerable<BrokenShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        var sb = new StringBuilder();
        Csv.AppendRow(sb, "Name", "Location", "Shortcut path", "Missing target", "Selected");
        foreach (var s in shortcuts)
        {
            Csv.AppendRow(sb, s.Name, s.Location, s.ShortcutPath, s.TargetPath,
                s.IsSelected ? "yes" : "no");
        }
        return sb.ToString();
    }
}
