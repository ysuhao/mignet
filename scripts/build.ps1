$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    throw "MSBuild.exe was not found on this machine."
}

Write-Host "==> Building DesktopStamp v2 (Release -> dist-v2)..." -ForegroundColor Cyan
& $msbuild "$projectRoot\src\DesktopStamp.csproj" /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { throw "DesktopStamp build failed with exit code $LASTEXITCODE" }

Write-Host "==> Building DesktopStamp.Tests v2 (Release -> dist-v2\tests)..." -ForegroundColor Cyan
& $msbuild "$projectRoot\tests\DesktopStamp.Tests.csproj" /p:Configuration=Release /v:minimal
if ($LASTEXITCODE -ne 0) { throw "DesktopStamp.Tests build failed with exit code $LASTEXITCODE" }

if (Test-Path "$projectRoot\benchmarks\DesktopStamp.Benchmarks.csproj") {
    Write-Host "==> Building DesktopStamp.Benchmarks (Release -> dist-v2\bench)..." -ForegroundColor Cyan
    & $msbuild "$projectRoot\benchmarks\DesktopStamp.Benchmarks.csproj" /p:Configuration=Release /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "DesktopStamp.Benchmarks build failed with exit code $LASTEXITCODE" }
}

# Copy assets into dist-v2/assets so dist-v2/DesktopStamp.exe is completely self-contained and launchable directly
$distAssets = Join-Path $projectRoot "dist-v2\assets"
New-Item -ItemType Directory -Force -Path $distAssets | Out-Null
Copy-Item -Path "$projectRoot\assets\*" -Destination $distAssets -Force

Write-Host "==> Packaged runnable v2 build at: $projectRoot\dist-v2\DesktopStamp.exe" -ForegroundColor Green
