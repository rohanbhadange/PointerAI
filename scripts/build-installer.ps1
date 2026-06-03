$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $repoRoot "installer\ClickyClone.iss"
$publishExe = Join-Path $repoRoot "artifacts\publish\ClickyClone\ClickyClone.exe"

if (!(Test-Path $publishExe)) {
    throw "Published app not found at $publishExe. Run .\scripts\build.ps1 first."
}

$isccCommand = Get-Command iscc.exe -ErrorAction SilentlyContinue
$candidatePaths = @(@(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$repoRoot\tools\Inno Setup 6\ISCC.exe",
    "$repoRoot\tools\innosetup\ISCC.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

if ($isccCommand) {
    & $isccCommand.Source $installerScript
} elseif ($candidatePaths.Count -gt 0) {
    $isccPath = [string]$candidatePaths[0]
    & $isccPath $installerScript
} else {
    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6, then rerun this script."
}

Write-Host "Installer written to artifacts\installer"
