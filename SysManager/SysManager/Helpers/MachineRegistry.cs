// SysManager · MachineRegistry — the HKLM root a store that keeps records there is built over
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;

namespace SysManager.Helpers;

/// <summary>
/// The registry root a store that keeps its records under HKLM is built over when nothing else is given: HKLM itself,
/// or nothing at all.
/// </summary>
/// <remarks>
/// Settable for the same reason <see cref="AppDataFolders.Local"/> is: <c>TestAssemblyInit</c> in both test projects
/// sets it to null, once, before any test runs, so a construction no seam scan can see (a view model that quietly
/// builds its own store, an object the container makes) never reads or writes the PC's real records. A store over a
/// null root keeps nothing and reads nothing as kept. <see cref="Services.ServiceStartupLedgerService"/>'s public
/// constructor reads it; a test that needs records passes a stand-in root of its own.
/// </remarks>
internal static class MachineRegistry
{
    internal static RegistryKey? Root { get; set; } = Registry.LocalMachine;
}
