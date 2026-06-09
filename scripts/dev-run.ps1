param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot "artifacts\publish\Nudge\Nudge.exe"
$realLocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$realAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$logPath = Join-Path $realLocalAppData "Nudge\nudge.log"

Get-Process Nudge -ErrorAction SilentlyContinue | Stop-Process -Force

if (!$NoBuild) {
    & (Join-Path $PSScriptRoot "build.ps1")
}

if (!(Test-Path -LiteralPath $exePath)) {
    throw "Published app not found at $exePath. Run scripts\build.ps1 first."
}

$env:LOCALAPPDATA = $realLocalAppData
$env:APPDATA = $realAppData
Start-Process -FilePath $exePath

Write-Host "Nudge launched from: $exePath"
Write-Host "Runtime log: $logPath"
