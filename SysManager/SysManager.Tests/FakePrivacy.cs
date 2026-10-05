// SysManager · FakePrivacy
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Privacy toggles a test chooses, behind <see cref="IPrivacyService"/>, so a test of the privacy profile or
/// of the Privacy &amp; Telemetry tab does not pass or fail with this machine's own privacy settings.
/// </summary>
internal static class FakePrivacy
{
    /// <summary>
    /// A toggle with this key and state. Its path names the hive, which is what decides whether a write needs
    /// administrator rights, under a key no test writes: the service it comes from is a substitute.
    /// </summary>
    public static PrivacyToggle Toggle(string key, bool on, string hive = "HKCU", string category = "Telemetry") => new()
    {
        Key = key,
        Name = key,
        Description = "a privacy toggle no test writes",
        Category = category,
        RegistryPath = $@"{hive}\Software\SysManagerTests\FakePrivacy",
        ValueName = key,
        EnabledValue = 1,
        DisabledValue = 0,
        IsEnabled = on,
    };

    /// <summary>
    /// A service whose every <see cref="IPrivacyService.LoadToggles"/> returns new toggles in these states, as
    /// the real one reads the registry afresh each time. Every write reports success and changes nothing.
    /// </summary>
    public static IPrivacyService Returning(params PrivacyToggle[] toggles)
    {
        var service = Substitute.For<IPrivacyService>();
        service.LoadToggles().Returns(_ => [.. toggles.Select(Copy)]);
        service.ApplyToggle(Arg.Any<PrivacyToggle>()).Returns(true);
        service.ApplyAll(Arg.Any<IEnumerable<PrivacyToggle>>()).Returns([]);
        return service;
    }

    /// <summary>Every protection off, as on a PC where nobody changed them: a profile built over it has no privacy section.</summary>
    public static IPrivacyService AllOff() => Returning(Toggle("diagnostic-data", on: false), Toggle("tips", on: false));

    private static PrivacyToggle Copy(PrivacyToggle toggle) => new()
    {
        Key = toggle.Key,
        Name = toggle.Name,
        Description = toggle.Description,
        Category = toggle.Category,
        RegistryPath = toggle.RegistryPath,
        ValueName = toggle.ValueName,
        EnabledValue = toggle.EnabledValue,
        DisabledValue = toggle.DisabledValue,
        IsEnabled = toggle.IsEnabled,
    };
}
