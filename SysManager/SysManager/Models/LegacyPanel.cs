// SysManager · LegacyPanel
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Models;

/// <summary>
/// A classic Windows applet that the Legacy Panels launcher can open. Pure launch
/// metadata — <see cref="FileName"/> + <see cref="Arguments"/> are passed verbatim to
/// <c>Process.Start</c> (UseShellExecute). The catalog is hard-coded, so no user input
/// ever reaches the process launcher.
/// </summary>
public sealed record LegacyPanel(
    string Name,
    string Description,
    string Glyph,
    string FileName,
    string Arguments);
