// SysManager · WindowsFeaturesService — list and toggle Windows optional features
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps PowerShell commands to list, enable, and disable Windows optional features.
/// All three need administrator privileges: <c>Get-WindowsOptionalFeature -Online</c> refuses an
/// unelevated caller just as the enable and disable commands do.
/// </summary>
public sealed partial class WindowsFeaturesService
{
    private readonly IPowerShellRunner _runner;

    public WindowsFeaturesService(IPowerShellRunner runner) => _runner = runner;

    /// <summary>
    /// What a change script prints in its catch before it exits 1: this marker, DISM's error code in hex and the first
    /// line of DISM's message, split by '|'.
    /// </summary>
    /// <remarks>
    /// The catch printed nothing, so DISM's reason reached neither the tab nor the log, and the tab said "Check
    /// permissions" for every failure (#2834). The cmdlets throw a <c>COMException</c> whose <c>HResult</c> is DISM's
    /// code: <c>0x800F080C</c> for a name Windows does not know, <c>0x800702E4</c> for a caller that is not elevated.
    /// </remarks>
    internal const string FailureMarker = "SYSMANAGER-FEATURE-ERROR";

    /// <summary>
    /// Lists all Windows optional features with their current state.
    /// Uses Get-WindowsOptionalFeature -Online.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The query failed — unelevated, it always does. Its exit code used to be discarded, so a refused query
    /// came back as an empty list and the tab reported "Found 0 features" (#2451).
    /// </exception>
    public Task<List<WindowsFeature>> ListFeaturesAsync(CancellationToken ct = default) => QueryAsync(null, ct);

    /// <summary>
    /// Reads one feature's state the way the list reads every feature's, or null when Windows does not know the name.
    /// </summary>
    /// <remarks>
    /// The tab reads a feature back after changing it rather than writing what it asked for into the row (#2834). One
    /// feature reads in about a second; the whole list took 54 seconds where it was measured.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The query failed.</exception>
    public async Task<WindowsFeature?> ReadFeatureAsync(string featureName, CancellationToken ct = default)
    {
        ThrowIfInvalid(featureName);
        var read = await QueryAsync(featureName, ct).ConfigureAwait(false);
        return read.Find(f => f.Name.Equals(featureName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enables a Windows optional feature. Requires admin.
    /// </summary>
    public Task<FeatureChangeResult> EnableFeatureAsync(string featureName, CancellationToken ct = default)
        => ChangeAsync(enable: true, featureName, ct);

    /// <summary>
    /// Disables a Windows optional feature. Requires admin.
    /// </summary>
    public Task<FeatureChangeResult> DisableFeatureAsync(string featureName, CancellationToken ct = default)
        => ChangeAsync(enable: false, featureName, ct);

    private async Task<FeatureChangeResult> ChangeAsync(bool enable, string featureName, CancellationToken ct)
    {
        ThrowIfInvalid(featureName);

        if (enable) Log.Information("Enabling Windows feature: {Feature}", featureName);
        else Log.Information("Disabling Windows feature: {Feature}", featureName);

        List<string> captured = [];
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            var code = await _runner.RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"{ChangeScript(enable, featureName)}\"", ct,
                PowerShellRunner.OemEncoding).ConfigureAwait(false);

            var result = ParseChange(code, captured);
            if (!result.Success)
            {
                Log.Warning("Windows feature {Feature} could not be {Action}: {Code} {Message}", featureName,
                    enable ? "enabled" : "disabled",
                    result.ErrorCode is { } hr ? $"0x{hr:X8}" : $"exit code {code}", result.ErrorMessage);
            }
            return result;
        }
        finally { _runner.LineReceived -= Collect; }
    }

    /// <summary>
    /// The script that turns a feature on or off, as powershell.exe runs it. Enabling passes <c>-All</c>, which turns
    /// on the parent features the named one needs.
    /// </summary>
    /// <remarks>
    /// Wrapped in try/catch with <c>exit 1</c> so a DISM cmdlet failure propagates to the process exit code. Without it
    /// the cmdlet's non-terminating error is ignored and powershell.exe still exits 0, reporting a false success. The
    /// catch prints <see cref="FailureMarker"/>, the code and DISM's reason before it exits.
    /// </remarks>
    internal static string ChangeScript(bool enable, string featureName) =>
        $"try {{ {(enable ? "Enable" : "Disable")}-WindowsOptionalFeature -Online -FeatureName '{featureName}' " +
        $"-NoRestart{(enable ? " -All" : "")} -ErrorAction Stop | Select-Object RestartNeeded | " +
        "ForEach-Object { $_.RestartNeeded } } " +
        $"catch {{ '{FailureMarker}|' + ('{{0:X8}}' -f $_.Exception.HResult) + '|' + " +
        "($_.Exception.Message.Trim() -split '\\r?\\n')[0]; exit 1 }";

    /// <summary>
    /// Reads a change script's output: whether Windows asked for a restart, and DISM's error when the change failed.
    /// </summary>
    internal static FeatureChangeResult ParseChange(int exitCode, IReadOnlyList<string> lines)
    {
        if (exitCode == 0)
        {
            var reboot = lines.Any(l => l.Trim().Equals("True", StringComparison.OrdinalIgnoreCase));
            return new FeatureChangeResult(true, reboot);
        }

        foreach (var line in lines)
        {
            var parts = line.Trim().Split('|', 3);
            if (parts.Length == 3 && parts[0] == FailureMarker
                && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                return new FeatureChangeResult(false, false, unchecked((int)code), parts[2].Trim());
        }
        return new FeatureChangeResult(false, false);
    }

    /// <summary>The query the list and the read-back share: every feature, or the one named.</summary>
    internal static string QueryScript(string? featureName) =>
        "Get-WindowsOptionalFeature -Online" + (featureName is null ? "" : $" -FeatureName '{featureName}'") + " | " +
        "Select-Object FeatureName, State | " +
        "ForEach-Object { $_.FeatureName + '|' + $_.State }";

    private async Task<List<WindowsFeature>> QueryAsync(string? featureName, CancellationToken ct)
    {
        List<string> captured = [];
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Add(l.Text);
        }

        int exitCode;
        _runner.LineReceived += Collect;
        try
        {
            exitCode = await _runner.RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"{QueryScript(featureName)}\"", ct, PowerShellRunner.OemEncoding)
                .ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Windows could not list the optional features (exit code {exitCode}).");
        return ParseFeatureList(captured);
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

                var (isEnabled, pending) = ReadState(state);

                var (safety, safetyDesc) = SafetyDatabase.GetFeatureSafety(name);

                return new WindowsFeature
                {
                    Name = name,
                    DisplayName = HumanizeName(name),
                    IsEnabled = isEnabled,
                    RequiresReboot = pending,
                    Status = pending ? "Reboot required" : isEnabled ? "Enabled" : "Disabled",
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

    /// <summary>Whether a state Windows reports is on, and whether it waits for a restart to get there.</summary>
    /// <remarks>
    /// A change that waits for a restart reads <c>EnablePending</c> or <c>DisablePending</c> until then. Only
    /// <c>Enabled</c> counted as on, so a feature just turned on read as off the next time it was read (#2834).
    /// </remarks>
    internal static (bool IsEnabled, bool Pending) ReadState(string state)
    {
        if (state.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return (true, false);
        if (state.Equals("EnablePending", StringComparison.OrdinalIgnoreCase)) return (true, true);
        if (state.Equals("DisablePending", StringComparison.OrdinalIgnoreCase)) return (false, true);
        return (false, false);
    }

    /// <summary>
    /// Converts PascalCase feature names to human-readable form.
    /// e.g. "Microsoft-Hyper-V-All" → "Microsoft Hyper-V All"
    /// </summary>
    internal static string HumanizeName(string featureName)
    {
        return featureName.Replace('-', ' ').Replace('_', ' ');
    }

    private static void ThrowIfInvalid(string featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName) || !FeatureNamePattern().IsMatch(featureName))
            throw new ArgumentException("Invalid feature name.", nameof(featureName));
    }

    /// <summary>
    /// Validates feature names: alphanumeric, hyphens, underscores, dots. Max 128 chars.
    /// SECURITY-CRITICAL: This regex is the sole defense against PowerShell injection
    /// in Enable/Disable/ReadFeatureAsync where featureName is interpolated into a command.
    /// Do NOT relax this pattern without reviewing injection implications.
    /// </summary>
    // \A…\z (absolute anchors): ^…$ would accept a trailing newline in the feature
    // name before it is embedded into the PowerShell command.
    [GeneratedRegex(@"\A[\w.\-]{1,128}\z")]
    private static partial Regex FeatureNamePattern();
}
