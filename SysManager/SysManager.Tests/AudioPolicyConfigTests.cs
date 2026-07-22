// SysManager · AudioPolicyConfigTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for the pure, easy-to-get-wrong string helpers in <see cref="AudioPolicyConfigFactory"/> —
/// the endpoint-id wrapping and the process token. The COM activation path itself is undocumented
/// and can only be exercised on a real Windows desktop (verified on the laptop workstation), so it
/// is intentionally not unit-tested here; these pin the formatting the routing SET call depends on.
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

        Assert.StartsWith(@"\\?\SWD#MMDEVAPI#", wrapped);
        Assert.Contains(plain, wrapped);
        // The render interface GUID suffix is what the interface keys the endpoint on.
        Assert.EndsWith("{e6327cad-dcec-4949-ae8a-991e976a79d2}", wrapped);
    }

    [Fact]
    public void ToPolicyEndpointId_AlreadyWrapped_PassesThrough()
    {
        var already = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        Assert.Equal(already, AudioPolicyConfigFactory.ToPolicyEndpointId(already));
    }

    [Theory]
    [InlineData(1234u, "1234")]
    [InlineData(1u, "1")]
    public void BuildProcessToken_IsRawPid(uint pid, string expected)
        => Assert.Equal(expected, AudioPolicyConfigFactory.BuildProcessToken(pid));
}
