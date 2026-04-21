# Manual UI smoke test for SysManager.
#
# Launches the published exe, clicks through every nav tab using the
# Windows UI Automation tree, and fails loudly if any tab doesn't render.
#
# Prerequisites:
#   - A published SysManager.exe (run ./publish.ps1 from the repo root).
#   - An interactive desktop session. Do NOT run this over SSH or a
#     non-interactive scheduled task; WPF apps need a real desktop.
#
# Usage (from the repo root):
#   ./docs/manual-smoke.ps1
#   ./docs/manual-smoke.ps1 -Exe ".\publish\SysManager.exe"
#   ./docs/manual-smoke.ps1 -Exe ".\publish\SysManager.exe" -DwellSeconds 2

[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\publish\SysManager.exe'),
    [int]$DwellSeconds = 1
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    throw "SysManager.exe not found at $Exe. Run ./publish.ps1 first."
}

$navIds = @(
    'nav-dashboard',
    'nav-app-updates',
    'nav-windows-update',
    'nav-system-health',
    'nav-cleanup',
    'nav-deep-cleanup',
    'nav-network',
    'nav-drivers',
    'nav-logs',
    'nav-about'
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Write-Host "Launching $Exe ..." -ForegroundColor Cyan
$proc = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds 4

    # Find the main window by process id.
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id
    )
    $window = $null
    for ($i = 0; $i -lt 20 -and -not $window; $i++) {
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if (-not $window) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $window) {
        throw "Could not locate the SysManager main window within 10 seconds."
    }
    Write-Host "Main window found." -ForegroundColor Green

    $scope = [System.Windows.Automation.TreeScope]::Descendants
    $failures = @()

    foreach ($id in $navIds) {
        $idCond = New-Object System.Windows.Automation.PropertyCondition (
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id
        )
        $item = $window.FindFirst($scope, $idCond)
        if (-not $item) {
            $failures += $id
            Write-Host "  ! $id  NOT FOUND" -ForegroundColor Red
            continue
        }

        try {
            $pattern = $item.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern
            )
            $pattern.Select()
        } catch {
            try {
                $pattern = $item.GetCurrentPattern(
                    [System.Windows.Automation.InvokePattern]::Pattern
                )
                $pattern.Invoke()
            } catch {
                $failures += $id
                Write-Host "  ! $id  not clickable ($_)" -ForegroundColor Red
                continue
            }
        }

        Write-Host "  + $id" -ForegroundColor Green
        Start-Sleep -Seconds $DwellSeconds
    }

    if ($failures.Count -gt 0) {
        throw "Smoke test failed for: $($failures -join ', ')"
    }
    Write-Host "`nAll $($navIds.Count) tabs reachable." -ForegroundColor Green
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { $proc.CloseMainWindow() | Out-Null } catch {}
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc | Stop-Process -Force }
    }
}
