$ErrorActionPreference = "Stop"

$logPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "ClickyClone\clickyclone.log"
if (!(Test-Path -LiteralPath $logPath)) {
    throw "ClickyClone log was not found at $logPath"
}

$entries = Get-Content -LiteralPath $logPath -Tail 500 |
    ForEach-Object {
        if ($_ -match "PERF stage=([^ ]+) .*?ms=([0-9]+)") {
            [pscustomobject]@{
                Stage = $matches[1]
                Ms = [int]$matches[2]
                Line = $_
            }
        }
    } |
    Where-Object { $_ }

Write-Host "Log: $logPath"
Write-Host ""

if (!$entries) {
    Write-Host "No PERF lines found yet. Run a fresh interaction with the latest build."
    exit 0
}

Write-Host "Performance summary from recent log lines:"
$entries |
    Group-Object Stage |
    Sort-Object Name |
    ForEach-Object {
        $values = $_.Group.Ms | Sort-Object
        $latest = $_.Group[-1].Ms
        $average = [math]::Round(($_.Group.Ms | Measure-Object -Average).Average)
        $max = ($_.Group.Ms | Measure-Object -Maximum).Maximum
        $median = $values[[math]::Floor(($values.Count - 1) / 2)]
        "{0,-24} latest={1,6}ms median={2,6}ms avg={3,6}ms max={4,6}ms count={5}" -f $_.Name, $latest, $median, $average, $max, $_.Count
    } |
    ForEach-Object { Write-Host $_ }

Write-Host ""
Write-Host "Latest PERF lines:"
$entries | Select-Object -Last 20 | ForEach-Object { Write-Host $_.Line }
