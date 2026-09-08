// SysManager · DeepCleanupRealRootsTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// One scan of the real machine, shared by both tests below.
/// </summary>
/// <remarks>
/// A full walk of this machine's caches took 45 seconds when measured, so two tests doing their own would
/// add a minute and a half to a suite that already runs six. Shared and read-only, the same arrangement
/// #2167 introduced in the unit project for the same reason.
/// </remarks>
public sealed class RealMachineScanFixture : IAsyncLifetime
{
    /// <summary>The one scan's categories. Read-only — both tests share this instance.</summary>
    public IReadOnlyList<CleanupCategory> Categories { get; private set; } = [];

    public async ValueTask InitializeAsync()
        => Categories = await new DeepCleanupService().ScanAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The one claim about Deep Cleanup's scan that only a live machine can make: the default roots really
/// are this machine's folders, and a scan over them produces the same catalogue the unit tests describe.
/// </summary>
/// <remarks>
/// Everything else moved the other way. Until #2176 the unit project scanned the real disk for all of it,
/// which cost 183 seconds on a workstation with real caches and — worse — meant the assertions could only
/// be shapes that hold anywhere. With <c>ICleanupRoots</c> the unit tests point at a tree they build and
/// assert exact counts in 0.25 seconds; what is left over is this, and it belongs here, where a
/// live-system dependency is the point of the project rather than a compromise.
/// <para>Deliberately narrow. Neither test asserts that any particular folder EXISTS — a fresh runner has
/// almost none of them — only that the definitions were built from the machine's own paths and that the
/// walk completes. A test that required content would fail on CI and pass on a developer's machine for
/// reasons neither chose, which is the state this replaced.</para>
/// </remarks>
public class DeepCleanupRealRootsTests(RealMachineScanFixture scan) : IClassFixture<RealMachineScanFixture>
{
    private readonly IReadOnlyList<CleanupCategory> _scanned = scan.Categories;

    [Fact]
    public void DefaultRoots_ProduceTheSameCatalogueOverTheRealMachine()
    {
        // The same floor the unit project's catalogue tests use, asserted here against real roots so a
        // definition that only resolves on a temp tree cannot pass unnoticed.
        Assert.True(_scanned.Count >= 10, $"expected >= 10 categories, got {_scanned.Count}");
        Assert.All(_scanned, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        Assert.Equal(_scanned.Count, _scanned.Select(c => c.Name).Distinct().Count());
    }

    /// <summary>
    /// Every path the scan resolved is under one of this machine's own roots.
    /// </summary>
    /// <remarks>
    /// All EIGHT roots are anchors, not just the seven folders. The first version of this test used only
    /// the folders and failed on two legitimate paths — a Recycle Bin and a Steam shader cache on a second
    /// drive — because those come from <c>FixedDriveRoots</c> and <c>RecycleBinPaths</c>. The failure was
    /// the test's, not the scan's, and it is the reason this list is derived from the interface rather than
    /// written out by hand.
    /// <para>Asserted over the folders that DO exist, because that is the subset the scan reports at all.
    /// <c>NotEmpty</c> first, so that an empty result says so instead of passing vacuously — which is what
    /// pointing the seam's default somewhere else would look like.</para>
    /// </remarks>
    [Fact]
    public void DefaultRoots_ResolveUnderTheMachinesOwnFolders()
    {
        var roots = new SystemCleanupRoots();
        var anchors = new List<string>
        {
            roots.LocalAppData, roots.ProgramData, roots.SystemDrive,
            roots.WindowsDirectory, roots.UserTemp, roots.ProgramFilesX86, roots.ProgramFiles,
        };
        anchors.AddRange(roots.FixedDriveRoots);
        anchors.AddRange(roots.RecycleBinPaths);

        var resolved = _scanned.SelectMany(c => c.Paths).ToList();

        Assert.NotEmpty(resolved);
        Assert.All(resolved, p => Assert.True(
            anchors.Any(a => p.StartsWith(a, StringComparison.OrdinalIgnoreCase)),
            $"a resolved cleanup path is under none of this machine's roots: {Path.GetFileName(p)}"));
    }
}
