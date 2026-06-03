$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$diagnosticsProjectPath = Join-Path $repoRoot "src\ClickyClone.Diagnostics\ClickyClone.Diagnostics.csproj"

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $repoRoot ".nuget\packages"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:TEMP = Join-Path $repoRoot ".tmp"
$env:TMP = Join-Path $repoRoot ".tmp"
$env:APPDATA = Join-Path $repoRoot ".appdata"
$env:LOCALAPPDATA = Join-Path $repoRoot ".localappdata"

New-Item -ItemType Directory -Force -Path $env:TEMP, $env:APPDATA, $env:LOCALAPPDATA | Out-Null

& (Join-Path $repoRoot ".dotnet\dotnet.exe") run --project $diagnosticsProjectPath -c Release
