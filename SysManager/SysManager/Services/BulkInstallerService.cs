// SysManager · BulkInstallerService — install apps via winget
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Linq;
using SysManager.Helpers;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Wraps winget.exe to install packages by their winget ID.
/// </summary>
public sealed class BulkInstallerService
{
    private readonly IPowerShellRunner _runner;

    public BulkInstallerService(IPowerShellRunner runner) => _runner = runner;

    public event Action<PowerShellLine>? LineReceived
    {
        add => _runner.LineReceived += value;
        remove => _runner.LineReceived -= value;
    }

    /// <summary>
    /// Installs a package by its winget ID. Returns the process exit code.
    /// </summary>
    public async Task<int> InstallAsync(string wingetId, CancellationToken ct = default)
    {
        // Validate wingetId before interpolating it into the winget command line.
        if (!WingetId.IsValid(wingetId))
            throw new ArgumentException("Invalid package ID.", nameof(wingetId));

        var args = $"install --id \"{wingetId}\" -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity";
        return await _runner.RunProcessAsync("winget", args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>winget list</c> and returns its raw stdout lines. Routed through the
    /// <see cref="IPowerShellRunner"/> seam (not a hand-built ProcessStartInfo) so the launch
    /// inherits the runner's winget hardening — <see cref="SystemPaths.ResolveWinget"/> pins the
    /// FileName to the trusted, admin-only-writable WindowsApps install path, closing the
    /// binary-planting LPE vector a bare <c>FileName="winget"</c> would open. Mirrors
    /// <see cref="UninstallerService.ListInstalledAsync"/>'s thread-safe capture pattern.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListInstalledAsync(CancellationToken ct = default)
    {
        var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Enqueue(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            await _runner.RunProcessAsync("winget", "list --disable-interactivity", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        return captured.ToList();
    }

    /// <summary>
    /// Runs <c>winget search &lt;query&gt;</c> and returns its raw stdout lines. The query is
    /// validated (rejecting quotes and control characters) before interpolation to prevent
    /// argument injection, and the launch is routed through the runner seam for the same
    /// WorkingDirectory pinning as <see cref="ListInstalledAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken ct = default)
    {
        var safe = SanitizeQuery(query);
        if (string.IsNullOrEmpty(safe)) return [];

        var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(PowerShellLine l)
        {
            if (l.Kind == OutputKind.Output) captured.Enqueue(l.Text);
        }

        _runner.LineReceived += Collect;
        try
        {
            await _runner.RunProcessAsync("winget",
                $"search \"{safe}\" --accept-source-agreements --disable-interactivity", ct).ConfigureAwait(false);
        }
        finally { _runner.LineReceived -= Collect; }

        return captured.ToList();
    }

    /// <summary>
    /// Strips characters that could break out of the quoted winget argument (double quotes,
    /// backslashes, control chars) so a search box entry like <c>foo" &amp; calc "</c> cannot
    /// inject extra arguments. The backslash is stripped because a trailing one turns the closing
    /// quote into an escaped quote (<c>"foo\"</c>), collapsing the argument boundary — and a winget
    /// search term has no legitimate use for it. Returns the trimmed, sanitized query (may be empty
    /// if nothing usable remains).
    /// </summary>
    internal static string SanitizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var cleaned = new string(query.Where(c => c != '"' && c != '\\' && !char.IsControl(c)).ToArray());
        return cleaned.Trim();
    }
}
