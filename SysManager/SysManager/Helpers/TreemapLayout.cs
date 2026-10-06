// SysManager · TreemapLayout
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;

namespace SysManager.Helpers;

/// <summary>
/// Squarified treemap layout: one rectangle per weight, with areas in proportion to the weights and shapes as close
/// to square as their order allows (#1592).
/// </summary>
/// <remarks>
/// <para>The algorithm is Bruls, Huizing and van Wijk's ("Squarified Treemaps", 2000). Weights are taken largest
/// first, the order the Disk Analyzer list already has, and laid in rows along the shorter side of the space that is
/// left. A weight joins the current row for as long as that makes the row's worst aspect ratio no worse; otherwise
/// the row is fixed and a new one starts beside it.</para>
/// <para>Pure, so every case is testable without a window. One weight fills the whole area, a weight of zero gets a
/// rectangle with no size, and an area with no size lays nothing out. The last rectangle of each row, and the last row,
/// are stretched to the edge, so rounding never leaves a gap.</para>
/// </remarks>
internal static class TreemapLayout
{
    /// <summary>
    /// One rectangle per weight, in the same order, tiling <paramref name="bounds"/>. Weights are expected largest
    /// first; in any other order the layout is still complete, only less square.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A weight is negative, infinite or not a number.</exception>
    public static Rect[] Squarify(IReadOnlyList<double> weights, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(weights);

        var total = 0d;
        foreach (var weight in weights)
        {
            if (!double.IsFinite(weight) || weight < 0)
                throw new ArgumentOutOfRangeException(nameof(weights), weight, "A weight must be a finite number of zero or more.");
            total += weight;
        }

        var rects = new Rect[weights.Count];
        for (var i = 0; i < rects.Length; i++) rects[i] = new Rect(bounds.X, bounds.Y, 0, 0);
        if (total <= 0 || bounds.IsEmpty || !(bounds.Width > 0) || !(bounds.Height > 0)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return rects;

        // Weights become areas in the bounds' own units, so a row's thickness is its area over the side it runs along.
        var scale = bounds.Width * bounds.Height / total;
        var free = bounds;
        var next = 0;

        while (next < weights.Count && free.Width > 0 && free.Height > 0)
        {
            var side = Math.Min(free.Width, free.Height);
            var end = next;
            double sum = 0, smallest = double.MaxValue, largest = 0, worst = double.MaxValue;

            while (end < weights.Count)
            {
                var area = weights[end] * scale;
                if (area <= 0)
                {
                    end++;
                    continue;
                }

                var candidateSum = sum + area;
                var candidateWorst = Worst(candidateSum, Math.Min(smallest, area), Math.Max(largest, area), side);
                if (sum > 0 && candidateWorst > worst) break;

                sum = candidateSum;
                smallest = Math.Min(smallest, area);
                largest = Math.Max(largest, area);
                worst = candidateWorst;
                end++;
            }

            if (sum <= 0) break;
            free = LayRow(weights, scale, next, end, sum, free, last: !AnyWithArea(weights, end, weights.Count), rects);
            next = end;
        }

        return rects;
    }

    /// <summary>
    /// The worst aspect ratio of a row of areas totalling <paramref name="sum"/> laid along a side of length
    /// <paramref name="side"/>: the ratio of its smallest or its largest rectangle, whichever is further from square.
    /// </summary>
    private static double Worst(double sum, double smallest, double largest, double side)
    {
        var sideSquared = side * side;
        var sumSquared = sum * sum;
        return Math.Max(sideSquared * largest / sumSquared, sumSquared / (sideSquared * smallest));
    }

    /// <summary>Places one row against the shorter side of <paramref name="free"/> and returns the space left.</summary>
    private static Rect LayRow(
        IReadOnlyList<double> weights, double scale, int from, int to, double sum, Rect free, bool last, Rect[] rects)
    {
        if (free.Width >= free.Height)
        {
            // A column down the left edge.
            var width = last ? free.Width : Math.Min(free.Width, sum / free.Height);
            var y = free.Y;
            var lastPlaced = LastWithArea(weights, from, to);
            for (var i = from; i < to; i++)
            {
                var area = weights[i] * scale;
                if (area <= 0) continue;
                var height = i == lastPlaced ? free.Bottom - y : area / width;
                rects[i] = new Rect(free.X, y, width, Math.Max(0, height));
                y += height;
            }
            return new Rect(free.X + width, free.Y, Math.Max(0, free.Width - width), free.Height);
        }
        else
        {
            // A row across the top edge.
            var height = last ? free.Height : Math.Min(free.Height, sum / free.Width);
            var x = free.X;
            var lastPlaced = LastWithArea(weights, from, to);
            for (var i = from; i < to; i++)
            {
                var area = weights[i] * scale;
                if (area <= 0) continue;
                var width = i == lastPlaced ? free.Right - x : area / height;
                rects[i] = new Rect(x, free.Y, Math.Max(0, width), height);
                x += width;
            }
            return new Rect(free.X, free.Y + height, free.Width, Math.Max(0, free.Height - height));
        }
    }

    private static int LastWithArea(IReadOnlyList<double> weights, int from, int to)
    {
        for (var i = to - 1; i >= from; i--)
            if (weights[i] > 0) return i;
        return -1;
    }

    private static bool AnyWithArea(IReadOnlyList<double> weights, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (weights[i] > 0) return true;
        return false;
    }
}
