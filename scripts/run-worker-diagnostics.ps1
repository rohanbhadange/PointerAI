$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    node .\scripts\run-worker-diagnostics.mjs
} finally {
    Pop-Location
}
