$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$resultsFile = Join-Path $projectRoot "test-results.txt"

& "$PSScriptRoot\build.ps1"

$logLines = [System.Collections.Generic.List[string]]::new()
$logLines.Add("Timestamp: $(Get-Date -Format o)")

$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    Write-Host "==> Exporting v2 offscreen renderer PNG previews..." -ForegroundColor Cyan
    $previewOut = (& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\export-previews.ps1" 2>&1 | Out-String)
    $previewExit = $LASTEXITCODE
    Write-Host $previewOut
    $logLines.Add("=== Offscreen Renderer Previews (scripts/export-previews.ps1) ===")
    $logLines.Add($previewOut.TrimEnd())

    Write-Host "==> Running dist-v2\DesktopStamp.exe --smoke-check..." -ForegroundColor Cyan
    $smokeOut = (& "$projectRoot\dist-v2\DesktopStamp.exe" --smoke-check 2>&1 | Out-String)
    $smokeExit = $LASTEXITCODE
    Write-Host $smokeOut
    $logLines.Add("=== dist-v2\DesktopStamp.exe --smoke-check ===")
    $logLines.Add($smokeOut.TrimEnd())

    Write-Host "==> Running dist-v2\tests\DesktopStamp.Tests.exe..." -ForegroundColor Cyan
    $testOut = (& "$projectRoot\dist-v2\tests\DesktopStamp.Tests.exe" 2>&1 | Out-String)
    $testExit = $LASTEXITCODE
    Write-Host $testOut
    $logLines.Add("=== dist-v2\tests\DesktopStamp.Tests.exe ===")
    $logLines.Add($testOut.TrimEnd())
}
finally {
    $ErrorActionPreference = $prevEap
    Set-Content -Path $resultsFile -Value ($logLines -join [Environment]::NewLine) -Encoding UTF8
    Write-Host "==> Recorded console output to: $resultsFile" -ForegroundColor Green
}

if ($previewExit -ne 0) {
    throw "export-previews.ps1 failed with exit code $previewExit"
}
if ($smokeExit -ne 0) {
    throw "DesktopStamp.exe --smoke-check failed with exit code $smokeExit"
}
if ($testExit -ne 0) {
    throw "DesktopStamp.Tests.exe failed with exit code $testExit"
}

Write-Host "==> All v2 validation checks, preview exports, and smoke tests passed." -ForegroundColor Green
