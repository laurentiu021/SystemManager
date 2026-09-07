// SysManager · AppResources
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace SysManager.IntegrationTests;

/// <summary>
/// Makes the app-scope styles from <c>App.xaml</c> available to a view constructed on a test's STA thread.
/// A view instantiated without them throws <c>XamlParseException</c> on its first
/// <c>{StaticResource Display}</c>, so this is a precondition for every view test rather than a nicety.
/// </summary>
/// <remarks>
/// Replaces two identical private copies that never worked. Each one did this:
/// <code>
/// if (Application.Current == null) {
///     try {
///         var _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
///         var uri = new Uri("pack://application:,,,/SysManager;component/App.xaml", UriKind.Absolute);
///         var dict = (ResourceDictionary)Application.LoadComponent(uri);
///         Application.Current?.Resources.MergedDictionaries.Add(dict);
///     } catch { }
/// }
/// </code>
/// Two things are wrong with the merge, and either alone is fatal:
/// <list type="number">
///   <item><see cref="Application.LoadComponent(Uri)"/> accepts only a RELATIVE URI. Handed an absolute
///   one it throws <c>ArgumentException: Cannot use absolute URI.</c> — every call, on every machine.</item>
///   <item>Even with a relative URI it would return an <see cref="Application"/>, because App.xaml's root
///   element is <c>&lt;Application x:Class="SysManager.App"&gt;</c>. The cast to
///   <see cref="ResourceDictionary"/> is an <see cref="InvalidCastException"/> waiting to happen.</item>
/// </list>
/// <para>The <c>catch { }</c> swallowed both, so the helper reliably created an Application with no styles
/// and reported success. It also sat inside the <c>Application.Current == null</c> test, so every later
/// caller skipped it entirely on the assumption that an earlier one had done the work.</para>
/// <para>Surfaced as <c>DeepCleanupViewUiTests.View_BindsToDeepCleanupViewModel</c> failing with "Cannot find
/// resource named 'Display'" — invisibly, because this project was only ever compile-checked in the pipeline,
/// never executed, until the integration job was added.</para>
/// <para>The fix loads the resources through the only thing that can: <c>App</c>'s own generated
/// <c>InitializeComponent</c>. Nothing is swallowed, and the sentinel is verified before returning, so a test
/// can never run against a styleless Application and look like it passed.</para>
/// </remarks>
public static class AppResources
{
    /// <summary>A style every view depends on, used to decide whether App.xaml is already loaded.</summary>
    private const string SentinelResource = "Display";

    /// <summary>
    /// Ensures an <see cref="Application"/> exists carrying <c>App.xaml</c>'s resources.
    /// Idempotent, and safe to call from every test and from more than one STA thread.
    /// </summary>
    public static void Ensure()
    {
        // App.xaml declares every style INLINE under <Application.Resources>, so there is no standalone
        // dictionary to merge and nothing generic to load — App's generated InitializeComponent is the
        // only loader for them. Constructing App runs no startup logic: the single-instance mutex, the DI
        // container, the tray icon and the pipe listener all live in OnStartup, which only Run() invokes,
        // and StartupUri is never acted on without it.
        if (Application.Current is null)
        {
            try
            {
                var created = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                created.InitializeComponent();
            }
            catch (Exception ex) when (ex is XamlParseException or IOException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"App.xaml could not be loaded, so no view test can resolve a style: {ex.Message}. This "
                    + "used to be swallowed by an empty catch, which left the process holding an Application "
                    + "with no resources and made every later view test fail on an unrelated-looking "
                    + "XamlParseException instead.",
                    ex);
            }
        }

        var app = Application.Current;
        if (app is null)
        {
            throw new InvalidOperationException(
                "No WPF Application exists and one could not be created. View tests construct controls that "
                + "resolve app-scope styles, so there is nothing meaningful left to assert.");
        }

        // Asked of the resource system rather than inferred from who created the Application. That inference
        // is what the old copies got wrong, and it is also the check that makes this idempotent.
        if (app.TryFindResource(SentinelResource) is null)
        {
            throw new InvalidOperationException(
                $"An Application exists but '{SentinelResource}' does not resolve, so it was not created from "
                + "App.xaml — a bare 'new Application()' somewhere else in the suite will do this, and the "
                + "state cannot be repaired because a second Application cannot be constructed. Route every "
                + $"such call through {nameof(AppResources)}.{nameof(Ensure)} instead. (If the style was "
                + $"renamed, update {nameof(SentinelResource)} here.)");
        }
    }
}
