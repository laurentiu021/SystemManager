// SysManager · ChartWindowOption
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>A span the Ping chart can show, and how its picker names it.</summary>
/// <param name="Seconds">How far back the chart reaches.</param>
/// <remarks>
/// The picker listed the bare seconds, 60, 300, 600 and 900, beside "Ping every 1 s", so nobody could tell whether 300
/// was seconds, samples or minutes (#2884).
/// </remarks>
public sealed record ChartWindowOption(int Seconds)
{
    /// <summary>The span in minutes, as the picker reads it: "5 min" for 300 seconds.</summary>
    public string Label => $"{Seconds / 60} min";

    /// <summary>The label, which is also what a screen reader says for the picker's entry.</summary>
    public override string ToString() => Label;
}
