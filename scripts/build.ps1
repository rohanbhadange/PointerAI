param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\Nudge.App\Nudge.App.csproj"
$diagnosticsProjectPath = Join-Path $repoRoot "src\Nudge.Diagnostics\Nudge.Diagnostics.csproj"
$testsProjectPath = Join-Path $repoRoot "src\Nudge.Tests\Nudge.Tests.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\Nudge"
$localDotnet = Join-Path $repoRoot ".dotnet\dotnet.exe"
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$command) {
        throw "dotnet SDK was not found. Install .NET 8 SDK or place it at $localDotnet."
    }

    $command.Source
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home"
$envNamesToRestore = @("DOTNET_CLI_HOME", "NUGET_PACKAGES", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_CLI_TELEMETRY_OPTOUT", "TEMP", "TMP", "APPDATA", "LOCALAPPDATA")
$oldEnv = @{}
foreach ($name in $envNamesToRestore) {
    $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home"
    $env:NUGET_PACKAGES = Join-Path $repoRoot ".nuget\packages"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:TEMP = Join-Path $repoRoot ".tmp"
    $env:TMP = Join-Path $repoRoot ".tmp"
    $env:APPDATA = Join-Path $repoRoot ".appdata"
    $env:LOCALAPPDATA = Join-Path $repoRoot ".localappdata"

    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TEMP, $env:APPDATA, $env:LOCALAPPDATA | Out-Null

    node --check (Join-Path $repoRoot "worker\nudge-worker.js")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    node (Join-Path $repoRoot "scripts\test-worker-pointing.mjs")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnetCommand restore $projectPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnetCommand restore $diagnosticsProjectPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnetCommand restore $testsProjectPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnetCommand run --project $testsProjectPath -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnetCommand publish $projectPath -c $Configuration --self-contained false -o $publishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Published Nudge to $publishDir"
} finally {
    foreach ($name in $envNamesToRestore) {
        [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], "Process")
    }
}
