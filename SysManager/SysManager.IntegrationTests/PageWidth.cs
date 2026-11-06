// SysManager · PageWidth
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.IntegrationTests;

/// <summary>
/// The width a tab's page has beside the sidebar, at the window's default size and at its smallest.
/// </summary>
/// <remarks>
/// The window opens 1280 px wide and can be made no narrower than 960. Its frame takes 8 px on each side and the
/// sidebar 220, so the page is 1,044 px wide at the default and 724 at the smallest. A view laid out off screen at
/// these widths is laid out as it is seen.
/// </remarks>
internal static class PageWidth
{
    /// <summary>The page at the window's default 1280 px.</summary>
    public const int Default = 1044;

    /// <summary>The page at the window's smallest, 960 px.</summary>
    public const int Smallest = 724;
}
