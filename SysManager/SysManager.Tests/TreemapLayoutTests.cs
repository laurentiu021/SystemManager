// SysManager · TreemapLayoutTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// <see cref="TreemapLayout"/> lays weights out as rectangles that tile the area, each sized by its weight (#1592).
/// </summary>
public sealed class TreemapLayoutTests
{
    private const double Tolerance = 1e-6;

    /// <summary>The example of the squarified-treemap paper: seven weights in a 6 by 4 rectangle.</summary>
    private static readonly double[] PaperWeights = [6, 6, 4, 3, 2, 2, 1];

    private static double Area(Rect r) => r.Width * r.Height;

    private static double Aspect(Rect r) => Math.Max(r.Width / r.Height, r.Height / r.Width);

    [Fact]
    public void OneWeight_FillsTheWholeArea()
    {
        var bounds = new Rect(0, 0, 940, 240);

        var rect = Assert.Single(TreemapLayout.Squarify([38], bounds));

        Assert.Equal(bounds, rect);
    }

    [Fact]
    public void EachArea_IsInProportionToItsWeight()
    {
        var bounds = new Rect(0, 0, 600, 400);

        var rects = TreemapLayout.Squarify(PaperWeights, bounds);

        var total = PaperWeights.Sum();
        for (var i = 0; i < rects.Length; i++)
            Assert.Equal(PaperWeights[i] / total * Area(bounds), Area(rects[i]), 6);
    }

    [Fact]
    public void TheRectangles_TileTheArea_WithoutOverlapOrGap()
    {
        var bounds = new Rect(10, 20, 940, 240);
        double[] weights = [38, 22, 14, 6, 4.5, 3, 1.2, 1.1];

        var rects = TreemapLayout.Squarify(weights, bounds);

        Assert.Equal(Area(bounds), rects.Sum(Area), 6);
        foreach (var r in rects)
        {
            Assert.True(r.Left >= bounds.Left - Tolerance && r.Top >= bounds.Top - Tolerance
                        && r.Right <= bounds.Right + Tolerance && r.Bottom <= bounds.Bottom + Tolerance,
                $"{r} is outside {bounds}");
        }
        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
            {
                var overlap = Rect.Intersect(rects[i], rects[j]);
                Assert.True(overlap.IsEmpty || Area(overlap) < Tolerance, $"{rects[i]} overlaps {rects[j]}");
            }
        }
    }

    [Fact]
    public void TheLayout_IsSquarerThanSlicing()
    {
        // Strips across the whole width would make the smallest of these 600 by 17, about 36 to 1. Laid out square,
        // the worst is the last one, 60 by 167, just under 2.8 to 1.
        var rects = TreemapLayout.Squarify(PaperWeights, new Rect(0, 0, 600, 400));

        Assert.All(rects, r => Assert.True(Aspect(r) <= 2.8, $"{r} is {Aspect(r):F2} to 1"));
    }

    [Fact]
    public void TheRectangles_ComeBackInTheOrderOfTheWeights()
    {
        var rects = TreemapLayout.Squarify([1, 6, 3], new Rect(0, 0, 100, 100));

        Assert.Equal(1000, Area(rects[0]), 6);
        Assert.Equal(6000, Area(rects[1]), 6);
        Assert.Equal(3000, Area(rects[2]), 6);
    }

    [Theory]
    [InlineData(new[] { 0d, 6, 4 })]
    [InlineData(new[] { 6d, 0, 4 })]
    [InlineData(new[] { 6d, 4, 0 })]
    public void AZeroWeight_GetsARectangleWithNoSize_AndTheRestStillFill(double[] weights)
    {
        var bounds = new Rect(0, 0, 500, 200);

        var rects = TreemapLayout.Squarify(weights, bounds);

        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] == 0) Assert.Equal(0, Area(rects[i]));
            else Assert.Equal(weights[i] / 10 * Area(bounds), Area(rects[i]), 6);
        }
    }

    [Fact]
    public void AZeroWeight_DoesNotChangeTheLayoutOfTheOthers()
    {
        // A zero has no area to compare, so taken into a row it would make every row look perfect and the layout
        // would collapse into one strip.
        var bounds = new Rect(0, 0, 600, 400);

        var without = TreemapLayout.Squarify(PaperWeights, bounds);
        var with = TreemapLayout.Squarify([0, .. PaperWeights], bounds);

        Assert.Equal(without, with.Skip(1));
    }

    [Fact]
    public void OneHugeWeightAndManyTiny_EachGetTheirShare()
    {
        double[] weights = [1_000_000, .. Enumerable.Repeat(1d, 50)];
        var bounds = new Rect(0, 0, 940, 240);

        var rects = TreemapLayout.Squarify(weights, bounds);

        var total = weights.Sum();
        Assert.Equal(1_000_000 / total * Area(bounds), Area(rects[0]), 6);
        Assert.All(rects.Skip(1), r => Assert.True(Area(r) > 0));
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(940, 0)]
    public void AnAreaWithNoSize_LaysNothingOut(double width, double height)
    {
        var rects = TreemapLayout.Squarify([6, 4], new Rect(0, 0, width, height));

        Assert.All(rects, r => Assert.Equal(0, Area(r)));
    }

    [Fact]
    public void WeightsThatAreAllZero_LayNothingOut()
    {
        var rects = TreemapLayout.Squarify([0, 0, 0], new Rect(0, 0, 940, 240));

        Assert.Equal(3, rects.Length);
        Assert.All(rects, r => Assert.Equal(0, Area(r)));
    }

    [Fact]
    public void NoWeights_LayNothingOut() => Assert.Empty(TreemapLayout.Squarify([], new Rect(0, 0, 940, 240)));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AWeightThatIsNotASize_IsRejected(double weight)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TreemapLayout.Squarify([6, weight], new Rect(0, 0, 100, 100)));

    [Fact]
    public void Null_IsRejected() => Assert.Throws<ArgumentNullException>(() => TreemapLayout.Squarify(null!, new Rect(0, 0, 1, 1)));
}
