// SysManager · WindowsThemeService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

public enum WindowsTheme { Light, Dark }

/// <summary>
/// Switches the per-user Windows light/dark theme (HKCU, no admin) and notifies the
/// shell via a WM_SETTINGCHANGE("ImmersiveColorSet") broadcast so it applies immediately
/// without signing out. Distinct from <see cref="ThemeService"/>, which themes
/// SysManager's OWN WPF UI — this touches the Windows OS theme.
///
/// Also loads/saves the fixed-time dark-mode schedule and exposes the pure
/// <see cref="ShouldBeDark"/> evaluation (handles the overnight wrap). The schedule is
/// driven by a timer in the ViewModel and only runs while the app is alive.
///
/// Fully reversible: writing the opposite DWORD restores the previous theme.
/// </summary>
public sealed partial class WindowsThemeService : IWindowsThemeService
{
    private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsValue = "AppsUseLightTheme";
    private const string SystemValue = "SystemUsesLightTheme";

    private static readonly string SchedulePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SysManager", "darkmode-schedule.json");

    /// <summary>Read the current Windows app theme (absent value = Light, the OS default).</summary>
    public WindowsTheme GetCurrentTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            int apps = key?.GetValue(AppsValue) as int? ?? 1;
            return apps == 0 ? WindowsTheme.Dark : WindowsTheme.Light;
        }
        catch (SecurityException ex) { Log.Debug("Windows theme read denied: {Error}", ex.Message); return WindowsTheme.Light; }
        catch (IOException ex) { Log.Debug("Windows theme read I/O error: {Error}", ex.Message); return WindowsTheme.Light; }
    }

    /// <summary>
    /// Set the Windows theme. When <paramref name="includeSystem"/>, also flips the
    /// taskbar/Start. Returns false if the registry write was denied.
    /// </summary>
    public bool SetTheme(bool dark, bool includeSystem)
    {
        int value = dark ? 0 : 1;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            if (key is null) { Log.Warning("Windows theme: Personalize key unavailable"); return false; }

            key.SetValue(AppsValue, value, RegistryValueKind.DWord);
            if (includeSystem) key.SetValue(SystemValue, value, RegistryValueKind.DWord);

            BroadcastImmersiveColorSet();
            Log.Information("Windows theme set to {Mode} (system={System})", dark ? "Dark" : "Light", includeSystem);
            return true;
        }
        catch (UnauthorizedAccessException ex) { Log.Warning(ex, "Windows theme write denied"); return false; }
        catch (SecurityException ex) { Log.Warning(ex, "Windows theme write security error"); return false; }
        catch (IOException ex) { Log.Warning(ex, "Windows theme write I/O error"); return false; }
        catch (ArgumentException ex) { Log.Warning(ex, "Windows theme write invalid value"); return false; }
    }

    /// <summary>Load the saved schedule, or defaults if none exists / it's unreadable.</summary>
    public DarkModeSchedule LoadSchedule()
    {
        try
        {
            if (File.Exists(SchedulePath))
            {
                string json = File.ReadAllText(SchedulePath);
                var s = JsonSerializer.Deserialize<DarkModeSchedule>(json);
                if (s is not null) return s;
            }
        }
        catch (IOException ex) { Log.Debug("Dark-mode schedule read failed: {Error}", ex.Message); }
        catch (JsonException ex) { Log.Debug("Dark-mode schedule parse failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Debug("Dark-mode schedule access denied: {Error}", ex.Message); }
        return new DarkModeSchedule();
    }

    /// <summary>Persist the schedule as indented JSON in the app's roaming AppData folder.</summary>
    public void SaveSchedule(DarkModeSchedule schedule)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchedulePath)!);
            string json = JsonSerializer.Serialize(schedule, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SchedulePath, json);
        }
        catch (IOException ex) { Log.Warning("Dark-mode schedule save failed: {Error}", ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warning("Dark-mode schedule save denied: {Error}", ex.Message); }
    }

    /// <summary>
    /// Pure: should the theme be DARK now? Dark window = half-open [darkStart, lightStart),
    /// handling the overnight wrap (darkStart &gt; lightStart). Equal times = always light
    /// (empty window → schedule is a no-op rather than flipping every tick).
    /// At exactly darkStart → dark; at exactly lightStart → light.
    /// </summary>
    public static bool ShouldBeDark(TimeOnly now, TimeOnly darkStart, TimeOnly lightStart)
    {
        if (darkStart == lightStart) return false;
        return darkStart < lightStart
            ? now >= darkStart && now < lightStart        // same-day window
            : now >= darkStart || now < lightStart;       // overnight wrap
    }

    private static void BroadcastImmersiveColorSet()
    {
        try
        {
            _ = NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE,
                IntPtr.Zero, "ImmersiveColorSet", NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Debug("Windows theme: ImmersiveColorSet broadcast unavailable: {Error}", ex.Message);
        }
    }

    private static partial class NativeMethods
    {
        internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        internal const uint WM_SETTINGCHANGE = 0x001A;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "SendMessageTimeoutW")]
        internal static partial IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
