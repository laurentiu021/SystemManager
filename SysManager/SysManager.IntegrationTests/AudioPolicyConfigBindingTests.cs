// SysManager · AudioPolicyConfigBindingTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.ServiceProcess;
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
        SkipUnlessTheAudioServiceRuns();
        var config = AudioPolicyConfigFactory.TryCreate();
        if (config is null) Assert.Fail("the routing interface did not bind, so there is no route to read");

        // Empty is "read, and no override". Null would be "could not read", which the picker shows as unknown.
        Assert.Equal(string.Empty,
            AudioPolicyConfigFactory.GetPersistedDefaultEndpoint(config, (uint)Environment.ProcessId));
    }

    /// <summary>
    /// A reported skip where the Windows Audio service is not running. It keeps the routes, so without it no route can
    /// be read, and a build machine with no sound hardware may not run it.
    /// </summary>
    private static void SkipUnlessTheAudioServiceRuns()
    {
        ServiceControllerStatus? status;
        try
        {
            using var audio = new ServiceController("Audiosrv");
            status = audio.Status;
        }
        catch (InvalidOperationException)
        {
            status = null;
        }

        if (status != ServiceControllerStatus.Running)
            Assert.Skip($"The Windows Audio service is {status?.ToString() ?? "not installed"} here, so no route can be read.");
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
