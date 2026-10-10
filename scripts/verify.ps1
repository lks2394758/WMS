#Requires -Version 7
# Runs the pull-request checks from guide §3.3 / §12.1 and stops at the first failure.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$steps = @(
    @('restore', 'Wms.sln'),
    @('format', 'whitespace', 'Wms.sln', '--verify-no-changes'),
    @('build', 'Wms.sln', '--no-restore'),
    @('test', 'Wms.sln', '--no-build')
)

foreach ($step in $steps) {
    Write-Host "dotnet $($step -join ' ')" -ForegroundColor Cyan
    & dotnet @step
    if ($LASTEXITCODE -ne 0) { throw "Step failed: dotnet $($step -join ' ')" }
}

Write-Host 'All checks passed.' -ForegroundColor Green
