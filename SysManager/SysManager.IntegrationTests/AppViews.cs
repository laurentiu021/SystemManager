// SysManager · AppViews
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;

namespace SysManager.IntegrationTests;

/// <summary>
/// The views a test can build on its own: every <see cref="UserControl"/> in <c>SysManager.Views</c> with a
/// parameterless constructor, in name order.
/// </summary>
internal static class AppViews
{
    public static List<Type> Constructible() => typeof(App).Assembly.GetTypes()
        .Where(t => t.Namespace == "SysManager.Views" && typeof(UserControl).IsAssignableFrom(t)
                    && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();
}
