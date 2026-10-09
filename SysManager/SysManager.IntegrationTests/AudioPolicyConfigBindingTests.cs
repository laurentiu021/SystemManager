// SysManager · AudioPolicyConfigBindingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// The per-app routing interface binds on a real Windows, and reads a route back (#2584, #2088).
/// </summary>
/// <remarks>
/// A genuine system dependency, so it lives here: the interface is undocumented, and the only proof that SysManager asks
/// the right object for it is asking. Routing never turned on from its first release, because it asked the
/// policy-config client, which answers to neither routing IID. The interface lives on the Windows Runtime factory
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>.
/// <para>These only read. Setting a route moves a real app's sound, so a set and its read-back are tried on real audio
/// hardware rather than on a build machine.</para>
/// </remarks>
public class AudioPolicyConfigBindingTests
{
    private const string FactoryKey =
        @"SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\Windows.Media.Internal.AudioPolicyConfig";

    [Fact]
    public void TheRoutingInterface_Binds_WhereWindowsHasIt()
    {
        SkipUnlessWindowsHasTheFactory();

        if (AudioPolicyConfigFactory.TryCreate() is null)
            Assert.Fail("Windows has the routing factory, but SysManager could not bind the routing interface on it, so "
                        + "every app row falls back to the button that opens Windows' settings");
    }

    [Fact]
    public void ThisProcess_WhichNothingRouted_ReadsAsFollowingTheDefault()
    {
        SkipUnlessWindowsHasTheFactory();

        // The output devices are listed first, as the tab does before it reads a route. Windows answers E_INVALIDARG
        // (0x80070057) for the route of a process that has not opened the audio device API yet: measured on Windows 11
        // build 26200, the same read returned that before the listing and "" after it.
        using var audio = new AudioMixerService();
        if (audio.GetRenderDevices().Count == 0)
            Assert.Skip("This machine has no active output device, so Windows keeps no route to read.");

        var config = AudioPolicyConfigFactory.TryCreate();
        if (config is null) Assert.Fail("the routing interface did not bind, so there is no route to read");

        var route = AudioPolicyConfigFactory.ReadPersistedDefaultEndpoint(config, (uint)Environment.ProcessId, out var hr);

        // Empty is "read, and no override". Null would be "could not read", which the picker shows as unknown.
        Assert.True(hr >= 0, $"Windows refused to read this process's route: 0x{hr:X8}");
        Assert.Equal(string.Empty, route);
    }

    /// <summary>A reported skip on a Windows without the factory, rather than a pass that bound nothing.</summary>
    private static void SkipUnlessWindowsHasTheFactory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(FactoryKey);
        if (key is null)
            Assert.Skip("This Windows has no Windows.Media.Internal.AudioPolicyConfig factory, so there is no "
                        + "routing interface to bind.");
    }
}
