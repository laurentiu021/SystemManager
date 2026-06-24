// SysManager · LegacyPanelsViewModel — one-click launcher for classic Windows applets
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using CommunityToolkit.Mvvm.Input;
using SysManager.Models;
using SysManager.Services;

namespace SysManager.ViewModels;

/// <summary>
/// ViewModel for the Legacy Panels tab. Exposes the fixed catalog of classic Windows
/// applets and a command to open one. Pure launchers — no system modification, no admin.
/// </summary>
public sealed partial class LegacyPanelsViewModel : ViewModelBase
{
    private readonly LegacyPanelService _service;

    public IReadOnlyList<LegacyPanel> Panels => LegacyPanelService.Panels;

    public LegacyPanelsViewModel(LegacyPanelService service)
    {
        _service = service;
        StatusMessage = "Click any panel to open the classic Windows applet.";
    }

    [RelayCommand]
    private void Open(LegacyPanel? panel)
    {
        if (panel is null) return;
        StatusMessage = _service.Launch(panel)
            ? $"Opened {panel.Name}."
            : $"Couldn't open {panel.Name} — it may not be available on this edition of Windows.";
    }
}
