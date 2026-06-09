param(
    [switch]$DryRun,
    [string]$DeployRoot
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Require-Command {
    param([string]$Name)
    if ($env:OS -eq "Windows_NT") {
        $nodeShim = Join-Path $env:ProgramFiles "nodejs\$Name"
        if ($Name -eq "node") {
            $nodeShim += ".exe"
        }
        else {
            $nodeShim += ".cmd"
        }

        if (Test-Path $nodeShim) {
            return $nodeShim
        }
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "$Name was not found. Install Node.js from https://nodejs.org, then run this script again."
    }

    return $command.Source
}

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerSource = Join-Path $repoRoot "worker\nudge-worker.js"
if ([string]::IsNullOrWhiteSpace($DeployRoot)) {
    $DeployRoot = Join-Path $env:LOCALAPPDATA "Nudge\worker-deploy"
}

Write-Step "Checking tools"
$node = Require-Command "node"
$npm = Require-Command "npm"
$npx = Require-Command "npx"
Write-Host "Node: $node"
Write-Host "npm:  $npm"
Write-Host "npx:  $npx"

Write-Step "Preparing Worker deploy folder"
if (-not (Test-Path $workerSource)) {
    throw "Worker source file was not found at $workerSource."
}

New-Item -ItemType Directory -Path $DeployRoot -Force | Out-Null
Copy-Item -LiteralPath $workerSource -Destination (Join-Path $DeployRoot "nudge-worker.js") -Force

$workerName = "nudge-" + ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$wranglerConfig = @"
{
  "name": "$workerName",
  "main": "nudge-worker.js",
  "compatibility_date": "2025-01-01"
}
"@
Set-Content -LiteralPath (Join-Path $DeployRoot "wrangler.jsonc") -Value $wranglerConfig -Encoding UTF8

Write-Host "Deploy folder: $DeployRoot"
Write-Host "Worker name:   $workerName"

if ($DryRun) {
    Write-Step "Checking npx"
    Invoke-Checked -FilePath $npx -Arguments @("--version") -WorkingDirectory $DeployRoot

    Write-Step "Dry run complete"
    Write-Host "Generated files:"
    Get-ChildItem -LiteralPath $DeployRoot | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
    Write-Host ""
    Write-Host "No Worker was deployed because -DryRun was used."
    Write-Host "Wrangler download/deploy is intentionally skipped in dry run."
    exit 0
}

Write-Step "Checking Wrangler"
Invoke-Checked -FilePath $npx -Arguments @("--yes", "wrangler@latest", "--version") -WorkingDirectory $DeployRoot

Write-Step "Deploying Worker"
Write-Host "Wrangler may open a browser or ask you to sign in to Cloudflare."
Write-Host "If deploy fails, use the Wrangler error shown here to finish setup in Cloudflare."
Invoke-Checked -FilePath $npx -Arguments @("--yes", "wrangler@latest", "deploy") -WorkingDirectory $DeployRoot

Write-Step "Next steps"
Write-Host "1. Copy the workers.dev URL printed by Wrangler above."
Write-Host "2. Open the Worker in the Cloudflare dashboard."
Write-Host "3. Add these Worker secrets:"
Write-Host "   OPENAI_API_KEY"
Write-Host "   ASSEMBLYAI_API_KEY"
Write-Host "   ELEVENLABS_API_KEY"
Write-Host "   ELEVENLABS_VOICE_ID"
Write-Host "4. Launch Nudge, choose 'Use a Cloudflare Worker URL', and paste the Worker URL."
