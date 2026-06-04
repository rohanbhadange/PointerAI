$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $repoRoot "artifacts\release"
$installerPath = Join-Path $repoRoot "artifacts\installer\ClickyCloneSetup.exe"

& (Join-Path $repoRoot "scripts\build.ps1")
& (Join-Path $repoRoot "scripts\build-installer.ps1")

if (!(Test-Path -LiteralPath $installerPath)) {
    throw "Installer was not found at $installerPath."
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Copy-Item -LiteralPath $installerPath -Destination (Join-Path $releaseDir "ClickyCloneSetup.exe") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot ".env.example") -Destination (Join-Path $releaseDir ".env.example") -Force

Write-Host "Release artifacts written to $releaseDir"
