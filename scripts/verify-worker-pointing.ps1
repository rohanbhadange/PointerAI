param(
    [string]$WorkerUrl = "https://clickyclone.rohanbhadange18.workers.dev"
)

$ErrorActionPreference = "Stop"

$endpoint = $WorkerUrl.TrimEnd("/") + "/pointing-self-test"
Write-Host "Checking Worker exact pointing support: $endpoint"

$response = Invoke-RestMethod -Method Get -Uri $endpoint
$source = $response.point.source
if ($response.ok -ne $true -or $source -ne "element") {
    $json = $response | ConvertTo-Json -Depth 8
    throw "Worker exact pointing self-test failed. Response: $json"
}

Write-Host "Worker exact pointing support: ok"
Write-Host "Resolved point: source=$source x=$($response.point.x) y=$($response.point.y) screen=$($response.point.screenNumber) label=$($response.point.label)"
