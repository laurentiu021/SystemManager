# Manual UI smoke test for SysManager.
#
# Launches the published exe, opens every tab through the Windows UI Automation tree, and fails loudly if a tab
# does not show its page header, if SysManager shows its error box, or if it exits.
#
# The tabs, and the header each one shows, are read from the UI suite's own table,
# AllTabsSmokeUiTests.AllTabs(), so the two cannot drift apart.
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
    [int]$DwellSeconds = 1,
    [int]$HeaderTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    throw "SysManager.exe not found at $Exe. Run ./publish.ps1 first."
}

# Each row of the table is one line: new object[] { "nav-...", "<a substring of the page header>" },
$tableFile = Join-Path $PSScriptRoot '..\SysManager\SysManager.UITests\AllTabsSmokeUiTests.cs'
$tabs = @(Select-String -Path $tableFile -Pattern 'new object\[\] \{ "(nav-[\w-]+)", "([^"]+)" \}' | ForEach-Object {
    [pscustomobject]@{ Id = $_.Matches[0].Groups[1].Value; Header = $_.Matches[0].Groups[2].Value }
})
if ($tabs.Count -lt 50) {
    throw "Read only $($tabs.Count) tabs from $tableFile. Its table changed shape, so this would check almost nothing."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants

function New-IdCondition([string]$automationId) {
    New-Object System.Windows.Automation.PropertyCondition ($auto::AutomationIdProperty, $automationId)
}

Write-Host "Launching $Exe ..." -ForegroundColor Cyan
$proc = Start-Process $Exe -PassThru
try {
    Start-Sleep -Seconds 4

    # Find the main window by process id.
    $root = $auto::RootElement
    $ownWindows = New-Object System.Windows.Automation.PropertyCondition ($auto::ProcessIdProperty, $proc.Id)
    $window = $null
    for ($i = 0; $i -lt 20 -and -not $window; $i++) {
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $ownWindows)
        if (-not $window) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $window) {
        throw "Could not locate the SysManager main window within 10 seconds."
    }
    Write-Host "Main window found." -ForegroundColor Green

    # An exception in a view shows this box and is marked handled, so the window stays up and only the box says so.
    $errorBox = New-Object System.Windows.Automation.AndCondition (
        $ownWindows,
        (New-Object System.Windows.Automation.PropertyCondition ($auto::NameProperty, 'SysManager error'))
    )

    # The first launch on a PC opens with the Welcome screen over the window, and the tabs under it are
    # disabled until it closes. Close it the way a user would, with its "Just look around" button.
    $lookAround = $window.FindFirst($scope, (New-IdCondition 'WelcomeLookAround'))
    if ($lookAround -and -not $lookAround.Current.IsOffscreen) {
        $lookAround.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Host "Welcome screen closed." -ForegroundColor Green
        Start-Sleep -Milliseconds 500
    }

    # Every sidebar group but Cleanup starts collapsed, and a collapsed group's tabs are not in the automation tree,
    # so open them all first, as the UI suite does.
    $groups = New-Object System.Windows.Automation.PropertyCondition (
        $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Group
    )
    foreach ($group in $window.FindAll($scope, $groups)) {
        $expander = $null
        if ($group.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expander) -and
            $expander.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $expander.Expand()
        }
    }
    Start-Sleep -Milliseconds 400

    $pageHost = New-IdCondition 'CurrentViewHost'
    $failures = @()

    foreach ($tab in $tabs) {
        $item = $window.FindFirst($scope, (New-IdCondition $tab.Id))
        if (-not $item) {
            $failures += $tab.Id
            Write-Host "  ! $($tab.Id)  NOT FOUND" -ForegroundColor Red
            continue
        }

        $invoke = $null
        $select = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
            $invoke.Invoke()
        } elseif ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) {
            $select.Select()
        } else {
            $failures += $tab.Id
            Write-Host "  ! $($tab.Id)  not clickable" -ForegroundColor Red
            continue
        }

        # The page has to show its header, within a bound, as AllTabsSmokeUiTests checks.
        $deadline = (Get-Date).AddSeconds($HeaderTimeoutSeconds)
        $shown = $false
        while (-not $shown -and (Get-Date) -lt $deadline) {
            $page = $window.FindFirst($scope, $pageHost)
            if ($page) {
                foreach ($element in $page.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)) {
                    $name = $element.Current.Name
                    if ($name -and $name.IndexOf($tab.Header, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        $shown = $true
                        break
                    }
                }
            }
            if (-not $shown) { Start-Sleep -Milliseconds 250 }
        }

        if ($root.FindFirst([System.Windows.Automation.TreeScope]::Children, $errorBox)) {
            $failures += $tab.Id
            throw "SysManager showed its error box on $($tab.Id). Failed so far: $($failures -join ', ')"
        }
        if ($proc.HasExited) {
            $failures += $tab.Id
            throw "SysManager exited with code $($proc.ExitCode) on $($tab.Id). Failed so far: $($failures -join ', ')"
        }
        if (-not $shown) {
            $failures += $tab.Id
            Write-Host "  ! $($tab.Id)  did not show '$($tab.Header)' within $HeaderTimeoutSeconds s" -ForegroundColor Red
            continue
        }

        Write-Host "  + $($tab.Id)" -ForegroundColor Green
        Start-Sleep -Seconds $DwellSeconds
    }

    if ($failures.Count -gt 0) {
        throw "Smoke test failed for: $($failures -join ', ')"
    }
    Write-Host "`nAll $($tabs.Count) tabs showed their page header." -ForegroundColor Green
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try {
            $proc.CloseMainWindow() | Out-Null
        } catch [System.InvalidOperationException] {
            Write-Verbose "SysManager exited before its window could be closed."
        }
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc | Stop-Process -Force }
    }
}
