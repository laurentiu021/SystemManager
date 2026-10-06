// SysManager · ComVtable
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

/// <summary>
/// Where a method of a <c>[ComImport]</c> interface lands in the COM vtable, read the way the runtime lays it out:
/// IUnknown's three slots first, then the interface's methods in the order they are declared, which is their
/// metadata order. Lets a test pin an interop declaration against the documented layout without a device to call
/// (.NET Framework's <c>Marshal.GetComSlotForMethodInfo</c> does not exist on .NET).
/// </summary>
internal static class ComVtable
{
    /// <summary>The vtable slot of <paramref name="method"/> on an IUnknown-based <paramref name="comInterface"/>.</summary>
    public static int SlotOf(Type comInterface, string method)
    {
        var declared = comInterface.GetMethods().OrderBy(m => m.MetadataToken).ToList();
        var index = declared.FindIndex(m => m.Name == method);
        Assert.True(index >= 0, $"{comInterface.Name} declares no {method}");
        return 3 + index;
    }
}
