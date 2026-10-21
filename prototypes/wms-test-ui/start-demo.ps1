# Starts the WMS API (http launch profile, SQLite demo database) in a new window, then this UI server here.
# Needs the one-time user-secrets from README.md. Stores and prints no secrets.
param(
    [string]$ApiBaseUrl = "http://127.0.0.1:5056",
    [int]$Port = 4173,
    [switch]$UiOnly
)
$ErrorActionPreference = "Stop"
$ui = $PSScriptRoot
$repo = Resolve-Path (Join-Path $ui "..\..")

if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use. Stop that process or pass -Port <other>."
}

if (-not $UiOnly) {
    $apiPort = ([Uri]$ApiBaseUrl).Port
    if (Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue) {
        Write-Host "Something already listens on port $apiPort; assuming it is the WMS API."
    } else {
        Start-Process -FilePath "dotnet" -WorkingDirectory $repo -ArgumentList @("run", "--project", "src/Wms.Api", "--launch-profile", "http")
        Write-Host "Started the WMS API in a new window ($ApiBaseUrl). The UI shows 'WMS API 不可达' until it is ready."
    }
}

$env:WMS_API_BASE_URL = $ApiBaseUrl
$env:PORT = "$Port"
Write-Host "Open http://localhost:$Port  (Ctrl+C stops the UI server)"
node (Join-Path $ui "server.mjs")
