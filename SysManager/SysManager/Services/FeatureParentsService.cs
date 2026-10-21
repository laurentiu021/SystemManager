// SysManager · FeatureParentsService
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Runtime.InteropServices;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Reads, through the DISM API, which optional features another one needs: the parents
/// <c>Enable-WindowsOptionalFeature -All</c> turns on with it (#2834).
/// </summary>
/// <remarks>
/// The PowerShell cmdlets do not report parents. <c>DismGetFeatureParent</c> in dismapi.dll does, for the running
/// Windows, and needs administrator rights, as every DISM call on it does. It returns a feature's direct parents with
/// their state, so the walk asks again for each parent, because <c>-All</c> turns on its parents too, and turning
/// the feature off leaves them all on.
/// <c>IIS-ASPNET45</c> has six direct parents, and they have their own.
/// </remarks>
public sealed partial class FeatureParentsService : IFeatureParentsService
{
    /// <summary>The image name dismapi.h defines for the running Windows, <c>DISM_ONLINE_IMAGE</c>.</summary>
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";

    // DismLogLevel and DismPackageIdentifier values from dismapi.h.
    private const int DismLogErrors = 0;
    private const int DismPackageNone = 0;

    // DismPackageFeatureState values from dismapi.h.
    internal const int UninstallPending = 1;
    internal const int Installed = 4;
    internal const int InstallPending = 5;

    /// <summary>More parents than any feature has, so a walk that keeps finding new ones ends.</summary>
    internal const int MaxParents = 64;

    // DismInitialize and DismShutdown are process-wide, so one walk at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeatureParent>?> ReadAsync(string featureName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(featureName), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<FeatureParent>? Read(string featureName)
    {
        try
        {
            var hr = NativeMethods.DismInitialize(DismLogErrors, null, null);
            if (hr < 0)
            {
                Log.Warning("The DISM API did not start: 0x{HResult:X8}", hr);
                return null;
            }
            try
            {
                hr = NativeMethods.DismOpenSession(OnlineImage, null, null, out var session);
                if (hr < 0)
                {
                    Log.Warning("The DISM API could not open the running Windows: 0x{HResult:X8}", hr);
                    return null;
                }
                try
                {
                    return Walk(featureName, name => ParentsOf(session, name));
                }
                finally
                {
                    NativeMethods.DismCloseSession(session);
                }
            }
            finally
            {
                NativeMethods.DismShutdown();
            }
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning("The DISM API is not available: {Error}", ex.Message);
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Warning("The DISM API is not available: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Every feature <paramref name="featureName"/> needs, nearest first and each once, asking
    /// <paramref name="parentsOf"/> again for each parent. Null when a question gets no answer.
    /// </summary>
    internal static IReadOnlyList<FeatureParent>? Walk(
        string featureName, Func<string, IReadOnlyList<(string Name, int State)>?> parentsOf)
    {
        var found = new List<FeatureParent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { featureName };
        var next = new Queue<string>([featureName]);
        while (next.Count > 0 && found.Count < MaxParents)
        {
            if (parentsOf(next.Dequeue()) is not { } parents) return null;
            foreach (var (name, state) in parents)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                var parent = new FeatureParent(name, IsOn(state), IsPending(state));
                found.Add(parent);
                next.Enqueue(name);
            }
        }
        return found;
    }

    internal static bool IsOn(int state) => state is Installed or InstallPending;

    internal static bool IsPending(int state) => state is UninstallPending or InstallPending;

    private static IReadOnlyList<(string Name, int State)>? ParentsOf(uint session, string name)
    {
        var hr = NativeMethods.DismGetFeatureParent(
            session, name, null, DismPackageNone, out var features, out var count);
        if (hr < 0)
        {
            Log.Warning("DISM could not read what {Feature} needs: 0x{HResult:X8}", name, hr);
            return null;
        }
        try
        {
            // dismapi.h packs its structures to one byte, so a DismFeature is the name's pointer, then its state.
            var size = IntPtr.Size + sizeof(int);
            var parents = new List<(string Name, int State)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = IntPtr.Add(features, i * size);
                parents.Add(
                    (Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", Marshal.ReadInt32(item, IntPtr.Size)));
            }
            return parents;
        }
        finally
        {
            if (features != IntPtr.Zero) NativeMethods.DismDelete(features);
        }
    }

    // Loaded from System32 only: dismapi.dll is not one of the DLLs Windows always takes from there, and SysManager
    // runs elevated, so a copy beside the exe must not be loaded in its place.
    private static partial class NativeMethods
    {
        [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

        [LibraryImport("dismapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismShutdown();

        [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismOpenSession(
            string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

        [LibraryImport("dismapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismCloseSession(uint session);

        [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismGetFeatureParent(
            uint session, string featureName, string? identifier, int packageIdentifier, out IntPtr feature,
            out uint count);

        [LibraryImport("dismapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int DismDelete(IntPtr dismStructure);
    }
}
