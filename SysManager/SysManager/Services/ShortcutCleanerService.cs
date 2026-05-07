// SysManager · ShortcutCleanerService — scans for broken .lnk shortcuts
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Scans common locations for .lnk shortcuts whose targets no longer exist.
/// Supports deletion to Recycle Bin or permanent delete.
/// </summary>
public sealed class ShortcutCleanerService
{
    /// <summary>
    /// Scans all common shortcut locations and returns broken shortcuts.
    /// </summary>
    public Task<IReadOnlyList<BrokenShortcut>> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => Scan(progress, ct), ct);

    private static IReadOnlyList<BrokenShortcut> Scan(
        IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<BrokenShortcut>();
        var locations = GetScanLocations();

        foreach (var (label, path) in locations)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

            progress?.Report($"Scanning {label}...");

            try
            {
                foreach (var lnk in Directory.EnumerateFiles(path, "*.lnk", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var target = ResolveShortcutTarget(lnk);
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        // Skip URLs, shell objects, and special targets
                        if (target.StartsWith("::") || target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Check if target exists (file or directory)
                        if (!File.Exists(target) && !Directory.Exists(target))
                        {
                            results.Add(new BrokenShortcut
                            {
                                Name = Path.GetFileNameWithoutExtension(lnk),
                                ShortcutPath = lnk,
                                TargetPath = target,
                                Location = label
                            });
                        }
                    }
                    catch (IOException) { /* skip inaccessible shortcut */ }
                    catch (UnauthorizedAccessException) { /* skip protected shortcut */ }
                    catch (COMException) { /* skip corrupted shortcut */ }
                }
            }
            catch (IOException ex) { Log.Debug(ex, "Failed to scan {Location}", label); }
            catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Access denied scanning {Location}", label); }
        }

        return results;
    }

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
                    MoveToRecycleBin(s.ShortcutPath);
                else
                    File.Delete(s.ShortcutPath);

                deleted++;
            }
            catch (IOException ex) { Log.Warning(ex, "Failed to delete shortcut: {Path}", s.ShortcutPath); }
            catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Access denied deleting shortcut: {Path}", s.ShortcutPath); }
        }
        return deleted;
    }

    private static List<(string Label, string Path)> GetScanLocations()
    {
        var locations = new List<(string, string)>();

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
        file.Load(lnkPath, 0);

        var sb = new char[260];
        link.GetPath(sb, sb.Length, IntPtr.Zero, 0);
        var target = new string(sb).TrimEnd('\0');

        if (target.Contains('%'))
            target = Environment.ExpandEnvironmentVariables(target);

        return target;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static void MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003,
            pFrom = path + '\0' + '\0',
            fFlags = 0x0040 | 0x0010
        };
        SHFileOperation(ref op);
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
}
