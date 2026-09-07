// SysManager · AppResourcesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.IntegrationTests;

/// <summary>
/// Pins the contract of <see cref="AppResources.Ensure"/>: when it returns, app-scope styles resolve.
/// </summary>
[Collection("Network")] // Application.Current is process-wide; serialize with the other Windows-level tests
public class AppResourcesTests
{
    /// <summary>
    /// After <c>Ensure()</c> returns, <c>{StaticResource Display}</c> resolves.
    /// </summary>
    /// <remarks>
    /// The postcondition, not the mechanism, so it survives a change to how the resources are loaded. It is
    /// also the whole of what the two private copies this replaced failed to deliver: they passed an absolute
    /// URI to <see cref="Application.LoadComponent(Uri)"/>, which accepts only relative ones, and swallowed
    /// the resulting <see cref="ArgumentException"/> in a bare <c>catch</c> — leaving an Application with no
    /// styles and no complaint. Run in isolation against that shape this goes red on the assertion below;
    /// in a suite where something else already loaded App.xaml it cannot, which is why the second test drives
    /// the repeat-call path that actually failed in CI.
    /// </remarks>
    [Fact]
    public void Ensure_ResolvesAppScopeStyles()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            Assert.NotNull(Application.Current);
            Assert.NotNull(Application.Current!.TryFindResource("Display"));
        });
    }

    /// <summary>
    /// A second <c>Ensure()</c>, on a second STA thread, still leaves the styles resolvable.
    /// </summary>
    /// <remarks>
    /// This is the shape the suite actually runs: each view test spawns its own STA thread and calls
    /// <c>Ensure()</c>, so all but the first take the "an Application already exists" path. The old copies
    /// treated that as proof the resources were loaded and returned without checking, which is how a failed
    /// load in the first caller became a <c>XamlParseException</c> in the fourth. Order-independent — it
    /// establishes its own first call rather than relying on another test having run.
    /// </remarks>
    [Fact]
    public void Ensure_StillResolvesStyles_OnASecondCallFromAnotherStaThread()
    {
        StaHelper.Run(AppResources.Ensure);

        StaHelper.Run(() =>
        {
            AppResources.Ensure();

            Assert.NotNull(Application.Current!.TryFindResource("Display"));
        });
    }
}
