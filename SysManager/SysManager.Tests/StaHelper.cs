// SysManager · StaHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Runs an action on a dedicated STA thread. Required for tests that touch
/// WPF Application / Dispatcher. xUnit's default runner is MTA.
/// </summary>
public static class StaHelper
{
    public static void Run(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (captured != null) throw captured;
    }
}
