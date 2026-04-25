// SysManager · PowerShellRunner
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using SysManager.Models;

namespace SysManager.Services;

/// <summary>
/// Runs PowerShell scripts in-process with live streaming of all output streams.
/// Uses System.Management.Automation so we don't spawn external pwsh.exe processes.
/// </summary>
public class PowerShellRunner
{
    public event Action<PowerShellLine>? LineReceived;
    public event Action<int>? ProgressChanged; // 0-100

    /// <summary>
    /// Execute a script and return the collected PSObject results.
    /// All streams are forwarded via <see cref="LineReceived"/> for live UI display.
    /// </summary>
    public async Task<Collection<PSObject>> RunAsync(
        string script,
        IDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        using var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(script);
        if (parameters != null)
        {
            foreach (var kv in parameters)
                ps.AddParameter(kv.Key, kv.Value);
        }

        // Hook all streams
        ps.Streams.Information.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<InformationRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Info, rec.MessageData?.ToString() ?? string.Empty, DateTime.Now));
        };
        ps.Streams.Warning.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<WarningRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Warning, rec.Message, DateTime.Now));
        };
        ps.Streams.Error.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<ErrorRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Error, rec.ToString(), DateTime.Now));
        };
        ps.Streams.Verbose.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<VerboseRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Verbose, rec.Message, DateTime.Now));
        };
        ps.Streams.Debug.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<DebugRecord>)s!)[e.Index];
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Debug, rec.Message, DateTime.Now));
        };
        ps.Streams.Progress.DataAdded += (s, e) =>
        {
            var rec = ((PSDataCollection<ProgressRecord>)s!)[e.Index];
            if (rec.PercentComplete >= 0) ProgressChanged?.Invoke(rec.PercentComplete);
            LineReceived?.Invoke(new PowerShellLine(OutputKind.Progress, $"{rec.Activity}: {rec.StatusDescription} ({rec.PercentComplete}%)", DateTime.Now));
        };

        using var output = new PSDataCollection<PSObject>();
        output.DataAdded += (s, e) =>
        {
            var obj = ((PSDataCollection<PSObject>)s!)[e.Index];
            if (obj?.BaseObject != null)
                LineReceived?.Invoke(new PowerShellLine(OutputKind.Output, obj.BaseObject.ToString() ?? string.Empty, DateTime.Now));
        };

        using var reg = cancellationToken.Register(() => { try { ps.Stop(); } catch (InvalidOperationException) { } });

        var task = Task.Factory.FromAsync(
            ps.BeginInvoke<PSObject, PSObject>(null, output),
            ar => ps.EndInvoke(ar));

        await task.ConfigureAwait(false);

        return new Collection<PSObject>(output.ToList());
    }

    /// <summary>
    /// Run a PowerShell script via an external powershell.exe (Windows PS 5.1).
    /// This gives full access to built-in modules (Management, Utility, PSWindowsUpdate, etc.)
    /// without bundling them with our app. All output is streamed live.
    /// Suppresses progress/CLIXML noise by default.
    /// </summary>
    public async Task<int> RunScriptViaPwshAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        // Prefix to silence progress (which gets serialized as CLIXML in stderr
        // when pwsh runs under a non-PS host) and set UTF-8 for clean text.
        var wrapped =
            "$ProgressPreference='SilentlyContinue';" +
            "$WarningPreference='Continue';" +
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
            script;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapped));
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}";
        return await RunProcessAsync("powershell.exe", args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience for running an external process (winget etc.) with live line streaming.
    /// </summary>
    public async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        System.Text.Encoding? outputEncoding = null)
    {
        // Always launch from a neutral system directory so the spawned
        // process never inherits a "locked" CWD (e.g. a user's Downloads
        // folder on another drive, which causes "Access is denied" when
        // running chkdsk.exe even under elevation).
        var workingDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(workingDir) || !System.IO.Directory.Exists(workingDir))
            workingDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Default to UTF-8 for most tools. System tools like sfc.exe, DISM.exe,
        // and chkdsk.exe write in the OEM code page — callers should pass
        // Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).
        var enc = outputEncoding ?? System.Text.Encoding.UTF8;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };

        using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !IsClixmlNoise(e.Data))
                LineReceived?.Invoke(PowerShellLine.Output(e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && !IsClixmlNoise(e.Data))
                LineReceived?.Invoke(PowerShellLine.Err(e.Data));
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var reg = cancellationToken.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return proc.ExitCode;
    }

    private static bool IsClixmlNoise(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("#< CLIXML", StringComparison.Ordinal)
            || t.StartsWith("<Objs ", StringComparison.Ordinal)
            || t.StartsWith("<Obj ", StringComparison.Ordinal)
            || t.StartsWith("</Objs>", StringComparison.Ordinal);
    }
}
