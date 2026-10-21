$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $projectRoot "dist-v2\DesktopStamp.exe"
if (-not (Test-Path $exePath)) {
    throw "Compiled assembly not found at $exePath. Run scripts\build.ps1 first."
}

Add-Type -AssemblyName System.Drawing
[void][System.Reflection.Assembly]::LoadFrom($exePath)

$outDir = Join-Path $projectRoot "design\runtime-previews-v2"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$assetsDir = Join-Path $projectRoot "assets"
$repo = [DesktopStamp.Imaging.AssetRepository]::new($assetsDir)
$renderer = [DesktopStamp.UI.StampRenderer]::new($repo)
$seals = [DesktopStamp.Imaging.SealImageProcessor]::CreateBuiltInSampleSeals()
$circleSeal = $seals[0]

function Save-RendererSnapshot {
    param(
        [string]$FileName,
        [int]$CanvasSize,
        [DesktopStamp.Core.StampStateMachine]$StateMachine,
        [DesktopStamp.Imaging.SealAsset]$Seal,
        [DesktopStamp.HostAdapters.StampTargetProbe]$Probe,
        [double]$Dpi
    )
    $bmp = [System.Drawing.Bitmap]::new($CanvasSize, $CanvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $renderer.RenderFrame($g, $CanvasSize, $CanvasSize, $StateMachine, $Seal, $Probe, $Dpi, $null, 0.0)
        $fullPath = Join-Path $outDir $FileName
        $bmp.Save($fullPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $corner = $bmp.GetPixel(0, 0)
        Write-Output ("  [Preview] {0} -> {1}x{2} (CornerAlpha={3}, State={4}, Morph={5:F3})" -f $FileName, $bmp.Width, $bmp.Height, $corner.A, $StateMachine.CurrentState, $StateMachine.MorphProgress)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

Write-Output "==> Exporting v2 offscreen renderer PNG previews + transition sequences to $outDir..."

# 1. Idle (Parked upright)
$smIdle = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
Save-RendererSnapshot -FileName "01-idle.png" -CanvasSize 460 -StateMachine $smIdle -Seal $circleSeal -Probe $null -Dpi 96.0

# 2. Hover (Breathing crimson base glow + active seal name pill)
$smHover = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
$smHover.SetHoverInIdle($true)
for ($i = 0; $i -lt 15; $i++) { $smHover.Update(0.05) }
Save-RendererSnapshot -FileName "02-hover.png" -CanvasSize 460 -StateMachine $smHover -Seal $circleSeal -Probe $null -Dpi 96.0

# 3. Drag Pickup (Lifted & tilted pose blend with spring-damper velocity tilt)
$smDrag = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(600, 600), 42)
[void]$smDrag.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(320, 420))
$smDrag.Update(0.055)
Save-RendererSnapshot -FileName "03-drag-pickup.png" -CanvasSize 460 -StateMachine $smDrag -Seal $circleSeal -Probe $null -Dpi 96.0

# 4. Aiming (Top-down optical reticle with transparent aperture + 40mm SAMPLE seal ghost)
$smAim = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
[void]$smAim.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(400, 400))
[void]$smAim.CompletePickupToAiming([DesktopStamp.Core.Vector2D]::new(400, 400))
for ($i = 0; $i -lt 10; $i++) { $smAim.Update(0.04) }
$probeWord100 = [DesktopStamp.HostAdapters.StampTargetProbe]::new()
$probeWord100.IsValidTarget = $true
$probeWord100.AdapterName = "WordStampHostAdapter"
$probeWord100.MonitorDpi = 96.0
$probeWord100.DocumentZoomPercent = 100.0
$probeWord100.PageNumber = 1
Save-RendererSnapshot -FileName "04-aiming.png" -CanvasSize 460 -StateMachine $smAim -Seal $circleSeal -Probe $probeWord100 -Dpi 96.0

# 5. Stamping Impact (Z-compression rebound + expanding shockwave ripple + signed jitter)
$smImpact = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
[void]$smImpact.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(400, 400))
[void]$smImpact.CompletePickupToAiming([DesktopStamp.Core.Vector2D]::new(400, 400))
for ($i = 0; $i -lt 10; $i++) { $smImpact.Update(0.04) }
[void]$smImpact.TryTriggerStamp([DesktopStamp.Core.Vector2D]::new(400, 400))
$smImpact.Update(0.095) # Just past peak impact so Z-compression and expanding shockwave ripple are both visible
Save-RendererSnapshot -FileName "05-stamping-impact.png" -CanvasSize 460 -StateMachine $smImpact -Seal $circleSeal -Probe $probeWord100 -Dpi 96.0

# 6. High-DPI (144 DPI) High-Zoom (200%) Wide Signature SAMPLE Seal in Aiming mode
try {
    $wideSeal = [DesktopStamp.Imaging.SealImageProcessor]::CreateWideSignatureSampleSeal(40.0)
    try {
        $probeHigh = [DesktopStamp.HostAdapters.StampTargetProbe]::new()
        $probeHigh.IsValidTarget = $true
        $probeHigh.AdapterName = "WordStampHostAdapter"
        $probeHigh.MonitorDpi = 144.0
        $probeHigh.DocumentZoomPercent = 200.0
        $probeHigh.PageNumber = 2

        $dynCanvas = $renderer.ComputeRequiredCanvasSize(40.0, $wideSeal.InkAspectRatio, 144.0, 200.0)
        Save-RendererSnapshot -FileName "06-aiming-wide-signature-highdpi-highzoom.png" -CanvasSize $dynCanvas -StateMachine $smAim -Seal $wideSeal -Probe $probeHigh -Dpi 144.0
    }
    finally {
        $wideSeal.Dispose()
    }

    # 7. Transition Sequence A: Before / At / After Upright-to-Drag Handoff (morph ~0.06, ~0.08, ~0.12)
    $smHandoffA = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
    [void]$smHandoffA.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(320, 380))
    for ($step = 0; $step -lt 500 -and $smHandoffA.MorphProgress -lt 0.058; $step++) { $smHandoffA.Update(0.002) }
    Save-RendererSnapshot -FileName "07a-handoff-upright-before-morph006.png" -CanvasSize 460 -StateMachine $smHandoffA -Seal $circleSeal -Probe $null -Dpi 96.0
    for ($step = 0; $step -lt 500 -and $smHandoffA.MorphProgress -lt 0.081; $step++) { $smHandoffA.Update(0.002) }
    Save-RendererSnapshot -FileName "07b-handoff-upright-at-morph008.png" -CanvasSize 460 -StateMachine $smHandoffA -Seal $circleSeal -Probe $null -Dpi 96.0
    for ($step = 0; $step -lt 500 -and $smHandoffA.MorphProgress -lt 0.125; $step++) { $smHandoffA.Update(0.002) }
    Save-RendererSnapshot -FileName "07c-handoff-upright-after-morph013.png" -CanvasSize 460 -StateMachine $smHandoffA -Seal $circleSeal -Probe $null -Dpi 96.0

    # 8. Transition Sequence B: Before / At / After Drag-to-Reticle Handoff (morph ~0.68, ~0.72, ~0.76)
    $smHandoffB = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
    [void]$smHandoffB.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(420, 420))
    [void]$smHandoffB.CompletePickupToAiming([DesktopStamp.Core.Vector2D]::new(420, 420))
    for ($step = 0; $step -lt 500 -and $smHandoffB.MorphProgress -lt 0.67; $step++) { $smHandoffB.Update(0.004) }
    Save-RendererSnapshot -FileName "08a-handoff-reticle-before-morph068.png" -CanvasSize 460 -StateMachine $smHandoffB -Seal $circleSeal -Probe $probeWord100 -Dpi 96.0
    for ($step = 0; $step -lt 500 -and $smHandoffB.MorphProgress -lt 0.72; $step++) { $smHandoffB.Update(0.002) }
    Save-RendererSnapshot -FileName "08b-handoff-reticle-at-morph072.png" -CanvasSize 460 -StateMachine $smHandoffB -Seal $circleSeal -Probe $probeWord100 -Dpi 96.0
    for ($step = 0; $step -lt 500 -and $smHandoffB.MorphProgress -lt 0.76; $step++) { $smHandoffB.Update(0.002) }
    Save-RendererSnapshot -FileName "08c-handoff-reticle-after-morph076.png" -CanvasSize 460 -StateMachine $smHandoffB -Seal $circleSeal -Probe $probeWord100 -Dpi 96.0

    # 9. Transition Sequence C: Animated Return Arc (Early Reticle -> Mid Tilted Body -> Upright Park Settle)
    $smReturn = [DesktopStamp.Core.StampStateMachine]::new([DesktopStamp.Core.StampConfiguration]::new(), [DesktopStamp.Core.Vector2D]::new(500, 500), 42)
    [void]$smReturn.BeginPickupDrag([DesktopStamp.Core.Vector2D]::new(350, 350))
    [void]$smReturn.CompletePickupToAiming([DesktopStamp.Core.Vector2D]::new(350, 350))
    for ($i = 0; $i -lt 10; $i++) { $smReturn.Update(0.04) }
    [void]$smReturn.BeginReturnToParked()
    $smReturn.Update(0.045) # Early return (reticle morphing back)
    Save-RendererSnapshot -FileName "09a-return-arc-early.png" -CanvasSize 460 -StateMachine $smReturn -Seal $circleSeal -Probe $null -Dpi 96.0
    $smReturn.Update(0.085) # Mid return (tilted body gliding back)
    Save-RendererSnapshot -FileName "09b-return-arc-mid.png" -CanvasSize 460 -StateMachine $smReturn -Seal $circleSeal -Probe $null -Dpi 96.0
    $smReturn.Update(0.115) # Late settle into upright park
    Save-RendererSnapshot -FileName "09c-return-arc-settle.png" -CanvasSize 460 -StateMachine $smReturn -Seal $circleSeal -Probe $null -Dpi 96.0
}
finally {
    foreach ($s in $seals) { $s.Dispose() }
    $renderer.Dispose()
    $repo.Dispose()
}
