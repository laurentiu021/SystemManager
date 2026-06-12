// SysManager · AppFixture
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace SysManager.UITests;

/// <summary>
/// xUnit collection fixture that launches the SysManager WPF app once per
/// test collection, attaches UI Automation (FlaUI + UIA3), and tears it
/// down at the end.
/// </summary>
public sealed class AppFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    public Window MainWindow { get; }

    public AppFixture()
    {
        var exe = FindExecutable();
        App = Application.Launch(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = false
        });

        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(20))
            ?? throw new InvalidOperationException("Main window did not appear in time");
    }

    /// <summary>
    /// Selects a nav entry by its AutomationId (e.g. "nav-network", "nav-logs").
    /// Works with both the old ListBox layout and the new grouped tree layout.
    /// </summary>
    public void GoToTab(string navId)
    {
        // Find any descendant with the matching AutomationId and click it.
        var item = Retry.WhileNull(() =>
            MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(navId)),
            TimeSpan.FromSeconds(5)).Result
            ?? throw new InvalidOperationException($"Nav item '{navId}' not found");

        item.Click();
        Thread.Sleep(250);
    }

    /// <summary>
    /// Wait up to <paramref name="timeoutSeconds"/> for any descendant whose
    /// Name contains <paramref name="text"/> (case-insensitive).
    /// </summary>
    public AutomationElement? WaitForText(string text, int timeoutSeconds = 5)
        => Retry.WhileNull(() =>
            MainWindow.FindAllDescendants()
                .FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.Name) &&
                    e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(timeoutSeconds)).Result;

    /// <summary>Find a Button by its exact visible content text.</summary>
    public Button? FindButton(string text) =>
        MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => string.Equals(b.Name, text, StringComparison.OrdinalIgnoreCase))
            ?.AsButton();

    /// <summary>Find a control by its AutomationId.</summary>
    public AutomationElement? FindById(string automationId) =>
        MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private static string FindExecutable()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));

        // Search whichever configuration the app was actually built in. CI builds
        // Release; local dev often builds Debug — accept either rather than hardcoding
        // one (the previous Debug-only lookup made every UI test fail under a Release build).
        // Resolve the target-framework folder dynamically (e.g. net10.0-windows) so the
        // path survives .NET version bumps.
        var searched = new List<string>();
        foreach (var config in new[] { "Release", "Debug" })
        {
            var binDir = Path.Combine(repoRoot, "SysManager", "bin", config);
            searched.Add(binDir);
            if (!Directory.Exists(binDir)) continue;

            var candidate = Directory
                .EnumerateDirectories(binDir, "net*-windows")
                .Select(tfm => Path.Combine(tfm, "SysManager.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }

        throw new FileNotFoundException(
            $"Expected SysManager.exe under {string.Join(" or ", searched.Select(d => d + "\\net*-windows"))}. " +
            "Build SysManager (Debug or Release) before running UI tests.");
    }

    public void Dispose()
    {
        try
        {
            if (!App.HasExited) App.Close();
            App.Dispose();
        }
        catch { }
        try { Automation.Dispose(); } catch { }
    }
}

[CollectionDefinition("App", DisableParallelization = true)]
public class AppCollection : ICollectionFixture<AppFixture> { }
