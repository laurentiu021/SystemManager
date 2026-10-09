// SysManager · AudioPolicyConfigTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the pure, easy-to-get-wrong string helpers in <see cref="AudioPolicyConfigFactory"/> —
/// the endpoint-id wrapping and unwrapping, and the process token. The COM path itself is undocumented and
/// needs a real Windows: <c>IntegrationTests.AudioPolicyConfigBindingTests</c> binds it and reads a route, and a
/// route set and read back is tried on real audio hardware. These pin the formatting the write and the read
/// depend on.
/// </summary>
public class AudioPolicyConfigTests
{
    [Fact]
    public void ToPolicyEndpointId_Empty_ReturnsEmpty()
        => Assert.Equal("", AudioPolicyConfigFactory.ToPolicyEndpointId(""));

    [Fact]
    public void ToPolicyEndpointId_WrapsPlainEndpointId()
    {
        var plain = "{0.0.0.00000000}.{a1b2c3d4-0000-0000-0000-000000000000}";
        var wrapped = AudioPolicyConfigFactory.ToPolicyEndpointId(plain);

        // The WHOLE string, not three fragments. StartsWith + Contains + EndsWith all stay true if the
        // interior "#" between the endpoint id and the render interface GUID is dropped or duplicated —
        // exactly the subtle mistake the implementation's doc comment claims is "pinned by a test". The
        // pass-through test below cannot cover it either: its input already carries the SWD prefix, so
        // it returns at the guard and never reaches the string construction.
        Assert.Equal(
            @"\\?\SWD#MMDEVAPI#" + plain + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}",
            wrapped);
    }

    [Fact]
    public void ToPolicyEndpointId_AlreadyWrapped_PassesThrough()
    {
        var already = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        Assert.Equal(already, AudioPolicyConfigFactory.ToPolicyEndpointId(already));
    }

    [Fact]
    public void FromPolicyEndpointId_UnwrapsWhatToPolicyEndpointIdWraps()
    {
        var plain = "{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}";

        Assert.Equal(plain, AudioPolicyConfigFactory.FromPolicyEndpointId(AudioPolicyConfigFactory.ToPolicyEndpointId(plain)));
    }

    [Fact]
    public void FromPolicyEndpointId_ReadsTheFormWindowsHandsBack()
    {
        // Exactly as GetPersistedDefaultAudioEndpoint returned it on Windows 11 build 26200.
        const string persisted =
            @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        Assert.Equal("{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}",
            AudioPolicyConfigFactory.FromPolicyEndpointId(persisted));
    }

    [Fact]
    public void FromPolicyEndpointId_IgnoresTheCaseOfTheWrapper()
    {
        const string persisted =
            @"\\?\swd#mmdevapi#{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}#{E6327CAD-DCEC-4949-AE8A-991E976A79D2}";

        Assert.Equal("{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}",
            AudioPolicyConfigFactory.FromPolicyEndpointId(persisted));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}", "{0.0.0.00000000}.{fb3fb1ab-3cb7-4dfa-a583-23998a32cfc3}")]
    public void FromPolicyEndpointId_LeavesAnUnwrappedIdAlone(string id, string expected)
    {
        // Empty is the read finding no override: the app follows the default, and must stay empty, not become unknown.
        Assert.Equal(expected, AudioPolicyConfigFactory.FromPolicyEndpointId(id));
    }

    [Theory]
    [InlineData(1234u, "1234")]
    [InlineData(1u, "1")]
    public void BuildProcessToken_IsRawPid(uint pid, string expected)
        => Assert.Equal(expected, AudioPolicyConfigFactory.BuildProcessToken(pid));
}
