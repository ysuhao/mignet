using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DesktopStamp.Audio;
using DesktopStamp.Core;
using DesktopStamp.HostAdapters;
using DesktopStamp.Imaging;
using DesktopStamp.UI;

namespace DesktopStamp.Tests
{
    public static class Program
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        public static int Main(string[] args)
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Desktop Physical Stamp v2 — Automated Validation & Smoke Tests");
            Console.WriteLine("==================================================================");

            RunTest("1. State Machine Lifecycle & Repeated Aiming Contract", TestStateMachineLifecycle);
            RunTest("2. Elapsed-Time (dt) Spring-Damper Frame-Rate Stability", TestElapsedTimeStability);
            RunTest("3. Double-Stamp Debounce & Signed ±1°..3° Angular Jitter", TestDebounceAndAngularJitter);
            RunTest("4. Size Locks (20mm/40mm) vs Free Ctrl+Wheel Scaling", TestSizeLocksAndWheelScaling);
            RunTest("5. CoordinateTransformer: mm->pt, DPI, Zoom & Anchor Cancellation", TestCoordinateTransformations);
            RunTest("6. Non-Square Seals (Wide Signature & Tall Chop): Independent Padding Ratios", TestNonSquareSealAspectPreservation);
            RunTest("7. High-Zoom (100/150/200%) & High-DPI (96/144/192) Unclamped Ghost & Dynamic Canvas", TestHighZoomAndDpiGhostScaling);
            RunTest("8. SealImageProcessor: Alpha Preservation & 80-85% Opacity", TestImagePipelineAlphaPreservation);
            RunTest("9. SealImageProcessor: Red Seal Extraction from Opaque Scan over Black Text", TestImagePipelineRedExtraction);
            RunTest("10. SealImageProcessor: Non-Destructive Per-Impression Noise & Input Rejection", TestNonDestructiveImpressionAndRejection);
            RunTest("11. Reticle Full-Aperture Alpha==0 Masking (Eliminating 1/255 Residue) & Ghost Preservation", TestAssetsAndReticleTransparency);
            RunTest("12. Partial Word Shape Creation Failure Rollback", TestWordShapeFailureRollback);
            RunTest("13. Extension Point Host Stubs (PDF / Excel / Screenshot) Fail-Closed Rejection", TestHostStubsFailClosed);
            RunTest("14. Synthesized Audio PCM WAV Header & Signal Integrity", TestAudioSynthesizer);
            RunTest("15. Win32 Layered Overlay WS_EX_TRANSPARENT Toggle & GlobalInputHook Lifecycle", TestLayeredWindowAndGlobalHookLifecycle);
            RunTest("16. Live Disposable Word COM: Multi-Page, Multi-Window HWND Binding & Non-Square Stamping", TestDisposableWordComIntegration);
            RunTest("17. v2 Non-Blocking AsyncStampHostWorker: Probe Coalescing, Bounded FIFO Queue & Epoch Safety", TestAsyncHostWorkerCoalescingAndEpochSafety);
            RunTest("18. v2 StampRenderer Caches, Automatic Invalidation & Zero Opacity Dropout at morph=0.72", TestRendererCachesAndHandoffContinuity);
            RunTest("19. v2 Interrupted Motions: Smooth Release Convergence, Partial Cancel, Mid-Return Re-Pickup & Reduced Motion", TestInterruptedMotionsAndReducedMotion);
            RunTest("20. v2 High-Resolution Mouse Wheel Accumulation (120-Unit Notches & Direction Reversal)", TestHighResolutionWheelAccumulation);
            RunTest("21. v2 DPI-Aware Parking, Keyboard Context Menu Accessibility & Persistent DIB Handle Stability", TestDpiParkingMenuKeyboardAndDibStability);
            RunTest("22. v2 Deterministic Click-Snapshot Guard, Pre-Mutation Abort, Post-AddPicture Rollback & Stale Probe Rejection", TestDeterministicStampGuardAndRollbackOnLateInvalidation);
            RunTest("23. v2 WH_MOUSE_LL Real Hook Callback: Plain Wheel Pass-Through vs Ctrl/Alt/Ring Swallow & Injected Filtering", TestLowLevelHookWheelRoutingAndPassThrough);

            Console.WriteLine("------------------------------------------------------------------");
            Console.WriteLine($"TOTAL PASSED: {_passed}, TOTAL FAILED: {_failed}");
            Console.WriteLine("==================================================================");

            return _failed == 0 ? 0 : 1;
        }

        private static void RunTest(string name, Action testAction)
        {
            try
            {
                testAction();
                _passed++;
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"[FAIL] {name}");
                Console.WriteLine($"       {ex.Message}");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void AssertNear(double actual, double expected, double tolerance, string label)
        {
            if (Math.Abs(actual - expected) > tolerance)
                throw new InvalidOperationException($"{label}: expected {expected:F4} ± {tolerance:F4}, got {actual:F4}");
        }

        private static void TestStateMachineLifecycle()
        {
            var config = new StampConfiguration();
            var parked = new Vector2D(1500, 850);
            var sm = new StampStateMachine(config, parked, randomSeed: 101);

            Assert(sm.CurrentState == StampState.Idle, "Initial state must be Idle.");
            sm.SetHoverInIdle(true);
            sm.Update(0.25);
            Assert(sm.HoverProgress > 0.6, "HoverProgress should increase when hovered in Idle.");

            var dragTarget = new Vector2D(900, 500);
            Assert(sm.BeginPickupDrag(dragTarget), "BeginPickupDrag should succeed from Idle.");
            Assert(sm.CurrentState == StampState.DraggingPickup, "State should be DraggingPickup.");

            sm.Update(0.18);
            Assert(sm.MorphProgress > 0.3, "MorphProgress should advance during pickup drag.");
            Assert(Math.Abs(sm.TiltAngleDeg) > 0.5, "Velocity-dependent tilt should be non-zero during fast pickup movement.");

            Assert(sm.CompletePickupToAiming(dragTarget), "CompletePickupToAiming should succeed.");
            Assert(sm.CurrentState == StampState.Aiming, "Release must leave stamp suspended in Aiming mode.");

            int impactCount = 0;
            sm.StampImpactCommitted += (pos, sizeMm, angleDeg) => impactCount++;

            Assert(sm.TryTriggerStamp(dragTarget), "TryTriggerStamp should succeed in Aiming.");
            Assert(sm.CurrentState == StampState.Stamping, "State should transition to Stamping.");

            sm.Update(0.08);
            Assert(impactCount == 1, $"Impact event must fire once during plunge (got {impactCount}).");
            Assert(sm.ZCompression > 0.2, "ZCompression should be elevated near impact.");

            sm.Update(0.20);
            Assert(sm.CurrentState == StampState.Aiming, "Stamp must return to Aiming mode after each impression for repeated work.");
            Assert(impactCount == 1, "Impact event must not fire a second time during rebound.");

            Assert(sm.BeginReturnToParked(), "BeginReturnToParked should succeed from Aiming.");
            Assert(sm.CurrentState == StampState.Returning, "State should be Returning.");

            sm.Update(config.ReturnArcDurationSeconds * 0.5);
            double linearMidY = (dragTarget.Y + parked.Y) * 0.5;
            Assert(sm.CurrentPosition.Y < linearMidY - 10.0, "Returning trajectory must follow an elevated smooth arc.");

            sm.Update(config.ReturnArcDurationSeconds * 0.6);
            Assert(sm.CurrentState == StampState.Idle, "State must return to Idle after return arc completes.");
            AssertNear(Vector2D.Distance(sm.CurrentPosition, parked), 0.0, 0.5, "Final position at parked dock");
        }

        private static void TestElapsedTimeStability()
        {
            var start = new Vector2D(1600, 900);
            var target = new Vector2D(750, 420);

            var sm30Fps = new StampStateMachine(new StampConfiguration(), start, 1);
            var sm240Fps = new StampStateMachine(new StampConfiguration(), start, 1);

            sm30Fps.BeginPickupDrag(target);
            sm240Fps.BeginPickupDrag(target);

            for (int i = 0; i < 12; i++)
                sm30Fps.Update(0.40 / 12.0);

            for (int i = 0; i < 96; i++)
                sm240Fps.Update(0.40 / 96.0);

            double posDiff = Vector2D.Distance(sm30Fps.CurrentPosition, sm240Fps.CurrentPosition);
            Assert(posDiff < 0.01, $"Sub-stepped spring-damper must be dt-independent (posDiff={posDiff:F5}px).");
        }

        private static void TestDebounceAndAngularJitter()
        {
            var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), randomSeed: 777);
            sm.BeginPickupDrag(new Vector2D(400, 400));
            sm.CompletePickupToAiming(new Vector2D(400, 400));
            sm.RotateByWheel(5); // BaseRotationDeg = +15.0°
            AssertNear(sm.BaseRotationDeg, 15.0, 1e-4, "BaseRotationDeg");

            Assert(sm.TryTriggerStamp(new Vector2D(400, 400)), "First stamp should trigger.");
            Assert(!sm.TryTriggerStamp(new Vector2D(400, 400)), "Click during Stamping must be rejected.");

            sm.Update(0.225);
            Assert(sm.CurrentState == StampState.Aiming, "Should be back in Aiming.");
            Assert(!sm.TryTriggerStamp(new Vector2D(400, 400)), "Click inside debounce cooldown window must be rejected.");

            sm.Update(0.08);

            int positiveJitterCount = 0;
            int negativeJitterCount = 0;

            for (int i = 0; i < 40; i++)
            {
                Assert(sm.TryTriggerStamp(new Vector2D(400, 400)), $"Stamp #{i} should succeed after cooldown.");
                double jitter = sm.LastImpressionJitterDeg;
                double absJitter = Math.Abs(jitter);

                Assert(absJitter >= 1.0 && absJitter <= 3.0,
                    $"Per-impression jitter magnitude {absJitter:F3}° must lie in [1.0°, 3.0°].");

                if (jitter > 0) positiveJitterCount++;
                if (jitter < 0) negativeJitterCount++;

                AssertNear(sm.EffectiveImpressionAngleDeg, 15.0 + jitter, 1e-4, "EffectiveImpressionAngleDeg");
                sm.Update(0.16);
                sm.Update(0.16);
            }

            Assert(positiveJitterCount > 5 && negativeJitterCount > 5,
                $"Signed jitter must produce both positive ({positiveJitterCount}) and negative ({negativeJitterCount}) angles.");
        }

        private static void TestSizeLocksAndWheelScaling()
        {
            var sm = new StampStateMachine(new StampConfiguration());
            Assert(sm.SizeLock == SizeLockMode.Unlocked, "Default size lock should be Unlocked so Ctrl+wheel works immediately.");
            AssertNear(sm.PhysicalSizeMm, 40.0, 1e-4, "Default size 40mm");

            Assert(sm.AdjustPhysicalSizeByWheel(2), "Ctrl+Wheel should scale when Unlocked.");
            AssertNear(sm.PhysicalSizeMm, 43.0, 1e-4, "Scaled size");

            sm.SetSizeLock(SizeLockMode.Locked20mm);
            AssertNear(sm.PhysicalSizeMm, 20.0, 1e-4, "Locked 20mm size");
            Assert(!sm.AdjustPhysicalSizeByWheel(4), "Ctrl+Wheel must not alter size while Locked20mm is active.");
            AssertNear(sm.PhysicalSizeMm, 20.0, 1e-4, "Still 20mm");

            sm.SetSizeLock(SizeLockMode.Locked40mm);
            AssertNear(sm.PhysicalSizeMm, 40.0, 1e-4, "Locked 40mm size");
            Assert(!sm.AdjustPhysicalSizeByWheel(-3), "Ctrl+Wheel must not alter size while Locked40mm is active.");
        }

        private static void TestCoordinateTransformations()
        {
            double pt40 = CoordinateTransformer.MillimetersToPoints(40.0);
            double pt20 = CoordinateTransformer.MillimetersToPoints(20.0);
            AssertNear(pt40, 113.38582677, 1e-5, "40mm in points");
            AssertNear(pt20, 56.69291339, 1e-5, "20mm in points");
            AssertNear(CoordinateTransformer.PointsToMillimeters(pt40), 40.0, 1e-6, "Roundtrip 40mm");

            double dpi = 144.0;
            double zoom = 125.0;
            double pxPerPt = CoordinateTransformer.ComputePixelsPerPoint(dpi, zoom);
            AssertNear(pxPerPt, 2.5, 1e-6, "PixelsPerPoint at 144 DPI, 125% zoom");

            var truePageOriginPx = new Vector2D(420.0, 260.0);
            var targetScreenPx = new Vector2D(945.0, 1110.0);

            var map1 = CoordinateTransformer.MapScreenToPagePoints(
                targetScreenPx,
                new Vector2D(645.0, 440.0),
                anchorPageLeftPt: 90.0,
                anchorPageTopPt: 72.0,
                pageWidthPt: 595.3,
                pageHeightPt: 841.9,
                monitorDpi: dpi,
                zoomPercent: zoom,
                physicalInkWidthMm: 40.0,
                aspectRatio: 1.0,
                inkToCanvasWidthRatio: 0.95,
                pageNumber: 2,
                inkToCanvasHeightRatio: 0.95);

            var anchor2Screen = new Vector2D(truePageOriginPx.X + 412.5 * pxPerPt, truePageOriginPx.Y + 680.2 * pxPerPt);
            var map2 = CoordinateTransformer.MapScreenToPagePoints(
                targetScreenPx,
                anchor2Screen,
                anchorPageLeftPt: 412.5,
                anchorPageTopPt: 680.2,
                pageWidthPt: 595.3,
                pageHeightPt: 841.9,
                monitorDpi: dpi,
                zoomPercent: zoom,
                physicalInkWidthMm: 40.0,
                aspectRatio: 1.0,
                inkToCanvasWidthRatio: 0.95,
                pageNumber: 2,
                inkToCanvasHeightRatio: 0.95);

            Assert(map1.IsValid && map2.IsValid, "Both anchor mappings must be valid.");
            AssertNear(map1.CenterPageLeftPt, 210.0, 1e-6, "CenterPageLeftPt from Anchor 1");
            AssertNear(map1.CenterPageTopPt, 340.0, 1e-6, "CenterPageTopPt from Anchor 1");
            AssertNear(map2.CenterPageLeftPt, map1.CenterPageLeftPt, 1e-6, "Anchor invariance X");
            AssertNear(map2.CenterPageTopPt, map1.CenterPageTopPt, 1e-6, "Anchor invariance Y");

            AssertNear(map1.InkWidthPt, pt40, 1e-5, "InkWidthPt must equal exact 40mm in points");
            AssertNear(map1.ShapeWidthPt * 0.95, pt40, 1e-5, "Visible ink inside shape must equal exact 40mm");

            AssertNear(map1.ShapeLeftPt + map1.ShapeWidthPt * 0.5, map1.CenterPageLeftPt, 1e-6, "Center-anchored Left");
            AssertNear(map1.ShapeTopPt + map1.ShapeHeightPt * 0.5, map1.CenterPageTopPt, 1e-6, "Center-anchored Top");

            var outsideScreenPx = new Vector2D(truePageOriginPx.X - 40.0, truePageOriginPx.Y + 200.0);
            var invalidMap = CoordinateTransformer.MapScreenToPagePoints(
                outsideScreenPx,
                new Vector2D(645.0, 440.0),
                90.0, 72.0, 595.3, 841.9, dpi, zoom, 40.0);
            Assert(!invalidMap.IsValid, "Coordinates outside the physical page bounds must fail closed.");
        }

        /// <summary>
        /// Regression test for Codex Review #1:
        /// Verifies that wide signatures (e.g. 800x80 ink bbox) and tall rectangular chops (e.g. 100x300 ink bbox)
        /// carry independent horizontal and vertical ink-to-canvas ratios so equal-pixel margins never distort
        /// vertical aspect ratio in either Word mapping or aiming ghost preview.
        /// </summary>
        private static void TestNonSquareSealAspectPreservation()
        {
            // 1. Wide signature (10:1 ink aspect ratio: 600x60 ink bbox inside 640x100 bitmap)
            using (var wideRaw = new Bitmap(640, 120, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(wideRaw))
                {
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(220, 210, 24, 28)))
                    {
                        g.FillRectangle(brush, 20, 30, 600, 60);
                    }
                }

                SealImportResult wideRes = SealImageProcessor.ProcessSealBitmap(wideRaw, "WideSignatureSample", isBuiltInSample: true);
                Assert(wideRes.Success, $"Wide signature processing failed: {wideRes.ErrorMessage}");
                using (SealAsset wideSeal = wideRes.Asset)
                {
                    // Notice InkToCanvasWidthRatio (~600/630) != InkToCanvasHeightRatio (~60/90)
                    Assert(Math.Abs(wideSeal.InkToCanvasWidthRatio - wideSeal.InkToCanvasHeightRatio) > 0.10,
                        "Wide seal must have distinct horizontal and vertical ink-to-canvas ratios.");

                    PageCoordinateMapping mapWide = CoordinateTransformer.MapScreenToPagePoints(
                        new Vector2D(500, 500),
                        new Vector2D(400, 400),
                        72.0, 72.0, 595.3, 841.9,
                        monitorDpi: 96.0,
                        zoomPercent: 100.0,
                        physicalInkWidthMm: 40.0,
                        aspectRatio: wideSeal.InkAspectRatio,
                        inkToCanvasWidthRatio: wideSeal.InkToCanvasWidthRatio,
                        pageNumber: 1,
                        inkToCanvasHeightRatio: wideSeal.InkToCanvasHeightRatio);

                    Assert(mapWide.IsValid, "Wide seal mapping must be valid.");
                    double expectedInkW = CoordinateTransformer.MillimetersToPoints(40.0);
                    double expectedInkH = expectedInkW * wideSeal.InkAspectRatio;

                    // Verify placed canvas shape aspect ratio matches MasterBitmap aspect ratio exactly
                    AssertNear(mapWide.ShapeHeightPt / mapWide.ShapeWidthPt, wideSeal.CanvasAspectRatio, 1e-5,
                        "Wide signature Word Shape aspect ratio must equal MasterBitmap CanvasAspectRatio");

                    // Verify visible ink width AND height inside the placed shape match 40mm and 4mm
                    AssertNear(mapWide.ShapeWidthPt * wideSeal.InkToCanvasWidthRatio, expectedInkW, 1e-4,
                        "Visible ink width in points for wide signature");
                    AssertNear(mapWide.ShapeHeightPt * wideSeal.InkToCanvasHeightRatio, expectedInkH, 1e-4,
                        "Visible ink height in points for wide signature");
                }
            }

            // 2. Tall rectangular chop (1:3 ink aspect ratio: 100x300 ink bbox)
            using (var tallRaw = new Bitmap(160, 360, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(tallRaw))
                {
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(220, 208, 22, 26)))
                    {
                        g.FillRectangle(brush, 30, 30, 100, 300);
                    }
                }

                SealImportResult tallRes = SealImageProcessor.ProcessSealBitmap(tallRaw, "TallChopSample", isBuiltInSample: true);
                Assert(tallRes.Success, $"Tall chop processing failed: {tallRes.ErrorMessage}");
                using (SealAsset tallSeal = tallRes.Asset)
                {
                    PageCoordinateMapping mapTall = CoordinateTransformer.MapScreenToPagePoints(
                        new Vector2D(500, 500),
                        new Vector2D(400, 400),
                        72.0, 72.0, 595.3, 841.9,
                        monitorDpi: 144.0,
                        zoomPercent: 150.0,
                        physicalInkWidthMm: 20.0,
                        aspectRatio: tallSeal.InkAspectRatio,
                        inkToCanvasWidthRatio: tallSeal.InkToCanvasWidthRatio,
                        pageNumber: 1,
                        inkToCanvasHeightRatio: tallSeal.InkToCanvasHeightRatio);

                    double expectedInkW = CoordinateTransformer.MillimetersToPoints(20.0);
                    double expectedInkH = expectedInkW * tallSeal.InkAspectRatio;

                    AssertNear(mapTall.ShapeHeightPt / mapTall.ShapeWidthPt, tallSeal.CanvasAspectRatio, 1e-5,
                        "Tall chop Word Shape aspect ratio must equal MasterBitmap CanvasAspectRatio");
                    AssertNear(mapTall.ShapeWidthPt * tallSeal.InkToCanvasWidthRatio, expectedInkW, 1e-4,
                        "Visible ink width in points for tall chop");
                    AssertNear(mapTall.ShapeHeightPt * tallSeal.InkToCanvasHeightRatio, expectedInkH, 1e-4,
                        "Visible ink height in points for tall chop");
                }
            }
        }

        /// <summary>
        /// Regression test for Codex Review #5:
        /// Verifies unclamped aiming ghost scale and aperture enclosure across 100%, 150%, 200% zoom
        /// and 96, 144, 192 DPI (including 40mm at 144 DPI & 200% zoom = 453.54px), and verifies dynamic canvas sizing.
        /// </summary>
        private static void TestHighZoomAndDpiGhostScaling()
        {
            using (var repo = new AssetRepository())
            {
                var renderer = new StampRenderer(repo);
                double[] dpis = { 96.0, 144.0, 192.0 };
                double[] zooms = { 100.0, 150.0, 200.0 };
                double[] aspects = { 1.0, 0.15, 2.5 }; // circular, wide signature, tall chop

                foreach (double dpi in dpis)
                {
                    foreach (double zoom in zooms)
                    {
                        foreach (double aspect in aspects)
                        {
                            renderer.GetReticleRadiiPx(
                                40.0,
                                dpi,
                                zoom,
                                out float inkW,
                                out float inkH,
                                out float innerR,
                                out float outerR,
                                aspect);

                            double expectedW = CoordinateTransformer.MillimetersToScreenPixels(40.0, dpi, zoom);
                            double expectedH = expectedW * aspect;

                            AssertNear(inkW, expectedW, 0.05, $"Unclamped inkWidthPx at DPI={dpi}, Zoom={zoom}%");
                            AssertNear(inkH, expectedH, 0.05, $"Unclamped inkHeightPx at DPI={dpi}, Zoom={zoom}%, Aspect={aspect}");

                            // Specific check mentioned in review: 40mm at 144 DPI and 200% zoom is ~453.54px
                            if (Math.Abs(dpi - 144.0) < 0.1 && Math.Abs(zoom - 200.0) < 0.1)
                            {
                                AssertNear(inkW, 453.5433, 0.05, "40mm at 144 DPI & 200% zoom must be ~453.5px (never clamped to 300px)");
                            }

                            // Reticle aperture must enclose the full seal including diagonal corners (even for 1:1 square chops!)
                            double cornerDiagonalRadius = 0.5 * Math.Sqrt((double)inkW * inkW + (double)inkH * inkH);
                            Assert(innerR >= cornerDiagonalRadius + 13.5,
                                $"Inner aperture radius ({innerR:F1}px) must enclose full diagonal corner radius ({cornerDiagonalRadius:F1}px) for aspect={aspect}.");

                            int reqCanvas = renderer.ComputeRequiredCanvasSize(40.0, aspect, dpi, zoom);
                            Assert(reqCanvas >= outerR * 2.0 + 40.0,
                                $"Dynamic canvas size ({reqCanvas}px) must fit outer ring diameter ({outerR * 2.0:F1}px).");
                        }
                    }
                }

                // Verify 2400px canvas cap explicitly rejects unsupported extreme preview extents with a concise customer zoom-out message
                bool supportedExtreme = renderer.IsPreviewExtentSupported(
                    75.0, 1.0, 192.0, 400.0, out int extremeCanvasPx, out string extremeMsg);
                Assert(!supportedExtreme && extremeCanvasPx > StampRenderer.MaxSupportedCanvasSizePx,
                    $"Extreme extent ({extremeCanvasPx}px) must be flagged as unsupported (> {StampRenderer.MaxSupportedCanvasSizePx}px).");
                Assert(extremeMsg == "请缩小 Word 视图比例或减小印章尺寸后再盖章",
                    $"Unsupported extent message must use concise customer-facing text: '{extremeMsg}'");
            }
        }

        private static void TestImagePipelineAlphaPreservation()
        {
            var seals = SealImageProcessor.CreateBuiltInSampleSeals();
            Assert(seals.Count >= 3, "Must generate at least 3 built-in SAMPLE/样章 seals.");

            foreach (var seal in seals)
            {
                Assert(seal.Name.Contains("样章") && seal.Name.Contains("SAMPLE"),
                    $"Built-in seal '{seal.Name}' must clearly identify itself as SAMPLE/样章.");
                Assert(seal.PreservedOriginalAlpha, "Built-in transparent seal must preserve alpha.");
                Assert(seal.PeakOpacity >= SealImageProcessor.TargetMinOpacity &&
                       seal.PeakOpacity <= SealImageProcessor.TargetMaxOpacity,
                    $"Peak opacity {seal.PeakOpacity:F3} must be in [0.80, 0.85].");
                Assert(seal.InkToCanvasWidthRatio > 0.85 && seal.InkToCanvasWidthRatio < 1.0,
                    $"Tight ink crop ratio {seal.InkToCanvasWidthRatio:F3} should include feather margin.");

                Color corner = seal.MasterBitmap.GetPixel(0, 0);
                Assert(corner.A == 0, "Master seal corner pixel must have Alpha == 0.");
                seal.Dispose();
            }
        }

        private static void TestImagePipelineRedExtraction()
        {
            using (var scanBmp = new Bitmap(320, 320, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(scanBmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.FromArgb(255, 245, 242, 235));

                    using (var blackBrush = new SolidBrush(Color.FromArgb(255, 28, 28, 30)))
                    {
                        g.FillRectangle(blackBrush, 30, 80, 260, 14);
                        g.FillRectangle(blackBrush, 30, 155, 260, 14);
                        g.FillRectangle(blackBrush, 30, 230, 260, 14);
                    }

                    using (var redPen = new Pen(Color.FromArgb(255, 210, 28, 34), 16f))
                    {
                        g.DrawEllipse(redPen, 60, 60, 200, 200);
                    }
                }

                SealImportResult res = SealImageProcessor.ProcessSealBitmap(scanBmp, "ScannedSampleSeal");
                Assert(res.Success && res.Asset != null, $"Red extraction from opaque scan failed: {res.ErrorMessage}");
                using (SealAsset asset = res.Asset)
                {
                    Assert(!asset.PreservedOriginalAlpha, "Opaque scan should trigger chromatic red threshold extraction.");
                    Assert(asset.PeakOpacity >= 0.80 && asset.PeakOpacity <= 0.85,
                        $"Extracted seal peak opacity {asset.PeakOpacity:F3} must be in 80-85%.");

                    Color centerPx = asset.MasterBitmap.GetPixel(asset.MasterBitmap.Width / 2, asset.MasterBitmap.Height / 2);
                    Assert(centerPx.A == 0, $"Non-red paper/black text at center must become transparent (got A={centerPx.A}).");
                }
            }
        }

        private static void TestNonDestructiveImpressionAndRejection()
        {
            var seals = SealImageProcessor.CreateBuiltInSampleSeals();
            using (SealAsset seal = seals[0])
            {
                long masterChecksumBefore = ComputeBitmapAlphaSum(seal.MasterBitmap);

                using (Bitmap imp1 = SealImageProcessor.CreateImpressionBitmap(seal, impressionSeed: 1001))
                using (Bitmap imp2 = SealImageProcessor.CreateImpressionBitmap(seal, impressionSeed: 2002))
                {
                    long masterChecksumAfter = ComputeBitmapAlphaSum(seal.MasterBitmap);
                    Assert(masterChecksumBefore == masterChecksumAfter,
                        "CreateImpressionBitmap must NEVER destructively overwrite or mutate MasterBitmap.");

                    long imp1Sum = ComputeBitmapAlphaSum(imp1);
                    long imp2Sum = ComputeBitmapAlphaSum(imp2);
                    Assert(imp1Sum != masterChecksumBefore, "Impression bitmap must include micro-noise/ink gaps.");
                    Assert(imp1Sum != imp2Sum, "Distinct impression seeds must produce distinct per-impression ink textures.");
                }
            }
            for (int i = 1; i < seals.Count; i++) seals[i].Dispose();

            using (var grayBmp = new Bitmap(160, 160, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(grayBmp))
                {
                    g.Clear(Color.White);
                    g.FillRectangle(Brushes.Black, 20, 20, 100, 30);
                }

                SealImportResult failRes = SealImageProcessor.ProcessSealBitmap(grayBmp, "NoRedImage");
                Assert(!failRes.Success && !string.IsNullOrWhiteSpace(failRes.ErrorMessage),
                    "Opaque image without red seal ink must be rejected with a clear error message.");
            }
        }

        /// <summary>
        /// Regression test for Codex Review #6:
        /// Verifies that the 1/255 residual antialiasing alpha inside the 450px aperture of stamp-reticle-v1.png
        /// is masked to exact 0/255 in memory, and that drawing the reticle with a seal ghost preserves the seal ghost
        /// while keeping the surrounding aperture margin strictly Alpha == 0.
        /// </summary>
        private static void TestAssetsAndReticleTransparency()
        {
            using (var repo = new AssetRepository())
            {
                Assert(repo.IdleSprite != null, "stamp-idle-v1.png should be loaded by AssetRepository.");
                Assert(repo.DragSprite != null, "stamp-drag-v1.png should be loaded by AssetRepository.");
                Assert(repo.ReticleSprite != null, "stamp-reticle-v1.png should be loaded by AssetRepository.");

                // 1. Check every sampled pixel within radius 450px of ReticleSprite.OccupiedCenter has exact Alpha == 0
                Bitmap retBmp = repo.ReticleSprite.Bitmap;
                PointF occCenter = repo.ReticleSprite.OccupiedCenter;
                byte maxApertureAlpha = 0;
                for (int y = (int)occCenter.Y - 450; y <= (int)occCenter.Y + 450; y += 3)
                {
                    for (int x = (int)occCenter.X - 450; x <= (int)occCenter.X + 450; x += 3)
                    {
                        float dx = x - occCenter.X;
                        float dy = y - occCenter.Y;
                        if (dx * dx + dy * dy <= 450f * 450f)
                        {
                            byte a = retBmp.GetPixel(x, y).A;
                            if (a > maxApertureAlpha) maxApertureAlpha = a;
                        }
                    }
                }
                Assert(maxApertureAlpha == 0,
                    $"In-memory masked reticle aperture within R=450px must have max alpha 0/255 (got {maxApertureAlpha}/255).");

                var renderer = new StampRenderer(repo);
                var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), 1);
                sm.BeginPickupDrag(new Vector2D(400, 400));
                sm.CompletePickupToAiming(new Vector2D(400, 400));
                sm.Update(0.25);

                renderer.GetReticleRadiiPx(40.0, 96.0, 100.0, out float inkW, out float innerR, out float outerR);

                // 2. Render Aiming frame with activeSeal: null -> verify entire aperture inside innerR - 3 has Alpha == 0
                using (var canvas = new Bitmap(460, 460, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    renderer.RenderFrame(g, 460, 460, sm, activeSeal: null, targetProbe: null, monitorDpi: 96.0);
                    byte maxRenderedHoleAlpha = 0;
                    int testHoleR = (int)(innerR - 4f);
                    for (int dy = -testHoleR; dy <= testHoleR; dy += 4)
                    {
                        for (int dx = -testHoleR; dx <= testHoleR; dx += 4)
                        {
                            if (dx * dx + dy * dy <= testHoleR * testHoleR)
                            {
                                byte a = canvas.GetPixel(230 + dx, 230 + dy).A;
                                if (a > maxRenderedHoleAlpha) maxRenderedHoleAlpha = a;
                            }
                        }
                    }
                    Assert(maxRenderedHoleAlpha == 0,
                        $"Rendered aiming aperture must have max alpha 0/255 everywhere inside innerR (got {maxRenderedHoleAlpha}).");
                }

                // 3. Render Aiming frame WITH a seal ghost -> verify seal ghost is preserved in center while gap between seal and ring is Alpha == 0
                var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                using (SealAsset circleSeal = seals[0])
                using (var canvas = new Bitmap(460, 460, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    renderer.RenderFrame(g, 460, 460, sm, activeSeal: circleSeal, targetProbe: null, monitorDpi: 96.0);
                    // Center star of circular seal must be visible (ghost preserved)
                    Color centerGhost = canvas.GetPixel(230, 230);
                    Assert(centerGhost.A > 60 && centerGhost.R > 150,
                        $"Seal ghost at center must be preserved (got RGBA=({centerGhost.R},{centerGhost.G},{centerGhost.B},{centerGhost.A})).");

                    // Gap between seal outer edge (inkW * 0.5) and ring inner edge (innerR) must still have Alpha == 0
                    int gapX = 230 + (int)Math.Round(inkW * 0.5f + 7f);
                    Color gapColor = canvas.GetPixel(gapX, 230);
                    Assert(gapColor.A == 0, $"Clear margin between seal ghost and inner ring must have Alpha == 0 (got A={gapColor.A}).");
                }
                for (int i = 1; i < seals.Count; i++) seals[i].Dispose();
            }
        }

        /// <summary>
        /// Regression test for Codex Review #3:
        /// Verifies that if AddPicture succeeds and a subsequent shape property setter throws an exception,
        /// InjectStampShapeAtMapping deletes the partially configured shape and fails closed.
        /// </summary>
        private static void TestWordShapeFailureRollback()
        {
            var adapter = new WordStampHostAdapter();
            var seals = SealImageProcessor.CreateBuiltInSampleSeals();
            using (SealAsset seal = seals[0])
            using (Bitmap imp = SealImageProcessor.CreateImpressionBitmap(seal, 99))
            {
                var mockDoc = new MockWordDocumentWithFaultingShape();
                var mapping = CoordinateTransformer.MapScreenToPagePoints(
                    new Vector2D(500, 500),
                    new Vector2D(400, 400),
                    72.0, 72.0, 595.3, 841.9, 96.0, 100.0, 40.0);

                var req = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0
                };

                StampPlacementResult res = adapter.InjectStampShapeAtMapping(mockDoc, null, mapping, req);
                Assert(!res.Success && res.FailedClosed, "InjectStampShapeAtMapping must return FailedClosed when shape configuration throws.");
                Assert(mockDoc.Shapes.CreatedShape != null && mockDoc.Shapes.CreatedShape.WasDeleted,
                    "Partially created Word Shape must be rolled back via Delete() when configuration fails.");
            }
            for (int i = 1; i < seals.Count; i++) seals[i].Dispose();
        }

        public sealed class MockFaultingWordShape
        {
            public bool WasDeleted { get; private set; }
            public MockWrapFormat WrapFormat { get; } = new MockWrapFormat();
            public int RelativeHorizontalPosition { get; set; }
            public int RelativeVerticalPosition
            {
                get => 1;
                set => throw new InvalidOperationException("Simulated Word COM failure setting RelativeVerticalPosition.");
            }
            public void Delete() => WasDeleted = true;
        }

        public sealed class MockWrapFormat
        {
            public int Type { get; set; }
            public int AllowOverlap { get; set; }
        }

        public sealed class MockWordShapesCollection
        {
            public MockFaultingWordShape CreatedShape { get; private set; }
            public object AddPicture(string fileName, object link, object save, object left, object top, object width, object height, object anchor)
            {
                CreatedShape = new MockFaultingWordShape();
                return CreatedShape;
            }
        }

        public sealed class MockWordDocumentWithFaultingShape
        {
            public MockWordShapesCollection Shapes { get; } = new MockWordShapesCollection();
        }

        private static void TestHostStubsFailClosed()
        {
            var pdf = new PdfStampHostAdapter();
            var excel = new ExcelStampHostAdapter();
            var screenshot = new ScreenshotStampHostAdapter();

            Assert(!pdf.IsLiveIntegrationSupported, "PdfStampHostAdapter must report IsLiveIntegrationSupported = false.");
            Assert(!excel.IsLiveIntegrationSupported, "ExcelStampHostAdapter must report IsLiveIntegrationSupported = false.");
            Assert(!screenshot.IsLiveIntegrationSupported, "ScreenshotStampHostAdapter must report IsLiveIntegrationSupported = false.");

            var dummyReq = new StampRequest { ScreenPointPx = new Vector2D(100, 100), PhysicalSizeMm = 40.0 };

            Assert(!pdf.TryApplyStamp(dummyReq, IntPtr.Zero).Success, "PDF stub must fail closed.");
            Assert(!excel.TryApplyStamp(dummyReq, IntPtr.Zero).Success, "Excel stub must fail closed.");
            Assert(!screenshot.TryApplyStamp(dummyReq, IntPtr.Zero).Success, "Screenshot stub must fail closed.");
            Assert(!screenshot.TryStampScreenshotRegion(new Rectangle(0, 0, 100, 100), dummyReq).Success, "Screenshot region stub must fail closed.");
        }

        private static void TestAudioSynthesizer()
        {
            byte[] wav = StampAudioSynthesizer.GetImpactWavBytes();
            Assert(wav != null && wav.Length > 44, "Synthesized impact WAV must contain valid header and PCM samples.");
            Assert(wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F', "WAV must start with RIFF header.");
            Assert(wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E', "WAV must contain WAVE signature.");
        }

        /// <summary>
        /// Regression test for Codex Review #7 & #8:
        /// Verifies that StampOverlayForm dynamically toggles WS_EX_TRANSPARENT on its native HWND
        /// across Idle -> DraggingPickup -> Aiming -> Stamping -> Returning -> Idle, and verifies GlobalInputHook install/unhook.
        /// </summary>
        private static void TestLayeredWindowAndGlobalHookLifecycle()
        {
            using (var hook = new GlobalInputHook())
            {
                Assert(hook.Install(), "GlobalInputHook.Install() must succeed and return true.");
                Assert(hook.IsInstalled, "GlobalInputHook should install both WH_MOUSE_LL and WH_KEYBOARD_LL.");
                hook.Uninstall();
                Assert(!hook.IsInstalled, "GlobalInputHook must cleanly unhook on Uninstall().");
            }

            using (var form = new StampOverlayForm())
            {
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                Assert(form.IsHandleCreated, "StampOverlayForm handle must be created.");
                Assert(form.StateMachine.CurrentState == StampState.Idle, "Overlay starts in Idle.");
                Assert(!form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must be OFF in Idle so parked stamp is interactive.");

                // Enter DraggingPickup -> WS_EX_TRANSPARENT stays OFF
                form.StateMachine.BeginPickupDrag(new Vector2D(500, 500));
                form.SyncPassThroughWindowStyle();
                Assert(!form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must be OFF during DraggingPickup.");

                // Complete pickup to Aiming -> WS_EX_TRANSPARENT must be ON for cross-process wheel/mouse pass-through
                form.StateMachine.CompletePickupToAiming(new Vector2D(500, 500));
                form.SyncPassThroughWindowStyle();
                Assert(form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must be ON in Aiming mode.");

                // Trigger Stamping -> WS_EX_TRANSPARENT remains ON
                form.StateMachine.TryTriggerStamp(new Vector2D(500, 500));
                form.SyncPassThroughWindowStyle();
                Assert(form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must remain ON in Stamping mode.");

                // Return to parked -> WS_EX_TRANSPARENT remains ON during Returning flight, then turns OFF on reaching Idle
                form.StateMachine.BeginReturnToParked();
                form.SyncPassThroughWindowStyle();
                Assert(form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must remain ON during Returning flight.");

                form.StateMachine.Update(0.20);
                form.StateMachine.Update(0.20);
                form.SyncPassThroughWindowStyle();
                Assert(form.StateMachine.CurrentState == StampState.Idle, "Should be back in Idle.");
                Assert(!form.IsCrossProcessPassThroughActive, "WS_EX_TRANSPARENT must be restored to OFF when back in Idle.");

                // Verify StampOverlayForm blocks stamping when preview extent exceeds 2400px
                form.StateMachine.SetPhysicalSizeMm(75.0);
                Assert(!form.CanCommitStampAtExtent(192.0, 400.0, out string rejectMsg) && !string.IsNullOrWhiteSpace(rejectMsg),
                    "StampOverlayForm.CanCommitStampAtExtent must reject stamping when preview extent exceeds 2400px.");

                form.Close();
            }
        }

        private static void TestDisposableWordComIntegration()
        {
            Type wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                Console.WriteLine("       (Word ProgID not registered on host; skipping COM smoke test)");
                return;
            }

            dynamic wordApp = null;
            dynamic doc1 = null;
            dynamic doc2 = null;

            try
            {
                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = true;
                wordApp.DisplayAlerts = 0; // wdAlertsNone

                doc1 = wordApp.Documents.Add();
                dynamic win1 = doc1.ActiveWindow;
                win1.View.Type = 3; // wdPrintView
                win1.View.Zoom.Percentage = 100;

                dynamic sel = wordApp.Selection;
                sel.TypeText("DISPOSABLE SMOKE TEST DOCUMENT 1 - PAGE 1 (SAMPLE ONLY)\r\n");
                sel.TypeText("This temporary document verifies floating In-Front-Of-Text seal placement.\r\n");
                sel.InsertBreak(7); // wdPageBreak = 7
                sel.TypeText("DISPOSABLE SMOKE TEST DOCUMENT 1 - PAGE 2 (MULTI-PAGE SCROLL/ZOOM VERIFICATION)\r\n");

                // Open a second disposable document (doc2) which becomes ActiveWindow!
                // Regression test for Codex Review #2: Verify FindMatchingWindowInApplication binds strictly to win1's HWND
                // (never to the unrelated ActiveWindow doc2) and returns null for an unknown HWND!
                doc2 = wordApp.Documents.Add();
                dynamic win2 = doc2.ActiveWindow;
                int win1HwndInt = Convert.ToInt32(win1.Hwnd);
                int win2HwndInt = Convert.ToInt32(win2.Hwnd);
                var win1RootHwnd = new IntPtr(win1HwndInt);
                var win2RootHwnd = new IntPtr(win2HwndInt);

                Assert(win1RootHwnd != IntPtr.Zero && win2RootHwnd != IntPtr.Zero && win1RootHwnd != win2RootHwnd,
                    "Two distinct disposable Word document windows must have distinct HWNDs.");

                dynamic resolvedWin1 = WordStampHostAdapter.FindMatchingWindowInApplication(wordApp, win1RootHwnd, win1RootHwnd);
                Assert(resolvedWin1 != null && Convert.ToInt32(resolvedWin1.Hwnd) == win1HwndInt,
                    "FindMatchingWindowInApplication must bind to hovered Document 1 even when Document 2 is ActiveWindow.");

                dynamic unmatchedWin = WordStampHostAdapter.FindMatchingWindowInApplication(wordApp, new IntPtr(0x7DEAD), new IntPtr(0x7DEAD));
                Assert(unmatchedWin == null,
                    "FindMatchingWindowInApplication must return null (fail closed) when target HWND does not match any Word window.");

                var adapter = new WordStampHostAdapter();
                var seals = SealImageProcessor.CreateBuiltInSampleSeals();

                using (SealAsset circleSeal40mm = seals[0])
                using (SealAsset ovalSeal40mm = seals[1])
                using (SealAsset squareSeal20mm = seals[2])
                using (Bitmap imp40 = SealImageProcessor.CreateImpressionBitmap(circleSeal40mm, 501))
                using (Bitmap impOval = SealImageProcessor.CreateImpressionBitmap(ovalSeal40mm, 502))
                {
                    // 1. Place 40mm circular sample seal on Document 1, Page 1
                    dynamic page1Range = doc1.Paragraphs[1].Range;
                    double pageW = Convert.ToDouble(page1Range.PageSetup.PageWidth);
                    double pageH = Convert.ToDouble(page1Range.PageSetup.PageHeight);

                    var mapPage1 = CoordinateTransformer.MapScreenToPagePoints(
                        targetScreenPx: new Vector2D(520 + 220 * (96.0 / 72.0), 300 + 260 * (96.0 / 72.0)),
                        anchorScreenPx: new Vector2D(520 + 72 * (96.0 / 72.0), 300 + 72 * (96.0 / 72.0)),
                        anchorPageLeftPt: 72.0,
                        anchorPageTopPt: 72.0,
                        pageWidthPt: pageW,
                        pageHeightPt: pageH,
                        monitorDpi: 96.0,
                        zoomPercent: 100.0,
                        physicalInkWidthMm: 40.0,
                        aspectRatio: circleSeal40mm.InkAspectRatio,
                        inkToCanvasWidthRatio: circleSeal40mm.InkToCanvasWidthRatio,
                        pageNumber: 1,
                        inkToCanvasHeightRatio: circleSeal40mm.InkToCanvasHeightRatio);

                    var req1 = new StampRequest
                    {
                        Seal = circleSeal40mm,
                        ImpressionBitmap = imp40,
                        PhysicalSizeMm = 40.0,
                        BaseAngleDeg = 12.0,
                        JitterAngleDeg = -1.75,
                        ImpressionSeed = 501
                    };

                    StampPlacementResult res1 = adapter.InjectStampShapeAtMapping(doc1, page1Range, mapPage1, req1);
                    Assert(res1.Success, $"Page 1 Word COM stamp placement failed: {res1.Message}");

                    // 2. Place non-square 40mm oval seal on Document 1, Page 2 at 150% zoom using live Window.GetPoint + RangeFromPoint
                    win1.View.Zoom.Percentage = 150;
                    dynamic page2Range = doc1.Paragraphs[doc1.Paragraphs.Count].Range;
                    page2Range.Collapse(1);
                    win1.ScrollIntoView(page2Range, true);

                    int anchorScreenX = 0, anchorScreenY = 0, anchorScreenW = 0, anchorScreenH = 0;
                    win1.GetPoint(out anchorScreenX, out anchorScreenY, out anchorScreenW, out anchorScreenH, page2Range);

                    var liveScreenTarget = new Vector2D(anchorScreenX + 75, anchorScreenY + 45);
                    var req2 = new StampRequest
                    {
                        ScreenPointPx = liveScreenTarget,
                        Seal = ovalSeal40mm,
                        ImpressionBitmap = impOval,
                        PhysicalSizeMm = 40.0,
                        BaseAngleDeg = -5.0,
                        JitterAngleDeg = 2.25,
                        ImpressionSeed = 502
                    };

                    StampPlacementResult res2 = adapter.ApplyStampToWordWindow(win1, req2, monitorDpi: 96.0);
                    Assert(res2.Success, $"Live Page 2 Word.Window RangeFromPoint+GetPoint stamping failed: {res2.Message}");
                    Assert(res2.PageNumber == 2, $"Expected stamp #2 on Page 2, got Page {res2.PageNumber}.");

                    // Verify Document 2 (the unrelated ActiveWindow) remained completely untouched!
                    Assert(Convert.ToInt32(doc2.Shapes.Count) == 0,
                        "Unrelated Document 2 (ActiveWindow) must have 0 shapes injected.");

                    // 3. Verify both injected Shapes in Document 1 directly via Word COM
                    int shapeCount = Convert.ToInt32(doc1.Shapes.Count);
                    Assert(shapeCount == 2, $"Expected 2 floating shapes in disposable Word doc1, found {shapeCount}.");

                    dynamic shape1 = doc1.Shapes[1];
                    Assert(Convert.ToInt32(shape1.WrapFormat.Type) == 5, "Shape 1 must use wdWrapBehind (5).");
                    Assert(Convert.ToInt32(shape1.RelativeHorizontalPosition) == 1 &&
                           Convert.ToInt32(shape1.RelativeVerticalPosition) == 1,
                        "Shape 1 must use page-relative horizontal and vertical positioning.");

                    double actualW1 = Convert.ToDouble(shape1.Width);
                    double actualH1 = Convert.ToDouble(shape1.Height);
                    AssertNear(actualW1 * circleSeal40mm.InkToCanvasWidthRatio, CoordinateTransformer.MillimetersToPoints(40.0), 0.15,
                        "Shape 1 physical 40mm ink width in Word points");
                    AssertNear(actualH1 * circleSeal40mm.InkToCanvasHeightRatio, CoordinateTransformer.MillimetersToPoints(40.0) * circleSeal40mm.InkAspectRatio, 0.15,
                        "Shape 1 physical ink height in Word points");

                    dynamic shape2 = doc1.Shapes[2];
                    double actualW2 = Convert.ToDouble(shape2.Width);
                    double actualH2 = Convert.ToDouble(shape2.Height);
                    AssertNear(actualW2 * ovalSeal40mm.InkToCanvasWidthRatio, CoordinateTransformer.MillimetersToPoints(40.0), 0.15,
                        "Shape 2 (non-square oval) physical 40mm ink width in Word points");
                    AssertNear(actualH2 * ovalSeal40mm.InkToCanvasHeightRatio, CoordinateTransformer.MillimetersToPoints(40.0) * ovalSeal40mm.InkAspectRatio, 0.15,
                        "Shape 2 (non-square oval) physical ink height in Word points (no vertical distortion)");
                }
            }
            finally
            {
                WordStampHostAdapter.ClearThreadLocalCache();
                if (doc2 != null)
                {
                    try { doc2.Close(0); } catch { }
                    try { Marshal.FinalReleaseComObject(doc2); } catch { }
                }
                if (doc1 != null)
                {
                    try { doc1.Close(0); } catch { }
                    try { Marshal.FinalReleaseComObject(doc1); } catch { }
                }
                if (wordApp != null)
                {
                    try { wordApp.Quit(0); } catch { }
                    try { Marshal.FinalReleaseComObject(wordApp); } catch { }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static void TestAsyncHostWorkerCoalescingAndEpochSafety()
        {
            int slowProbeCalls = 0;
            int stampCalls = 0;

            var slowMockAdapter = new SlowMockWordHostAdapter(
                probeDelayMs: 65,
                stampDelayMs: 25,
                onProbe: () => Interlocked.Increment(ref slowProbeCalls),
                onStamp: () => Interlocked.Increment(ref stampCalls));

            var router = new CompositeStampHostRouter(new IStampHostAdapter[] { slowMockAdapter });
            using (var worker = new AsyncStampHostWorker(router))
            {
                var sw = Stopwatch.StartNew();
                // Fire 25 rapid probe requests on the UI thread (simulating 120Hz mouse movement while Word COM takes 65ms)
                for (int i = 0; i < 25; i++)
                {
                    worker.RequestProbe(new Vector2D(300 + i * 5, 400 + i * 3), IntPtr.Zero);
                }
                sw.Stop();

                Assert(sw.Elapsed.TotalMilliseconds < 8.0,
                    $"25 non-blocking RequestProbe calls must complete in < 8ms on the UI thread (took {sw.Elapsed.TotalMilliseconds:F2}ms).");

                Thread.Sleep(240);
                Assert(worker.TotalProbeRequests == 25, $"Expected 25 total probe requests, got {worker.TotalProbeRequests}.");
                Assert(worker.ExecutedProbes >= 1 && worker.ExecutedProbes <= 4,
                    $"Expected coalescing to execute <= 4 probes for a 25-request burst, got {worker.ExecutedProbes}.");
                Assert(worker.CoalescedProbeRequests >= 21,
                    $"Expected >= 21 coalesced intermediate probes, got {worker.CoalescedProbeRequests}.");

                AsyncHostProbeSnapshot latest = worker.GetLatestProbeSnapshot();
                Assert(latest != null && Math.Abs(latest.ScreenPointPx.X - (300 + 24 * 5)) < 0.1,
                    "Latest probe snapshot must reflect the final coalesced coordinate.");

                // Verify ViewEpoch scroll guard aborts an in-flight stamp if user scrolls before placement
                var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                using (SealAsset seal = seals[0])
                {
                    // Occupy worker with a probe, queue a stamp, and immediately advance ViewEpoch (simulating user wheel scroll)
                    worker.RequestProbe(new Vector2D(500, 500), IntPtr.Zero);
                    worker.RequestStamp(new Vector2D(520, 520), IntPtr.Zero, seal, 40.0, 0.0, 1.5, 901);
                    worker.AdvanceViewEpoch();

                    Thread.Sleep(200);
                    Assert(worker.TryConsumeStampCompletion(out AsyncStampCompletion abortedCompletion),
                        "Expected stamp completion record after ViewEpoch scroll abort.");
                    Assert(abortedCompletion.WasAbortedByEpoch && !abortedCompletion.Result.Success,
                        "Stamp queued before a document scroll (ViewEpoch change) must safely abort without placing at stale coordinates.");
                    Assert(stampCalls == 0, "Underlying adapter TryApplyStamp must not be called when ViewEpoch changed before execution.");

                    // Verify bounded FIFO queue for fast continuous repeated stamps (never silently overwriting earlier stamps)
                    var recordedStampXs = new List<double>();
                    for (int i = 0; i < 4; i++)
                    {
                        worker.RequestStamp(new Vector2D(600 + i * 25, 500), IntPtr.Zero, seal, 40.0, 0.0, 1.0, 910 + i);
                    }

                    Thread.Sleep(260);
                    while (worker.TryConsumeStampCompletion(out AsyncStampCompletion fifoComp))
                    {
                        Assert(fifoComp.Result != null && fifoComp.Result.Success,
                            $"Rapid FIFO stamp completion failed: {fifoComp.Result?.Message}");
                        recordedStampXs.Add(fifoComp.ScreenPointPx.X);
                    }
                    Assert(recordedStampXs.Count == 4,
                        $"Bounded FIFO stamp queue must execute all 4 rapid stamps without silent overwrite (got {recordedStampXs.Count}).");
                    AssertNear(recordedStampXs[0], 600.0, 0.1, "FIFO stamp #1 X");
                    AssertNear(recordedStampXs[3], 675.0, 0.1, "FIFO stamp #4 X");

                    // Verify queue capacity overflow emits explicit busy feedback instead of dropping or overwriting
                    slowMockAdapter.StampDelayMs = 80;
                    for (int i = 0; i < AsyncStampHostWorker.MaxPendingStampQueueDepth + 3; i++)
                    {
                        worker.RequestStamp(new Vector2D(700 + i * 10, 500), IntPtr.Zero, seal, 40.0, 0.0, 0.0, 950 + i);
                    }
                    Assert(worker.RejectedBusyStamps >= 1,
                        "Exceeding MaxPendingStampQueueDepth must record RejectedBusyStamps and emit explicit busy completion.");
                    worker.AdvanceCancelEpoch(); // clean up remaining queued items quickly
                    Thread.Sleep(150);
                    bool sawBusyMessage = false;
                    while (worker.TryConsumeStampCompletion(out AsyncStampCompletion c))
                    {
                        if (c.WasRejectedBusy && !string.IsNullOrWhiteSpace(c.Result?.Message))
                            sawBusyMessage = true;
                    }
                    Assert(sawBusyMessage, "Busy overflow completion must surface concise Chinese busy feedback.");

                    // Verify raw exception strings never leak to user-facing Result.Message or Probe.StatusSummary
                    slowMockAdapter.ThrowRawException = true;
                    worker.RequestProbe(new Vector2D(400, 400), IntPtr.Zero);
                    worker.RequestStamp(new Vector2D(400, 400), IntPtr.Zero, seal, 40.0, 0.0, 0.0, 999);
                    Thread.Sleep(180);

                    AsyncHostProbeSnapshot faultProbe = worker.GetLatestProbeSnapshot();
                    Assert(faultProbe?.Probe != null &&
                           faultProbe.Probe.StatusSummary.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0 &&
                           faultProbe.Probe.StatusSummary.IndexOf("0x800", StringComparison.OrdinalIgnoreCase) < 0,
                        $"Probe StatusSummary must never leak raw exception text (got '{faultProbe?.Probe?.StatusSummary}').");

                    Assert(worker.TryConsumeStampCompletion(out AsyncStampCompletion faultStamp) && faultStamp?.Result != null,
                        "Expected completion from throwing adapter.");
                    Assert(faultStamp.Result.Message.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0 &&
                           faultStamp.Result.Message.IndexOf("0x800", StringComparison.OrdinalIgnoreCase) < 0,
                        $"Stamp Result.Message must never leak raw exception text (got '{faultStamp.Result.Message}').");
                    Assert(!string.IsNullOrWhiteSpace(worker.LastDiagnosticError),
                        "AsyncStampHostWorker.LastDiagnosticError must retain internal exception diagnostics.");
                }
                for (int i = 1; i < seals.Count; i++) seals[i].Dispose();
            }
        }

        private sealed class SlowMockWordHostAdapter : IStampHostAdapter
        {
            private readonly int _probeDelayMs;
            private readonly Action _onProbe;
            private readonly Action _onStamp;
            public int StampDelayMs { get; set; }
            public bool ThrowRawException { get; set; }

            public SlowMockWordHostAdapter(int probeDelayMs, int stampDelayMs, Action onProbe, Action onStamp)
            {
                _probeDelayMs = probeDelayMs;
                StampDelayMs = stampDelayMs;
                _onProbe = onProbe;
                _onStamp = onStamp;
            }

            public string AdapterName => "SlowMockWordHostAdapter";
            public bool IsLiveIntegrationSupported => true;

            public bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName) => true;

            public StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight)
            {
                _onProbe?.Invoke();
                if (ThrowRawException)
                    throw new COMException("Simulated raw COMException 0x80010001 RPC_E_CALL_REJECTED in ProbeTarget", unchecked((int)0x80010001));

                Thread.Sleep(_probeDelayMs);
                return new StampTargetProbe
                {
                    IsValidTarget = true,
                    AdapterName = AdapterName,
                    MonitorDpi = 96.0,
                    DocumentZoomPercent = 100.0,
                    PageNumber = 1,
                    StatusSummary = "第 1 页"
                };
            }

            public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd)
            {
                _onStamp?.Invoke();
                if (ThrowRawException)
                    throw new COMException("Simulated raw COMException 0x8001010A RPC_E_SERVERCALL_RETRYLATER in TryApplyStamp", unchecked((int)0x8001010A));

                if (StampDelayMs > 0)
                    Thread.Sleep(StampDelayMs);

                if (request != null && request.TryGetInvalidationReason(out string reason))
                {
                    return new StampPlacementResult
                    {
                        Success = false,
                        FailedClosed = true,
                        WasAbortedByEpoch = true,
                        AdapterName = AdapterName,
                        Message = reason
                    };
                }

                return new StampPlacementResult
                {
                    Success = true,
                    AdapterName = AdapterName,
                    PageNumber = 1,
                    Message = "OK"
                };
            }
        }

        private static void TestRendererCachesAndHandoffContinuity()
        {
            using (var repo = new AssetRepository())
            using (var renderer = new StampRenderer(repo))
            {
                var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                using (SealAsset seal1 = seals[0])
                using (SealAsset seal2 = seals[1])
                using (var bmp = new Bitmap(460, 460, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), 77);
                    var probe = new StampTargetProbe
                    {
                        IsValidTarget = true,
                        AdapterName = "Word",
                        MonitorDpi = 96.0,
                        DocumentZoomPercent = 100.0,
                        PageNumber = 1,
                        StatusSummary = "第 1 页"
                    };

                    // 1. Verify zero opacity dropout across the entire pickup handoff (specifically around morph = 0.72)
                    sm.BeginPickupDrag(new Vector2D(300, 300));
                    double[] criticalMorphSteps = { 0.05, 0.35, 0.58, 0.68, 0.715, 0.719, 0.720, 0.721, 0.75, 0.88, 1.00 };
                    PropertyInfo morphProp = typeof(StampStateMachine).GetProperty("MorphProgress");
                    foreach (double m in criticalMorphSteps)
                    {
                        morphProp.SetValue(sm, m);
                        renderer.RenderFrame(g, 460, 460, sm, seal1, probe, 96.0);
                        long alphaSum = ComputeBitmapAlphaSum(bmp);
                        Assert(alphaSum > 50000,
                            $"Frame at morph={m:F3} must remain clearly visible with zero opacity dropout (alphaSum={alphaSum}).");
                    }

                    // 2. Verify high cache hit rate during Aiming frames
                    sm.CompletePickupToAiming(new Vector2D(300, 300));
                    sm.Update(0.35);
                    long hitsBefore = renderer.ReticleCacheHits;
                    for (int i = 0; i < 30; i++)
                    {
                        renderer.RenderFrame(g, 460, 460, sm, seal1, probe, 96.0);
                    }
                    long hitsDelta = renderer.ReticleCacheHits - hitsBefore;
                    Assert(hitsDelta >= 29, $"Expected >= 29 reticle composite cache hits across 30 Aiming frames, got {hitsDelta}.");

                    // 3. Verify automatic cache invalidation when switching active seal or zoom
                    long missesBefore = renderer.ReticleCacheMisses;
                    renderer.RenderFrame(g, 460, 460, sm, seal2, probe, 96.0);
                    Assert(renderer.ReticleCacheMisses > missesBefore,
                        "Switching activeSeal must automatically trigger a new composite cache build.");
                }
            }
        }

        private static void TestInterruptedMotionsAndReducedMotion()
        {
            var config = new StampConfiguration();
            var parked = new Vector2D(1500, 850);
            var sm = new StampStateMachine(config, parked, 42);

            // 1. Partial pickup release into Aiming must converge smoothly (no 1-frame snap to MorphProgress = 1.0)
            sm.BeginPickupDrag(new Vector2D(1420, 800));
            sm.Update(0.08);
            double morphAtRelease = sm.MorphProgress;
            Assert(morphAtRelease > 0.10 && morphAtRelease < 0.85,
                $"Expected partial morph before release, got {morphAtRelease:F3}.");

            sm.CompletePickupToAiming(new Vector2D(1420, 800));
            AssertNear(sm.MorphProgress, morphAtRelease, 0.001,
                "CompletePickupToAiming must preserve current MorphProgress at release frame without snapping");

            sm.Update(0.03);
            Assert(sm.MorphProgress > morphAtRelease && sm.MorphProgress < 0.999,
                "MorphProgress must smoothly converge toward 1.0 over subsequent frames.");
            for (int i = 0; i < 6; i++) sm.Update(0.08);
            AssertNear(sm.MorphProgress, 1.0, 0.002, "MorphProgress converged to 1.0 in Aiming");

            // 2. Partial pickup cancellation (Right-Click / Esc before reaching Aiming) must not jump MorphProgress up to 1.0
            sm.BeginReturnToParked();
            for (int i = 0; i < 6; i++) sm.Update(0.08);
            Assert(sm.CurrentState == StampState.Idle, "Returned to Idle.");

            sm.BeginPickupDrag(new Vector2D(1350, 720));
            sm.Update(0.07);
            double partialCancelMorph = sm.MorphProgress;
            Assert(partialCancelMorph > 0.10 && partialCancelMorph < 0.75,
                $"Expected partial pickup morph in (0.10, 0.75), got {partialCancelMorph:F3}.");

            Vector2D posAtCancel = sm.CurrentPosition;
            sm.BeginReturnToParked();
            sm.Update(0.016);
            Assert(sm.MorphProgress <= partialCancelMorph + 1e-4,
                $"Cancelling partial pickup must decrease MorphProgress monotonically from {partialCancelMorph:F3}, got {sm.MorphProgress:F3}.");
            Assert(Vector2D.Distance(sm.CurrentPosition, posAtCancel) > 1.5,
                "Ease-out return arc must move immediately on the first 16ms frame after cancellation.");

            // 3. Mid-return re-pickup must preserve current position without snapping back to ParkedPosition
            Vector2D midReturnPos = sm.CurrentPosition;
            Assert(sm.BeginPickupDrag(midReturnPos), "BeginPickupDrag must succeed mid-return.");
            AssertNear(Vector2D.Distance(sm.CurrentPosition, midReturnPos), 0.0, 0.01,
                "Mid-return re-pickup must preserve current position without teleporting to ParkedPosition");

            // 4. ReducedMotion accessibility mode
            var rmConfig = new StampConfiguration { ReducedMotion = true };
            var rmSm = new StampStateMachine(rmConfig, parked, 42);
            rmSm.BeginPickupDrag(new Vector2D(1000, 500));
            rmSm.Update(0.05);
            AssertNear(rmSm.TiltAngleDeg, 0.0, 0.001, "ReducedMotion must keep TiltAngleDeg at 0");
            AssertNear(rmSm.MorphProgress, 1.0, 0.001, "ReducedMotion must transition directly to top-down reticle");
        }

        private static void TestHighResolutionWheelAccumulation()
        {
            int remainder = 0;

            // 8 high-resolution touchpad ticks of +15 must accumulate to exactly 1 notch on the 8th tick
            for (int i = 1; i <= 7; i++)
            {
                int n = GlobalInputHook.AccumulateWheelDeltaToNotches(15, ref remainder);
                Assert(n == 0, $"Sub-notch tick #{i} (+15) must not fire a full notch early (got {n}).");
            }
            int eighth = GlobalInputHook.AccumulateWheelDeltaToNotches(15, ref remainder);
            Assert(eighth == 1 && remainder == 0, $"8th tick of +15 must produce +1 notch and 0 remainder (got {eighth}, rem={remainder}).");

            // Direction reversal must immediately reset remainder
            GlobalInputHook.AccumulateWheelDeltaToNotches(90, ref remainder);
            Assert(remainder == 90, "Remainder should be +90.");
            int rev = GlobalInputHook.AccumulateWheelDeltaToNotches(-30, ref remainder);
            Assert(rev == 0 && remainder == -30, $"Direction reversal must reset positive remainder and accumulate -30 (got rem={remainder}).");

            // Standard 120-unit wheel notch must fire immediately
            remainder = 0;
            Assert(GlobalInputHook.AccumulateWheelDeltaToNotches(120, ref remainder) == 1, "Standard +120 delta must yield +1 notch.");
            Assert(GlobalInputHook.AccumulateWheelDeltaToNotches(-240, ref remainder) == -2, "Fast -240 delta must yield -2 notches.");
        }

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        private static void TestDpiParkingMenuKeyboardAndDibStability()
        {
            // 1. Verify DPI-aware parking stays inside workArea at 100%, 125%, 150%, and 200% DPI
            var workArea = new Rectangle(0, 0, 1920, 1040);
            double[] dpiScales = { 1.0, 1.25, 1.5, 2.0 };
            foreach (double scale in dpiScales)
            {
                Point pt = StampConfiguration.ComputeSafeParkedCenter(workArea, scale);
                float halfBodyH = (StampRenderer.BaseIdleBodyHeightPxAt96Dpi * (float)scale) * 0.5f;
                float halfBodyW = halfBodyH * 0.94f;
                Assert(pt.X - halfBodyW >= workArea.Left && pt.X + halfBodyW <= workArea.Right,
                    $"Parked X at {scale * 100:F0}% DPI must keep body inside horizontal workArea.");
                Assert(pt.Y - halfBodyH >= workArea.Top && pt.Y + halfBodyH <= workArea.Bottom,
                    $"Parked Y at {scale * 100:F0}% DPI must keep body inside vertical workArea.");
            }

            // 2. Verify FrostedContextMenuForm keyboard navigation (Down, Up, Home, End, Enter)
            var seals = SealImageProcessor.CreateBuiltInSampleSeals();
            var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(1500, 850), 1);
            SealAsset selectedViaEnter = null;
            using (var menu = new FrostedContextMenuForm(
                seals,
                seals[0],
                sm,
                s => selectedViaEnter = s,
                () => { },
                _ => { },
                () => { },
                () => { },
                144.0))
            {
                Assert(menu.HandleKeyboardNavigation(Keys.Home), "Home key must select first selectable item.");
                int firstIdx = menu.HighlightedIndex;
                Assert(firstIdx >= 0, "First selectable index must be >= 0.");

                Assert(menu.HandleKeyboardNavigation(Keys.Down), "Down key must advance selection.");
                Assert(menu.HighlightedIndex > firstIdx, "HighlightedIndex must increase on Down key.");

                Assert(menu.HandleKeyboardNavigation(Keys.Up), "Up key must move selection back.");
                Assert(menu.HighlightedIndex == firstIdx, "HighlightedIndex must return to firstIdx on Up key.");

                Assert(menu.HandleKeyboardNavigation(Keys.Enter), "Enter key must invoke highlighted menu action.");
                Assert(selectedViaEnter != null && selectedViaEnter.Id == seals[0].Id,
                    "Enter key on first seal item must invoke onSelectSeal with seals[0].");
            }

            // 3. Verify StampOverlayForm persistent DIBSection stability & zero GDI handle leak across 120 frames
            using (var overlay = new StampOverlayForm())
            {
                overlay.Show();
                Application.DoEvents();
                overlay.InputHook?.Uninstall();

                MethodInfo presentMethod = typeof(StampOverlayForm).GetMethod(
                    "PresentLayeredFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                IntPtr hProc = Process.GetCurrentProcess().Handle;
                uint gdiBefore = GetGuiResources(hProc, 0);

                // Settled idle frames should be skipped without reallocating DIB or leaking GDI handles
                long skippedBefore = overlay.SkippedIdleFrameCount;
                for (int i = 0; i < 40; i++)
                {
                    presentMethod.Invoke(overlay, null);
                }
                Assert(overlay.SkippedIdleFrameCount - skippedBefore >= 39,
                    "Settled Idle PresentLayeredFrame must skip redundant renders and UpdateLayeredWindow calls.");

                // Active moving frames must reuse the persistent DIBSection with zero per-frame reallocations
                int dibCountBeforeMove = overlay.DibReallocationCount;
                overlay.StateMachine.BeginPickupDrag(new Vector2D(800, 500));
                overlay.StateMachine.CompletePickupToAiming(new Vector2D(800, 500));
                for (int i = 0; i < 80; i++)
                {
                    overlay.StateMachine.OnPointerMove(new Vector2D(800 + (i % 15) * 4, 500 + (i % 10) * 4));
                    overlay.StateMachine.Update(1.0 / 120.0);
                    presentMethod.Invoke(overlay, null);
                }

                uint gdiAfter = GetGuiResources(hProc, 0);
                Assert(overlay.DibReallocationCount == dibCountBeforeMove && overlay.DibReallocationCount <= 2,
                    $"Expected zero DIB reallocations across 80 moving frames (before={dibCountBeforeMove}, after={overlay.DibReallocationCount}).");
                Assert(Math.Abs((int)gdiAfter - (int)gdiBefore) <= 4,
                    $"GDI handle count must remain flat across 120 layered presents (before={gdiBefore}, after={gdiAfter}).");

                overlay.Close();
            }
        }

        public sealed class MockTransactionalWordShape
        {
            private readonly Action _onDelete;
            public MockTransactionalWordShape(Action onDelete) => _onDelete = onDelete;
            public bool WasDeleted { get; private set; }
            public MockWrapFormat WrapFormat { get; } = new MockWrapFormat();
            public int RelativeHorizontalPosition { get; set; }
            public int RelativeVerticalPosition { get; set; }
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double Rotation { get; set; }
            public int LockAnchor { get; set; }
            public int LockAspectRatio { get; set; }
            public string Name { get; set; }
            public void ZOrder(int cmd) { }
            public void Delete()
            {
                if (!WasDeleted)
                {
                    WasDeleted = true;
                    _onDelete?.Invoke();
                }
            }
        }

        public sealed class MockTransactionalWordShapesCollection
        {
            public int AddPictureCalls { get; private set; }
            public int ActiveShapeCount { get; private set; }
            public int DeletedShapeCount { get; private set; }
            public MockTransactionalWordShape LastShape { get; private set; }

            public object AddPicture(string fileName, object link, object save, object left, object top, object width, object height, object anchor)
            {
                AddPictureCalls++;
                ActiveShapeCount++;
                LastShape = new MockTransactionalWordShape(() =>
                {
                    ActiveShapeCount--;
                    DeletedShapeCount++;
                });
                return LastShape;
            }
        }

        public sealed class MockTransactionalWordDocument
        {
            private readonly MockTransactionalWordShapesCollection _shapes = new MockTransactionalWordShapesCollection();
            public Action OnShapesGetterAccessed { get; set; }
            public MockTransactionalWordShapesCollection RawShapes => _shapes;
            public MockTransactionalWordShapesCollection Shapes
            {
                get
                {
                    OnShapesGetterAccessed?.Invoke();
                    return _shapes;
                }
            }
        }

        public sealed class MockWordZoom
        {
            public double Percentage { get; set; }
            public MockWordZoom(double pct) { Percentage = pct; }
        }

        public sealed class MockWordView
        {
            public int Type { get; set; }
            public MockWordZoom Zoom { get; set; }
            public MockWordView(int type, double zoomPct)
            {
                Type = type;
                Zoom = new MockWordZoom(zoomPct);
            }
        }

        public sealed class MockTransactionalWordWindow
        {
            public int ViewType { get; set; } = 3; // wdPrintView
            public double ZoomPercentage { get; set; } = 100.0;
            public int CurrentAnchorLeftPx { get; set; } = 400;
            public int CurrentAnchorTopPx { get; set; } = 400;
            public Action OnViewGetterAccessed { get; set; }
            public Action OnGetPointCalled { get; set; }

            public object View
            {
                get
                {
                    OnViewGetterAccessed?.Invoke();
                    return new MockWordView(ViewType, ZoomPercentage);
                }
            }

            public void GetPoint(out int left, out int top, out int width, out int height, object range)
            {
                OnGetPointCalled?.Invoke();
                left = CurrentAnchorLeftPx;
                top = CurrentAnchorTopPx;
                width = 12;
                height = 16;
            }
        }

        private static void TestDeterministicStampGuardAndRollbackOnLateInvalidation()
        {
            var seals = SealImageProcessor.CreateBuiltInSampleSeals();
            using (SealAsset seal = seals[0])
            using (Bitmap imp = SealImageProcessor.CreateImpressionBitmap(seal, 202))
            {
                var adapter = new WordStampHostAdapter();
                var mapping = CoordinateTransformer.MapScreenToPagePoints(
                    new Vector2D(500, 500),
                    new Vector2D(400, 400),
                    72.0, 72.0, 595.3, 841.9, 96.0, 100.0, 40.0);

                // 1A. Deterministic invalidation DURING delayed mapping (OnBeforeMutationHook):
                //     Must abort BEFORE AddPicture is called -> AddPictureCalls == 0, ActiveShapeCount == 0.
                var docPreAbort = new MockTransactionalWordDocument();
                string dynamicInvalidation = null;
                var reqPreAbort = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0,
                    CheckInvalidationReason = () => dynamicInvalidation,
                    OnBeforeMutationHook = () =>
                    {
                        dynamicInvalidation = "模拟映射耗时期间发生滚轮滚动，已取消落章";
                    }
                };

                StampPlacementResult resPreAbort = adapter.InjectStampShapeAtMapping(docPreAbort, null, mapping, reqPreAbort);
                Assert(!resPreAbort.Success && resPreAbort.WasAbortedByEpoch,
                    "InjectStampShapeAtMapping must abort when invalidated right before Shapes.AddPicture.");
                Assert(docPreAbort.RawShapes.AddPictureCalls == 0 && docPreAbort.RawShapes.ActiveShapeCount == 0,
                    $"Pre-mutation guard must prevent Shapes.AddPicture entirely (AddPictureCalls={docPreAbort.RawShapes.AddPictureCalls}, ActiveShapes={docPreAbort.RawShapes.ActiveShapeCount}).");

                // 1B. Deterministic invalidation specifically DURING the blocking wordDocument.Shapes COM getter
                //     (AFTER OnBeforeMutationHook and VerifyTargetAndWordViewBeforeMutation have already passed):
                //     Final managed pre-mutation check right before shapesCollection.AddPicture must abort with 0 shapes added.
                var docShapesGetterAbort = new MockTransactionalWordDocument();
                var winShapesGetterAbort = new MockTransactionalWordWindow();
                string shapesGetterInvalidation = null;
                docShapesGetterAbort.OnShapesGetterAccessed = () =>
                {
                    shapesGetterInvalidation = "模拟读取 wordDocument.Shapes COM 属性阻塞期间用户按下 PageDown/Esc 失效";
                };
                var reqShapesGetterAbort = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0,
                    CheckInvalidationReason = () => shapesGetterInvalidation
                };

                StampPlacementResult resShapesGetterAbort = adapter.InjectStampShapeAtMapping(
                    docShapesGetterAbort,
                    new object(),
                    mapping,
                    reqShapesGetterAbort,
                    winShapesGetterAbort,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    100.0,
                    new Vector2D(400, 400));
                Assert(!resShapesGetterAbort.Success && resShapesGetterAbort.WasAbortedByEpoch && !resShapesGetterAbort.WasRolledBack,
                    "InjectStampShapeAtMapping must abort before AddPicture when invalidated inside wordDocument.Shapes getter.");
                Assert(docShapesGetterAbort.RawShapes.AddPictureCalls == 0 && docShapesGetterAbort.RawShapes.ActiveShapeCount == 0,
                    $"Final pre-AddPicture guard after Shapes getter must prevent AddPicture (AddPictureCalls={docShapesGetterAbort.RawShapes.AddPictureCalls}).");

                // 1C. Deterministic viewport scroll shift (e.g. PageDown / scrollbar) detected by anchor GetPoint verification:
                //     Even if zoom is unchanged, a shifted anchor screen coordinate must abort before AddPicture.
                var docScrollShiftAbort = new MockTransactionalWordDocument();
                var winScrollShiftAbort = new MockTransactionalWordWindow
                {
                    CurrentAnchorLeftPx = 400,
                    CurrentAnchorTopPx = 180 // shifted by 220px from mapped (400, 400) due to PageDown scroll
                };
                var reqScrollShiftAbort = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0
                };
                StampPlacementResult resScrollShiftAbort = adapter.InjectStampShapeAtMapping(
                    docScrollShiftAbort,
                    new object(),
                    mapping,
                    reqScrollShiftAbort,
                    winScrollShiftAbort,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    100.0,
                    new Vector2D(400, 400));
                Assert(!resScrollShiftAbort.Success && resScrollShiftAbort.WasAbortedByEpoch,
                    "VerifyTargetAndWordViewBeforeMutation must abort when anchor screen point shifted due to document scroll.");
                Assert(docScrollShiftAbort.RawShapes.AddPictureCalls == 0,
                    "Anchor scroll shift must abort before AddPicture is called.");

                // 2A. Deterministic invalidation AFTER Shapes.AddPicture during shape property configuration:
                //     Must transactionally call shape.Delete() to roll back -> AddPictureCalls == 1, DeletedShapeCount == 1, ActiveShapeCount == 0.
                var docRollback = new MockTransactionalWordDocument();
                string postInvalidation = null;
                var reqRollback = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0,
                    CheckInvalidationReason = () => postInvalidation,
                    OnDuringConfigurationHook = () =>
                    {
                        postInvalidation = "模拟AddPicture后用户按ESC取消，需回滚删除已插入印章";
                    }
                };

                StampPlacementResult resRollback = adapter.InjectStampShapeAtMapping(docRollback, null, mapping, reqRollback);
                Assert(!resRollback.Success && resRollback.WasAbortedByEpoch && resRollback.WasRolledBack,
                    "InjectStampShapeAtMapping must report WasAbortedByEpoch and WasRolledBack when invalidated after AddPicture.");
                Assert(docRollback.RawShapes.AddPictureCalls == 1 &&
                       docRollback.RawShapes.DeletedShapeCount == 1 &&
                       docRollback.RawShapes.ActiveShapeCount == 0,
                    $"Post-configuration guard must roll back the newly inserted shape via Delete() (Add={docRollback.RawShapes.AddPictureCalls}, Deleted={docRollback.RawShapes.DeletedShapeCount}, Active={docRollback.RawShapes.ActiveShapeCount}).");

                // 2B. Two-phase click stamping: AwaitMutationPermit gates Shapes.AddPicture. All read-only preparation
                //     (image, view verification, Shapes getter) runs first; a pre-impact cancel delivered through the
                //     permit must leave the document untouched, and an allowed permit must proceed to exactly one insert.
                var docPermitAbort = new MockTransactionalWordDocument();
                var winPermitAbort = new MockTransactionalWordWindow();
                bool permitAsked = false;
                var reqPermitAbort = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0,
                    AwaitMutationPermit = () =>
                    {
                        permitAsked = true;
                        return "落章前已取消";
                    }
                };
                StampPlacementResult resPermitAbort = adapter.InjectStampShapeAtMapping(
                    docPermitAbort,
                    new object(),
                    mapping,
                    reqPermitAbort,
                    winPermitAbort,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    100.0,
                    new Vector2D(400, 400));
                Assert(permitAsked && !resPermitAbort.Success && resPermitAbort.WasAbortedByEpoch && !resPermitAbort.WasRolledBack,
                    "A rejected mutation permit must abort before AddPicture without rollback.");
                Assert(docPermitAbort.RawShapes.AddPictureCalls == 0 && docPermitAbort.RawShapes.ActiveShapeCount == 0,
                    $"A rejected mutation permit must prevent AddPicture entirely (AddPictureCalls={docPermitAbort.RawShapes.AddPictureCalls}).");

                var docPermitGranted = new MockTransactionalWordDocument();
                var winPermitGranted = new MockTransactionalWordWindow();
                int addCallsWhenPermitAsked = -1;
                var reqPermitGranted = new StampRequest
                {
                    Seal = seal,
                    ImpressionBitmap = imp,
                    PhysicalSizeMm = 40.0,
                    AwaitMutationPermit = () =>
                    {
                        addCallsWhenPermitAsked = docPermitGranted.RawShapes.AddPictureCalls;
                        return null;
                    }
                };
                StampPlacementResult resPermitGranted = adapter.InjectStampShapeAtMapping(
                    docPermitGranted,
                    new object(),
                    mapping,
                    reqPermitGranted,
                    winPermitGranted,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    100.0,
                    new Vector2D(400, 400));
                Assert(resPermitGranted.Success && addCallsWhenPermitAsked == 0 && docPermitGranted.RawShapes.AddPictureCalls == 1,
                    $"A granted permit must be awaited before AddPicture and then insert once (asked at {addCallsWhenPermitAsked} adds, total {docPermitGranted.RawShapes.AddPictureCalls}, msg {resPermitGranted.Message}, diag {resPermitGranted.Diagnostics}).");

                // 3. Click -> Impact (~60ms plunge) snapshot lock and invalidation before impact:
                //    Verify clicking locks ScreenPointPx at LEFT DOWN even if cursor moves during the 60ms plunge,
                //    and scrolling/cancelling before the 60ms impact point aborts the snapshot. The snapshot is queued
                //    at LEFT DOWN for read-only preparation, but the abort opens its impact gate with a reason, so the
                //    worker never reaches AddPicture.
                using (var overlay = new StampOverlayForm())
                {
                    overlay.Show();
                    Application.DoEvents();
                    overlay.InputHook?.Uninstall();

                    overlay.StateMachine.BeginPickupDrag(new Vector2D(640, 480));
                    overlay.StateMachine.CompletePickupToAiming(new Vector2D(640, 480));
                    overlay.StateMachine.Update(0.25);

                    // Trigger stamp at (640, 480)
                    Assert(overlay.StateMachine.TryTriggerStamp(new Vector2D(640, 480)), "TryTriggerStamp at (640, 480) must succeed.");
                    Assert(overlay.PendingClickStampSnapshot != null, "Click-time snapshot must be captured immediately at LEFT DOWN.");
                    AssertNear(overlay.PendingClickStampSnapshot.ScreenPointPx.X, 640.0, 0.01, "Locked click X at LEFT DOWN");

                    // Move pointer during pre-impact plunge -> StampClickPosition must remain locked at (640, 480)
                    overlay.StateMachine.OnPointerMove(new Vector2D(790, 590));
                    overlay.StateMachine.Update(0.02); // still before 0.30 * 0.21s (~63ms) impact
                    AssertNear(overlay.StateMachine.StampClickPosition.X, 640.0, 0.01, "StampClickPosition must remain locked during pre-impact plunge");

                    // Now simulate user scroll before the 63ms impact commits
                    overlay.InvalidatePendingClickStamp("文档滚动取消落章");
                    overlay.StateMachine.Update(0.08); // crosses the 63ms impact threshold
                    Assert(overlay.PendingClickStampSnapshot == null, "Invalidated pre-impact snapshot must be cleared.");
                    Assert(overlay.AsyncHostWorker.AbortedByEpochStamps >= 1,
                        "Pre-impact invalidation must record an aborted stamp completion without executing Word placement.");

                    // 3B. Keyboard PageDown (VK_NEXT) between accepted LEFT DOWN click and 63ms impact:
                    //     Wait for debounce cooldown, trigger a second click, then simulate VK_NEXT via KeyboardHookCallback.
                    //     Must NOT swallow VK_NEXT (pass through to Word), must advance ViewEpoch, and must cancel the pending click snapshot before impact!
                    for (int i = 0; i < 6; i++) overlay.StateMachine.Update(0.08);
                    Assert(overlay.StateMachine.CurrentState == StampState.Aiming, "Returned to Aiming after rebound/cooldown.");

                    long viewEpochBeforePgDn = overlay.AsyncHostWorker.CurrentViewEpoch;
                    long abortedBeforePgDn = overlay.AsyncHostWorker.AbortedByEpochStamps;
                    Assert(overlay.StateMachine.TryTriggerStamp(new Vector2D(645, 485)), "Second TryTriggerStamp in Aiming must succeed.");
                    Assert(overlay.PendingClickStampSnapshot != null, "Second click snapshot captured at LEFT DOWN.");

                    bool pgDnSwallowed = overlay.InputHook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_NEXT);
                    Assert(!pgDnSwallowed, "PageDown (VK_NEXT) during Stamping plunge must NOT be swallowed by KeyboardHookCallback.");
                    Assert(overlay.AsyncHostWorker.CurrentViewEpoch > viewEpochBeforePgDn,
                        "PageDown (VK_NEXT) must advance ViewEpoch immediately.");
                    Assert(overlay.PendingClickStampSnapshot == null,
                        "PageDown (VK_NEXT) between accepted click and impact must invalidate PendingClickStampSnapshot.");

                    overlay.StateMachine.Update(0.09); // crosses impact point
                    Assert(overlay.AsyncHostWorker.AbortedByEpochStamps > abortedBeforePgDn,
                        "PageDown between click and impact must abort the stamp before enqueueing.");

                    // 4. Stale probe snapshot rejection:
                    //    Advance ViewEpoch and verify an older probe snapshot is rejected by TryAcceptProbeSnapshot.
                    long staleEpoch = overlay.AsyncHostWorker.CurrentViewEpoch;
                    overlay.AsyncHostWorker.AdvanceViewEpoch();
                    var staleSnapshot = new AsyncHostProbeSnapshot(
                        new Vector2D(640, 480),
                        new StampTargetProbe
                        {
                            IsValidTarget = true,
                            AdapterName = "StaleAdapter",
                            MonitorDpi = 96.0,
                            DocumentZoomPercent = 175.0,
                            PageNumber = 9,
                            StatusSummary = "第 9 页"
                        },
                        staleEpoch,
                        overlay.AsyncHostWorker.CurrentCancelEpoch,
                        Stopwatch.GetTimestamp(),
                        Stopwatch.GetTimestamp(),
                        isStaleAtCompletion: false);

                    Assert(!overlay.TryAcceptProbeSnapshot(staleSnapshot),
                        "TryAcceptProbeSnapshot must reject probe snapshots from an older ViewEpoch.");
                    Assert(overlay.RejectedStaleProbeCount >= 1, "RejectedStaleProbeCount must increment when rejecting a stale probe.");
                    Assert(overlay.LatestProbe == null || overlay.LatestProbe.PageNumber != 9,
                        "Stale probe must never overwrite LatestProbe.");

                    overlay.Close();
                }
            }
            for (int i = 1; i < seals.Count; i++) seals[i].Dispose();
        }

        private static void TestLowLevelHookWheelRoutingAndPassThrough()
        {
            const int WM_MOUSEWHEEL = 0x020A;
            const uint LLMHF_INJECTED = 0x00000001;

            bool ctrlDown = false;
            bool altDown = false;
            bool onOuterRing = false;

            int plainScrollEvents = 0;
            int ctrlScaleNotches = 0;
            int altOrRingRotateNotches = 0;

            using (var hook = new GlobalInputHook())
            {
                hook.QueryCurrentState = () => StampState.Aiming;
                hook.IsPointInsideOwnMenu = _ => false;
                hook.IsPointOnOuterReticleRing = _ => onOuterRing;
                hook.QueryKeyState = vk =>
                {
                    if (vk == NativeMethods.VK_CONTROL) return ctrlDown;
                    if (vk == NativeMethods.VK_MENU) return altDown;
                    return false;
                };

                hook.PlainWheelScrolled += () => plainScrollEvents++;
                hook.CtrlWheelScaled += n => ctrlScaleNotches += n;
                hook.AltOrRingWheelRotated += n => altOrRingRotateNotches += n;

                var testPt = new Vector2D(600, 450);

                // 1. Plain wheel in Aiming mode (not on outer ring, no Ctrl/Alt):
                //    Must pass through to CallNextHookEx (swallowed == false) so underlying Word scrolls once,
                //    and fire PlainWheelScrolled once to advance ViewEpoch.
                bool plainSwallowed = hook.SimulateLowLevelMouseMessage(WM_MOUSEWHEEL, testPt, 120, flags: 0);
                Assert(!plainSwallowed,
                    "Plain wheel in Aiming mode must pass through via CallNextHookEx (swallowed=false) so Word scrolls once.");
                Assert(plainScrollEvents == 1 && ctrlScaleNotches == 0 && altOrRingRotateNotches == 0,
                    $"Plain wheel must fire PlainWheelScrolled once and zero scale/rotate (plain={plainScrollEvents}, scale={ctrlScaleNotches}, rot={altOrRingRotateNotches}).");

                // 2. Ctrl + Wheel in Aiming mode:
                //    Must swallow the event (swallowed == true) so Word does NOT zoom, and scale the stamp by +1 notch.
                ctrlDown = true;
                bool ctrlSwallowed = hook.SimulateLowLevelMouseMessage(WM_MOUSEWHEEL, testPt, 120, flags: 0);
                ctrlDown = false;
                Assert(ctrlSwallowed,
                    "Ctrl+Wheel in Aiming mode must be swallowed (return 1) so underlying Word does not zoom.");
                Assert(ctrlScaleNotches == 1 && plainScrollEvents == 1,
                    $"Ctrl+Wheel must scale stamp by +1 notch without firing PlainWheelScrolled (scale={ctrlScaleNotches}, plain={plainScrollEvents}).");

                // 3. Alt + Wheel in Aiming mode:
                //    Must swallow the event (swallowed == true) so Word does NOT scroll, and rotate the stamp by +1 notch.
                altDown = true;
                bool altSwallowed = hook.SimulateLowLevelMouseMessage(WM_MOUSEWHEEL, testPt, 120, flags: 0);
                altDown = false;
                Assert(altSwallowed,
                    "Alt+Wheel in Aiming mode must be swallowed (return 1) so underlying Word does not scroll.");
                Assert(altOrRingRotateNotches == 1 && plainScrollEvents == 1,
                    $"Alt+Wheel must rotate stamp by +1 notch without firing PlainWheelScrolled (rot={altOrRingRotateNotches}).");

                // 4. Outer-ring wheel without modifiers:
                //    Must swallow the event (swallowed == true) and rotate the stamp by -1 notch.
                onOuterRing = true;
                bool ringSwallowed = hook.SimulateLowLevelMouseMessage(WM_MOUSEWHEEL, testPt, -120, flags: 0);
                onOuterRing = false;
                Assert(ringSwallowed,
                    "Outer-ring wheel in Aiming mode must be swallowed (return 1) so underlying Word does not scroll.");
                Assert(altOrRingRotateNotches == 0 && plainScrollEvents == 1,
                    $"Outer-ring -120 wheel must rotate stamp by -1 notch (net rot={altOrRingRotateNotches}).");

                // 5. Injected wheel event (LLMHF_INJECTED):
                //    Must be ignored by the hook and passed through without triggering duplicate scroll/scale/rotate.
                ctrlDown = true;
                bool injectedSwallowed = hook.SimulateLowLevelMouseMessage(WM_MOUSEWHEEL, testPt, 120, flags: LLMHF_INJECTED);
                ctrlDown = false;
                Assert(!injectedSwallowed && ctrlScaleNotches == 1 && plainScrollEvents == 1,
                    "Injected wheel event (LLMHF_INJECTED) must be ignored to prevent synthetic feedback loops.");

                // 6. Keyboard navigation keys (PageUp, PageDown, Home, End, Arrows, Ctrl+Plus) in Aiming mode:
                //    Must NOT be swallowed (pass through to Word) and must fire NavigationKeyPressed, while Esc IS swallowed.
                int navKeyEvents = 0;
                int escEvents = 0;
                hook.NavigationKeyPressed += vk => navKeyEvents++;
                hook.EscapePressed += () => escEvents++;

                Assert(!hook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_NEXT),
                    "VK_NEXT (PageDown) must pass through via CallNextHookEx.");
                Assert(!hook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_PRIOR),
                    "VK_PRIOR (PageUp) must pass through via CallNextHookEx.");
                Assert(!hook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_DOWN),
                    "VK_DOWN must pass through via CallNextHookEx.");
                Assert(navKeyEvents == 3, $"Expected 3 NavigationKeyPressed notifications, got {navKeyEvents}.");

                // Injected keyboard event (LLKHF_INJECTED) must be ignored
                Assert(!hook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_NEXT, NativeMethods.LLKHF_INJECTED),
                    "Injected VK_NEXT must pass through.");
                Assert(navKeyEvents == 3, "LLKHF_INJECTED must not fire NavigationKeyPressed.");

                Assert(hook.SimulateLowLevelKeyboardMessage(NativeMethods.WM_KEYDOWN, NativeMethods.VK_ESCAPE),
                    "VK_ESCAPE in Aiming mode must be swallowed (return 1) and fire EscapePressed.");
                Assert(escEvents == 1, "EscapePressed must fire once.");
            }
        }

        private static long ComputeBitmapAlphaSum(Bitmap bmp)
        {
            long sum = 0;
            for (int y = 0; y < bmp.Height; y += 2)
            {
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    Color c = bmp.GetPixel(x, y);
                    sum += c.A * 31 + c.R;
                }
            }
            return sum;
        }
    }
}
