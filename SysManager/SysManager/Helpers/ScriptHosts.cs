// SysManager · ScriptHosts — the Windows programs whose command line says what really runs
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;

namespace SysManager.Helpers;

/// <summary>What a Windows helper runs, read from its command line.</summary>
/// <param name="Host">The helper's file name without its extension, as the command names it.</param>
/// <param name="Target">What it runs, as a row says it: the file it is given, or a phrase for a command.</param>
/// <param name="TargetPath">
/// The file it runs, when that is a full drive path to a file that is there; otherwise empty, and then nothing about
/// the run can be attributed to a file.
/// </param>
internal sealed record HostedRun(string Host, string Target, string TargetPath);

/// <summary>
/// The Windows programs that run what their command line names: PowerShell, Command Prompt, Windows Script Host, mshta,
/// rundll32, regsvr32 and the console host, which exist to run scripts, commands and files someone else names, and
/// Explorer, Windows Installer, the management console, Registry Editor, WSL and Windows Terminal, which open what they
/// are given.
/// </summary>
/// <remarks>
/// <para>What such a program does is what its arguments say, so a label, a publisher or a signature read off the
/// program describes the helper and not what it runs. <see cref="Hosted"/> reads what it runs. It never looks a bare or
/// relative name up in the folder SysManager runs in, never touches a network path, and never throws: a command it
/// cannot read is not a hosted run, or a run it cannot attribute to a file.</para>
/// <para>One file never vouches for a whole command: a Command Prompt line with a separator, regsvr32 given several
/// files, or PowerShell given a command rather than a file, is a run with no file to attribute.</para>
/// </remarks>
internal static class ScriptHosts
{
    /// <summary>The programs that exist to run scripts, commands and files that someone else names.</summary>
    private static readonly HashSet<string> RunOthers = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "conhost",
    };

    /// <summary>The Windows programs that open what they are given.</summary>
    private static readonly HashSet<string> OpenWhatTheyAreGiven = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "msiexec", "mmc", "regedit", "wsl", "wt", "WindowsTerminal",
    };

    /// <summary>
    /// True when <paramref name="bareName"/> names a program that exists to run what someone else names, so how busy
    /// it is says nothing about Windows itself. Not Explorer or Windows Installer, which do work of their own.
    /// </summary>
    internal static bool IsScriptHost(string bareName) => RunOthers.Contains(bareName);

    /// <summary>
    /// What the helper at the front of <paramref name="command"/> runs, or null when the program is not one of these
    /// helpers or is given nothing to run (<c>explorer.exe /n</c>, <c>cmd</c> alone).
    /// </summary>
    /// <param name="command">The command line, as a Run value or a shortcut gives it.</param>
    /// <param name="fileExists">Whether a file is there; the file system unless a test passes its own.</param>
    internal static HostedRun? Hosted(string? command, Func<string, bool>? fileExists = null) =>
        Read(command, fileExists ?? File.Exists, nested: false);

    private static HostedRun? Read(string? command, Func<string, bool> exists, bool nested)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var line = Environment.ExpandEnvironmentVariables(command).Trim();
        var program = CommandLine.Program(line, path => Path.IsPathFullyQualified(path) && exists(path));
        if (program.Length == 0) return null;
        var host = BareName(program);
        if (!RunOthers.Contains(host) && !OpenWhatTheyAreGiven.Contains(host)) return null;

        var args = ArgumentsAfter(line, program);
        var tokens = Tokens(args);
        HostedRun Run(string target, string path) => new(host, target, path);
        HostedRun? File(string target) => target.Length == 0 ? null : Run(target, Attributable(target, exists));

        switch (host.ToLowerInvariant())
        {
            case "powershell":
            case "pwsh":
                if (tokens.All(t => IsPowerShellFlag(t.Text))) return null;
                var pwsh = host.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
                return PowerShell(tokens, pwsh) is { Length: > 0 } script
                    ? Run(script, Attributable(script, exists))
                    : Run("a PowerShell command", "");
            case "cmd":
                return Cmd(args) is not { } inner ? null
                    : HasCommandSeparator(inner) ? Run("a Command Prompt command", "")
                    : (nested ? null : Read(inner, exists, nested: true))
                      ?? Inner(Run, inner, exists) ?? Run("a Command Prompt command", "");
            case "wscript":
            case "cscript":
                return File(FirstText(tokens, t => !t.StartsWith("//", StringComparison.Ordinal)));
            case "mshta":
                return tokens.Count == 0 ? null
                    : IsUrl(tokens[0].Text) ? Run("a web page", "")
                    : File(tokens[0].Text);
            case "rundll32":
                return tokens.Count == 0 ? null
                    : tokens[0].Text is var first
                      && (first.Equals("-sta", StringComparison.OrdinalIgnoreCase)
                          || first.Equals("-localserver", StringComparison.OrdinalIgnoreCase))
                        ? Run("a COM object", "")
                        : File(Rundll32Library(args));
            case "regsvr32":
                var files = tokens.Where(t => !IsSwitch(t.Text)).ToList();
                var scriptlet = tokens.Any(t =>
                    IsSwitch(t.Text) && t.Text[1..].StartsWith("i", StringComparison.OrdinalIgnoreCase));
                return scriptlet
                    ? Run("a registration command", "")
                    : files.Count switch { 0 => null, 1 => File(files[0].Text), _ => Run("several files", "") };
            case "conhost":
                return Conhost(tokens, args) is not { Length: > 0 } rest ? null
                    : (nested ? null : Read(rest, exists, nested: true))
                      ?? Inner(Run, rest, exists) ?? Run("a console command", "");
            case "msiexec":
                return Msiexec(tokens) is not { } package ? null
                    : package.StartsWith('{') ? Run("an installed program's setup", "")
                    : File(package);
            case "wsl":
                return tokens.Count == 0 ? null : Run("a command in WSL", "");
            case "wt":
            case "windowsterminal":
                return tokens.Count == 0 ? null : Run("a command in Windows Terminal", "");
            default:
                // Explorer, the management console and Registry Editor: the first argument that is not a switch.
                return File(FirstText(tokens, t => !IsSwitch(t)));
        }
    }

    /// <summary>
    /// The program an inner command line starts, attributed to it as an ordinary program, or null when none can be
    /// read. Command Prompt takes the outer quotes off a line that is quoted as a whole, so that form is read too.
    /// </summary>
    private static HostedRun? Inner(Func<string, string, HostedRun> run, string inner, Func<string, bool> exists)
    {
        bool Exists(string path) => Path.IsPathFullyQualified(path) && exists(path);
        var program = CommandLine.Program(inner, Exists);
        if (Attributable(program, exists).Length == 0 && inner.Length > 2 && inner[0] == '"'
            && inner.LastIndexOf('"') is var last and > 0)
        {
            var unquoted = CommandLine.Program(inner[1..last] + inner[(last + 1)..], Exists);
            if (Attributable(unquoted, exists).Length > 0) program = unquoted;
        }
        return program.Length == 0 ? null : run(program, Attributable(program, exists));
    }

    /// <summary>The first argument <paramref name="match"/> accepts, or empty.</summary>
    private static string FirstText(List<Token> tokens, Func<string, bool> match) =>
        tokens.Select(t => t.Text).FirstOrDefault(match) ?? "";

    /// <summary>
    /// <paramref name="path"/> when it is a full drive path to a file that is there, otherwise empty. A network or
    /// device path is never probed: a scan must not reach out to another machine.
    /// </summary>
    private static string Attributable(string path, Func<string, bool> exists)
    {
        var file = path.Trim().Trim('"').Trim();
        var drive = file.Length >= 3 && char.IsAsciiLetter(file[0]) && file[1] == ':' && file[2] == '\\';
        return drive && Path.IsPathFullyQualified(file) && exists(file) ? file : "";
    }

    private static string BareName(string program)
    {
        try { return Path.GetFileNameWithoutExtension(program.Trim('"')); }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>
    /// The text after the program in <paramref name="line"/>, which <see cref="CommandLine.Program"/> read.
    /// </summary>
    private static string ArgumentsAfter(string line, string program)
    {
        if (line.StartsWith('"'))
        {
            var closing = line.IndexOf('"', 1);
            return closing > 0 ? line[(closing + 1)..].Trim() : "";
        }
        return line.StartsWith(program, StringComparison.OrdinalIgnoreCase) ? line[program.Length..].Trim() : "";
    }

    /// <summary>One argument and where it starts in the text it was read from.</summary>
    private readonly record struct Token(string Text, int Start);

    /// <summary>
    /// The arguments, split as programs split their command line: at spaces and tabs outside quotes, a quote toggling
    /// and being dropped, and backslashes literal unless they come before a quote (2n of them and a quote are n and a
    /// toggle, 2n+1 and a quote are n and a literal quote).
    /// </summary>
    private static List<Token> Tokens(string args)
    {
        List<Token> tokens = [];
        var i = 0;
        while (i < args.Length)
        {
            while (i < args.Length && (args[i] == ' ' || args[i] == '\t')) i++;
            if (i >= args.Length) break;
            var start = i;
            var text = new System.Text.StringBuilder();
            var quoted = false;
            while (i < args.Length && (quoted || (args[i] != ' ' && args[i] != '\t')))
            {
                var slashes = 0;
                while (i < args.Length && args[i] == '\\') { slashes++; i++; }
                if (i < args.Length && args[i] == '"')
                {
                    text.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) text.Append('"');
                    else quoted = !quoted;
                    i++;
                }
                else
                {
                    text.Append('\\', slashes);
                    if (i < args.Length && (quoted || (args[i] != ' ' && args[i] != '\t'))) text.Append(args[i++]);
                }
            }
            tokens.Add(new Token(text.ToString(), start));
        }
        return tokens;
    }

    private static bool IsSwitch(string token) => token.Length > 1 && (token[0] == '/' || token[0] == '-');

    private static bool IsUrl(string token) =>
        token.Contains("://", StringComparison.Ordinal)
        || token.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    private enum PowerShellParameter { Unknown, File, Command, EncodedCommand, TakesValue, Flag }

    /// <summary>
    /// PowerShell's own parameters, by full name, with the aliases it accepts and whether each is followed by a value.
    /// A parameter may also be given by any prefix of its full name that only it begins with.
    /// </summary>
    private static readonly (string Name, string[] Aliases, PowerShellParameter Kind)[] PowerShellParameters =
    [
        ("file", ["f"], PowerShellParameter.File),
        ("command", ["c"], PowerShellParameter.Command),
        ("encodedcommand", ["ec", "e", "enc"], PowerShellParameter.EncodedCommand),
        ("executionpolicy", ["ep", "ex"], PowerShellParameter.TakesValue),
        ("windowstyle", ["w"], PowerShellParameter.TakesValue),
        ("version", ["v"], PowerShellParameter.TakesValue),
        ("inputformat", ["inp", "if"], PowerShellParameter.TakesValue),
        ("outputformat", ["o", "of"], PowerShellParameter.TakesValue),
        ("configurationname", ["config"], PowerShellParameter.TakesValue),
        ("workingdirectory", ["wd"], PowerShellParameter.TakesValue),
        ("psconsolefile", [], PowerShellParameter.TakesValue),
        ("settingsfile", [], PowerShellParameter.TakesValue),
        ("custompipename", [], PowerShellParameter.TakesValue),
        ("encodedarguments", ["ea"], PowerShellParameter.TakesValue),
        ("noprofile", ["nop"], PowerShellParameter.Flag),
        ("noprofileloadtime", [], PowerShellParameter.Flag),
        ("noninteractive", [], PowerShellParameter.Flag),
        ("nologo", [], PowerShellParameter.Flag),
        ("noexit", [], PowerShellParameter.Flag),
        ("sta", [], PowerShellParameter.Flag),
        ("mta", [], PowerShellParameter.Flag),
        ("interactive", [], PowerShellParameter.Flag),
        ("login", ["l"], PowerShellParameter.Flag),
    ];

    private static PowerShellParameter ParameterOf(string token)
    {
        var name = token.TrimStart('-', '/');
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        if (name.Length == 0) return PowerShellParameter.Unknown;
        foreach (var (full, aliases, kind) in PowerShellParameters)
        {
            if (full.Equals(name, StringComparison.OrdinalIgnoreCase)
                || aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
                return kind;
        }
        var kinds = PowerShellParameters
            .Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Kind)
            .Distinct()
            .ToList();
        // Several parameters begin with it: still a flag when every one of them is, since none takes a value.
        return kinds.Count == 1 ? kinds[0] : PowerShellParameter.Unknown;
    }

    private static bool IsPowerShellFlag(string token) =>
        IsSwitch(token) && ParameterOf(token) is PowerShellParameter.Flag;

    /// <summary>
    /// The script PowerShell is given, or null when it is given a command, an encoded command, or something this
    /// cannot read, and then nothing is attributed to a file. Windows PowerShell 5.1 reads a first argument that is
    /// not a parameter as a command, PowerShell 7 as a script.
    /// </summary>
    private static string? PowerShell(List<Token> tokens, bool isPwsh)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].Text;
            if (!IsSwitch(token) && token != "-")
                return isPwsh ? token : null;

            var inline = token.IndexOf(':') is var colon and > 0 ? token[(colon + 1)..] : null;
            switch (ParameterOf(token))
            {
                case PowerShellParameter.File:
                    return inline ?? (i + 1 < tokens.Count ? tokens[i + 1].Text : "");
                case PowerShellParameter.TakesValue:
                    if (inline is null) i++;
                    break;
                case PowerShellParameter.Flag:
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>
    /// The command Command Prompt is given after <c>/c</c> or <c>/k</c>, with a leading <c>start</c> and its title and
    /// switches taken off, or null when it is given none.
    /// </summary>
    private static string? Cmd(string args)
    {
        var i = 0;
        while (i < args.Length)
        {
            while (i < args.Length && args[i] == ' ') i++;
            if (i >= args.Length || args[i] != '/') return null;
            var end = i + 1;
            while (end < args.Length && args[end] != ' ' && args[end] != '"' && args[end] != '/') end++;
            var name = args[(i + 1)..end];
            if (name.Equals("c", StringComparison.OrdinalIgnoreCase)
                || name.Equals("k", StringComparison.OrdinalIgnoreCase))
                return Start(args[end..].Trim()) is { Length: > 0 } inner ? inner : null;
            i = end;
        }
        return null;
    }

    /// <summary><paramref name="inner"/> without a leading <c>start</c>, its quoted title and its switches.</summary>
    private static string Start(string inner)
    {
        var tokens = Tokens(inner);
        if (tokens.Count == 0 || !tokens[0].Text.Equals("start", StringComparison.OrdinalIgnoreCase)) return inner;
        var i = 1;
        if (i < tokens.Count && inner[tokens[i].Start] == '"') i++;
        while (i < tokens.Count && IsSwitch(tokens[i].Text))
        {
            if (tokens[i].Text.Equals("/d", StringComparison.OrdinalIgnoreCase)
                || tokens[i].Text.Equals("/node", StringComparison.OrdinalIgnoreCase)
                || tokens[i].Text.Equals("/affinity", StringComparison.OrdinalIgnoreCase))
                i++;
            i++;
        }
        return i < tokens.Count ? inner[tokens[i].Start..].Trim() : "";
    }

    /// <summary>
    /// True when a Command Prompt line runs more than one thing, or sends output on: an <c>&amp;</c>, <c>|</c>,
    /// <c>&lt;</c> or <c>&gt;</c> outside quotes.
    /// </summary>
    private static bool HasCommandSeparator(string inner)
    {
        var quoted = false;
        foreach (var c in inner)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '&' or '|' or '<' or '>') return true;
        }
        return false;
    }

    /// <summary>The library rundll32 is given: its first argument up to a comma outside quotes.</summary>
    private static string Rundll32Library(string args)
    {
        var quoted = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == '"') quoted = !quoted;
            else if (!quoted && args[i] == ',') return args[..i].Trim().Trim('"').Trim();
        }
        return Tokens(args) is [var first, ..] ? first.Text : "";
    }

    /// <summary>What the console host is given to run, after its own switches and their values.</summary>
    private static string? Conhost(List<Token> tokens, string args)
    {
        string[] takesValue = ["--width", "--height", "--signal", "--server", "--feature", "--vtmode"];
        var i = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i].Text;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || token.Equals("-ForceV1", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--headless", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (takesValue.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                i += 2;
                continue;
            }
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            return args[tokens[i].Start..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The package Windows Installer is given: the value of its install, repair, uninstall or advertise switch, written
    /// after it or as the next argument; or null when there is none.
    /// </summary>
    private static string? Msiexec(List<Token> tokens)
    {
        string[] withPackage = ["/i", "/package", "/x", "/uninstall", "/f", "/a", "/j", "/jm", "/ju"];
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].Text;
            foreach (var name in withPackage)
            {
                if (!token.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                var attached = token[name.Length..];
                if (attached.Length > 0 && !char.IsAsciiLetter(attached[0])) return attached.Trim('"');
                if (attached.Length == 0 && i + 1 < tokens.Count) return tokens[i + 1].Text;
            }
        }
        return null;
    }
}
