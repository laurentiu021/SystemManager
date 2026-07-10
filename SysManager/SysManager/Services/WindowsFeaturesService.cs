// SysManager · WindowsFeaturesService — list and toggle Windows optional features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps PowerShell commands to list, enable, and disable Windows optional features.
/// Requires administrator privileges for enable/disable operations.
/// </summary>
public sealed partial class WindowsFeaturesService
{
    private readonly IPowerShellRunner _runner;

    public WindowsFeaturesService(IPowerShellRunner runner) => _runner = runner;

    /// <summary>
    /// Lists all Windows optional features with their current state.
    /// Uses Get-WindowsOptionalFeature -Online.
    /// </summary>
    public async Task<List<WindowsFeature>> ListFeaturesAsync(CancellationToken ct = default)
    {
        List<string> captured = [];
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            await _runner.RunProcessAsync("powershell.exe",
                "-NoProfile -Command \"Get-WindowsOptionalFeature -Online | " +
                "Select-Object FeatureName, State | " +
                "ForEach-Object { $_.FeatureName + '|' + $_.State }\"", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        return ParseFeatureList(captured);
    }

    /// <summary>
    /// Enables a Windows optional feature. Requires admin.
    /// Returns true if successful, false otherwise.
    /// </summary>
    public async Task<(bool Success, bool RebootRequired)> EnableFeatureAsync(
        string featureName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(featureName) || !FeatureNamePattern().IsMatch(featureName))
            throw new ArgumentException("Invalid feature name.", nameof(featureName));

        Log.Information("Enabling Windows feature: {Feature}", featureName);

        List<string> captured = [];
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            // Wrap in try/catch + `exit 1` so a DISM cmdlet failure propagates to the
            // process exit code. Without this the cmdlet's non-terminating error is
            // ignored and powershell.exe still exits 0, reporting a false success.
            var code = await _runner.RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"try {{ Enable-WindowsOptionalFeature -Online " +
                $"-FeatureName '{featureName}' -NoRestart -All -ErrorAction Stop | " +
                $"Select-Object RestartNeeded | ForEach-Object {{ $_.RestartNeeded }} }} catch {{ exit 1 }}\"", ct).ConfigureAwait(false);

            var reboot = captured.Any(l =>
                l.Contains("True", StringComparison.OrdinalIgnoreCase));

            return (code == 0, reboot);
        }
        finally { _runner.LineReceived -= Collect; }
    }

    /// <summary>
    /// Disables a Windows optional feature. Requires admin.
    /// Returns true if successful, false otherwise.
    /// </summary>
    public async Task<(bool Success, bool RebootRequired)> DisableFeatureAsync(
        string featureName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(featureName) || !FeatureNamePattern().IsMatch(featureName))
            throw new ArgumentException("Invalid feature name.", nameof(featureName));

        Log.Information("Disabling Windows feature: {Feature}", featureName);

        List<string> captured = [];
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            // Wrap in try/catch + `exit 1` so a DISM cmdlet failure propagates to the
            // process exit code. Without this the cmdlet's non-terminating error is
            // ignored and powershell.exe still exits 0, reporting a false success.
            var code = await _runner.RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"try {{ Disable-WindowsOptionalFeature -Online " +
                $"-FeatureName '{featureName}' -NoRestart -ErrorAction Stop | " +
                $"Select-Object RestartNeeded | ForEach-Object {{ $_.RestartNeeded }} }} catch {{ exit 1 }}\"", ct).ConfigureAwait(false);

            var reboot = captured.Any(l =>
                l.Contains("True", StringComparison.OrdinalIgnoreCase));

            return (code == 0, reboot);
        }
        finally { _runner.LineReceived -= Collect; }
    }

    /// <summary>Parses the pipe-delimited feature list output.</summary>
    internal static List<WindowsFeature> ParseFeatureList(List<string> lines)
    {
        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(line =>
            {
                var parts = line.Split('|', 2);
                if (parts.Length < 2) return null;

                var name = parts[0].Trim();
                var state = parts[1].Trim();

                if (string.IsNullOrWhiteSpace(name)) return null;

                var isEnabled = state.Equals("Enabled", StringComparison.OrdinalIgnoreCase);

                var (safety, safetyDesc) = SafetyDatabase.GetFeatureSafety(name);

                return new WindowsFeature
                {
                    Name = name,
                    DisplayName = HumanizeName(name),
                    IsEnabled = isEnabled,
                    Status = isEnabled ? "Enabled" : "Disabled",
                    Category = WindowsFeature.CategorizeFeature(name),
                    SafetyLevel = safety,
                    SafetyDescription = safetyDesc,
                };
            })
            .Where(f => f is not null)
            .OrderBy(f => f!.Category)
            .ThenBy(f => f!.DisplayName)
            .ToList()!;
    }

    /// <summary>
    /// Converts PascalCase feature names to human-readable form.
    /// e.g. "Microsoft-Hyper-V-All" → "Microsoft Hyper-V All"
    /// </summary>
    internal static string HumanizeName(string featureName)
    {
        return featureName.Replace('-', ' ').Replace('_', ' ');
    }

    /// <summary>
    /// Validates feature names: alphanumeric, hyphens, underscores, dots. Max 128 chars.
    /// SECURITY-CRITICAL: This regex is the sole defense against PowerShell injection
    /// in Enable/DisableFeatureAsync where featureName is interpolated into a command.
    /// Do NOT relax this pattern without reviewing injection implications.
    /// </summary>
    // \A…\z (absolute anchors): ^…$ would accept a trailing newline in the feature
    // name before it is embedded into the PowerShell command.
    [GeneratedRegex(@"\A[\w.\-]{1,128}\z")]
    private static partial Regex FeatureNamePattern();
}
