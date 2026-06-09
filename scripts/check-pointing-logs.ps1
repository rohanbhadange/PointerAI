$ErrorActionPreference = "Stop"

$logPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Nudge\nudge.log"
if (!(Test-Path -LiteralPath $logPath)) {
    throw "Nudge log was not found at $logPath"
}

$lines = Get-Content -LiteralPath $logPath -Tail 250
$captures = $lines | Where-Object { $_ -match "Screen capture elements\." }
$mappedPoints = $lines | Where-Object { $_ -match "Point mapped\." }
$refinements = $lines | Where-Object { $_ -match "UI Automation point refined\.|UI Automation point refinement found no high-confidence candidate\.|UI Automation point refinement timed out\." }
$selfTests = $lines | Where-Object { $_ -match "Worker exact pointing support" -or $_ -match "Worker /pointing-self-test response" }
$catalogDiagnostics = $lines | Where-Object { $_ -match "Element catalog diagnostic query\.|Element catalog sample\.|Element catalog transcript matches\." }

Write-Host "Log: $logPath"
Write-Host ""
Write-Host "Recent capture element counts:"
if ($captures) {
    $captures | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "No screen capture element lines found."
}

Write-Host ""
Write-Host "Worker exact-pointing startup check:"
if ($selfTests) {
    $selfTests | Select-Object -Last 6 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "No Worker pointing self-test lines found."
}

Write-Host ""
Write-Host "Recent mapped points:"
if ($mappedPoints) {
    $mappedPoints | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "No mapped point lines found."
}

Write-Host ""
Write-Host "Recent element catalog diagnostics:"
if ($catalogDiagnostics) {
    $catalogDiagnostics | Select-Object -Last 18 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "No element catalog diagnostic lines found."
}

Write-Host ""
Write-Host "Recent fuzzy refinement activity:"
if ($refinements) {
    $refinements | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
} else {
    Write-Host "No fuzzy refinement lines found."
}

Write-Host ""
if ($mappedPoints -match "PointSource=element") {
    Write-Host "Exact element path observed: yes"
} else {
    Write-Host "Exact element path observed: no"
}

if ($mappedPoints -match "PointSource=box") {
    Write-Host "Stable box path observed: yes"
} else {
    Write-Host "Stable box path observed: no"
}
