// SysManager · PolicyConfigClientTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// The guarded wrapper over the undocumented <c>IPolicyConfig</c> that moves all sound to another device (#1588).
/// Nothing here creates the real client or calls it: a test that did would change this machine's output device.
/// What can be pinned without it is the refusal of anything but the client, and the vtable slot the switch
/// calls.
/// </summary>
public class PolicyConfigClientTests
{
    [Fact]
    public void AnythingButTheClient_IsRefused()
        => Assert.False(PolicyConfigClient.SetDefaultEndpoint(new object(), "{0.0.0.00000000}.{any}"));

    /// <summary>
    /// <c>SetDefaultEndpoint</c> is the eleventh method of the published layout, after ten that read and set
    /// formats, periods, share modes and properties — slot 13 once IUnknown's three are counted. The slot after
    /// it, <c>SetEndpointVisibility</c>, hides a device, so a placeholder lost or added above it would call that
    /// instead.
    /// </summary>
    [Fact]
    public void TheSwitch_SitsInItsPublishedSlot()
    {
        var policyConfig = typeof(PolicyConfigClient).GetNestedType("IPolicyConfig", BindingFlags.NonPublic)!;

        Assert.Equal(13, ComVtable.SlotOf(policyConfig, "SetDefaultEndpoint"));
    }
}
