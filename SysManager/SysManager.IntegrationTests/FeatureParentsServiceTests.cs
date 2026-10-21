// SysManager · FeatureParentsServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <see cref="FeatureParentsService"/> against the real DISM API, read-only: the declarations, the session and the
/// one-byte packing of <c>DismFeature</c> show only in a real call (#2834). DISM answers only an administrator.
/// </summary>
public class FeatureParentsServiceTests
{
    // Features with parents, the first of them this Windows has. IIS-ASPNET45 comes first: it has six direct parents,
    // so it reads past the first DismFeature, and what IIS needs is known.
    private static readonly string[] Candidates =
        ["IIS-ASPNET45", "IIS-WebServer", "Microsoft-Hyper-V-Tools-All", "NetFx4Extended-ASPNET45"];

    [Fact]
    public async Task ReadAsync_ReadsTheFeaturesAFeatureNeeds_AsDismNamesThem()
    {
        if (!AdminHelper.IsElevated())
            Assert.Skip("The DISM API answers only an administrator.");

        var service = new FeatureParentsService();
        foreach (var name in Candidates)
        {
            var parents = await service.ReadAsync(name);
            if (parents is not { Count: > 0 }) continue;

            Assert.All(parents, p => Assert.Matches(@"\A[\w.\-]{1,128}\z", p.Name));
            if (name.StartsWith("IIS-", StringComparison.Ordinal))
                Assert.Contains(parents, p => p.Name == "IIS-WebServerRole");
            if (name == "IIS-ASPNET45")
                Assert.Contains(parents, p => p.Name == "NetFx4Extended-ASPNET45");
            return;
        }
        Assert.Skip("None of the features that have parents is on this Windows.");
    }

    [Fact]
    public async Task ReadAsync_AFeatureWindowsDoesNotKnow_KnowsNothing()
    {
        if (!AdminHelper.IsElevated())
            Assert.Skip("The DISM API answers only an administrator.");

        Assert.Null(await new FeatureParentsService().ReadAsync("SysManager-No-Such-Feature"));
    }
}
