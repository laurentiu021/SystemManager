# Publish a self-contained single-file SysManager.exe for Windows x64.
#
# Usage:
#   .\publish.ps1                 # default: Release, win-x64, output ./publish
#   .\publish.ps1 -Runtime win-arm64
#   .\publish.ps1 -Output dist

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Output        = 'publish',
    [string]$Version       = '',
    [switch]$NoTrim
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'SysManager\SysManager\SysManager.csproj'
if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

Write-Host "Publishing SysManager..." -ForegroundColor Cyan
Write-Host "  Config : $Configuration"
Write-Host "  Runtime: $Runtime"
Write-Host "  Output : $Output"

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=embedded',
    '-o', $Output
)

# Inject version from tag so the assembly reports the correct release number.
if ($Version -ne '') {
    Write-Host "  Version: $Version"
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:FileVersion=$Version.0"
    $publishArgs += "-p:AssemblyVersion=$Version.0"
}

# WPF doesn't support IL trimming reliably, so it's off by default.
if ($NoTrim) {
    $publishArgs += '-p:PublishTrimmed=false'
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $Output 'SysManager.exe'
if (Test-Path $exe) {
    $size = (Get-Item $exe).Length / 1MB
    Write-Host ("Done. {0} ({1:N1} MB)" -f $exe, $size) -ForegroundColor Green
} else {
    Write-Warning "Expected $exe but it was not produced."
}
