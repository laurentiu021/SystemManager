// SysManager · WindowFitsScreenTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Interop;

namespace SysManager.IntegrationTests;

/// <summary>
/// The main window fits the screen it opens on before it is first shown (#2704).
/// </summary>
/// <remarks>
/// <c>WindowFitTests</c> pins the rule on given numbers. This runs what the window runs from
/// <c>OnSourceInitialized</c>, <see cref="MainWindow.FitToScreen"/>, on a window handle made and never shown, against
/// the screen this runs on: a window far bigger than any screen comes out on it. A plain window stands in for the main
/// one, whose construction builds every tab.
/// </remarks>
public sealed class WindowFitsScreenTests
{
    [Fact]
    public void AWindowTooBigForItsScreen_ComesOutOnIt_CentredInTheWorkArea()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var window = new Window
            {
                Width = 20000,
                Height = 20000,
                MinWidth = 400,
                MinHeight = 300,
                ShowActivated = false,
            };
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var work = Assert.NotNull(MainWindow.WorkAreaOf(hwnd));
                var before = Assert.NotNull(MainWindow.BoundsOf(hwnd));
                Assert.False(work.Contains(before), $"the window started on the screen, at {before}");

                MainWindow.FitToScreen(window, hwnd);

                var bounds = Assert.NotNull(MainWindow.BoundsOf(hwnd));
                // To the pixel: the size goes through the window's own units on its way back to the screen's.
                Assert.True(Near(work, bounds), $"the window is at {bounds}, where the work area is {work}");
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(400, window.MinWidth);
                Assert.Equal(300, window.MinHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AScreenSmallerThanTheMinimumSize_OpensTheWindowMaximized_WithAMinimumItCanMeet()
    {
        StaHelper.Run(() =>
        {
            AppResources.Ensure();
            var window = new Window
            {
                Width = 20000,
                Height = 20000,
                MinWidth = 20000,
                MinHeight = 20000,
                ShowActivated = false,
            };
            try
            {
                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                var work = Assert.NotNull(MainWindow.WorkAreaOf(hwnd));
                var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;

                MainWindow.FitToScreen(window, hwnd);

                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(work.Width / scale, window.MinWidth, 3);
                Assert.Equal(work.Height / scale, window.MinHeight, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static bool Near(Rect expected, Rect actual) =>
        Math.Abs(expected.Left - actual.Left) <= 1 && Math.Abs(expected.Top - actual.Top) <= 1
        && Math.Abs(expected.Right - actual.Right) <= 1 && Math.Abs(expected.Bottom - actual.Bottom) <= 1;
}
