using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DesktopStamp.Core;
using DesktopStamp.UI;

namespace DesktopStamp.HostAdapters
{
    /// <summary>
    /// Production Microsoft Word host adapter via COM and Win32/MSAA accessibility geometry.
    /// Converts absolute screen coordinates, per-monitor DPI, Word view zoom, and page geometry
    /// into page-relative points, and injects an anchored floating picture (In Front of Text)
    /// without disturbing document text flow or stealing window focus.
    /// Uses strictly thread-local ([ThreadStatic]) COM window binding caches so RCWs are never shared across apartments.
    /// </summary>
    public sealed class WordStampHostAdapter : IStampHostAdapter
    {
        private static readonly Guid IID_IDispatch = new Guid("{00020400-0000-0000-C000-000000000046}");

        // Word WdInformation constants
        private const int wdActiveEndPageNumber = 3;
        private const int wdHorizontalPositionRelativeToPage = 5;
        private const int wdVerticalPositionRelativeToPage = 6;

        // Word WdWrapType & Position constants
        private const int wdWrapBehind = 5;
        private const int wdRelativeHorizontalPositionPage = 1;
        private const int wdRelativeVerticalPositionPage = 1;
        private const int wdCollapseStart = 1;
        private const int wdPrintView = 3;
        private const int wdGoToPage = 1;
        private const int wdGoToAbsolute = 1;

        // Word stores the inserted PNG verbatim. 300 dpi at the printed size is print quality and keeps documents
        // small even though every stamp embeds its own random-texture impression.
        private const double EmbeddedImageDpi = 300.0;

        // Apartment-isolated ([ThreadStatic]) cache of the most recently verified Word.Window for a given _WwG HWND
        [ThreadStatic]
        private static IntPtr _tlsCachedWwGHwnd;

        [ThreadStatic]
        private static IntPtr _tlsCachedRootHwnd;

        [ThreadStatic]
        private static object _tlsCachedWordWindow;

        // Apartment-isolated ([ThreadStatic]) cache of page setup dimensions for fast non-stalling probes
        [ThreadStatic]
        private static IntPtr _tlsCachedPageSetupRootHwnd;

        [ThreadStatic]
        private static int _tlsCachedPageSetupPageNumber;

        [ThreadStatic]
        private static double _tlsCachedPageWidthPt;

        [ThreadStatic]
        private static double _tlsCachedPageHeightPt;

        [ThreadStatic]
        private static long _tlsCachedPageSetupTickMs;

        /// <summary>
        /// Page located by the last full (resting-cursor) probe. A stamp click on the same page reuses it after a
        /// single GetPoint confirms the reference character has not moved on screen, skipping the slow
        /// RangeFromPoint/Information mapping (~70-200ms) so the stamp lands while the press animation plays.
        /// </summary>
        private sealed class PageReference
        {
            public IntPtr WwGHwnd;
            public double ZoomPercent;
            public double MonitorDpi;
            public int PageNumber;
            public Vector2D PageOriginScreenPx;
            public double PageWidthPt;
            public double PageHeightPt;
            public object AnchorRange;
            public Vector2D AnchorScreenPx;
        }

        [ThreadStatic]
        private static PageReference _tlsPageReference;

        public string AdapterName => "WordStampHostAdapter";
        public bool IsLiveIntegrationSupported => true;

        public bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName)
        {
            if (string.Equals(rootClassName, "OpusApp", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(windowClassName, "_WwG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(windowClassName, "_WwB", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(processName, "WINWORD", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight)
        {
            var pt = new NativeMethods.POINT((int)Math.Round(screenPointPx.X), (int)Math.Round(screenPointPx.Y));
            double dpi = NativeMethods.QueryDpiForPointOrWindow(pt, targetHwnd);
            string cls = NativeMethods.GetWindowClassName(targetHwnd);
            IntPtr rootHwnd = targetHwnd != IntPtr.Zero ? NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT) : IntPtr.Zero;

            // Must be over the actual Word document canvas (_WwG), not the Ribbon or title bar
            if (!string.Equals(cls, "_WwG", StringComparison.OrdinalIgnoreCase))
            {
                return new StampTargetProbe
                {
                    IsValidTarget = false,
                    AdapterName = AdapterName,
                    TargetHwnd = targetHwnd,
                    RootHwnd = rootHwnd,
                    MonitorDpi = dpi,
                    StatusSummary = "请移到 Word 文档页面"
                };
            }

            try
            {
                dynamic wordWindow = AcquireWordWindowFromHwnd(targetHwnd);
                if (wordWindow == null)
                {
                    return new StampTargetProbe
                    {
                        IsValidTarget = false,
                        AdapterName = AdapterName,
                        TargetHwnd = targetHwnd,
                        RootHwnd = rootHwnd,
                        MonitorDpi = dpi,
                        StatusSummary = "请移到 Word 文档页面"
                    };
                }

                if (!TryReadWindowViewAndZoom(wordWindow, out int viewType, out double zoomPercent))
                {
                    zoomPercent = 100.0;
                    viewType = wdPrintView;
                }

                if (viewType != wdPrintView)
                {
                    return new StampTargetProbe
                    {
                        IsValidTarget = false,
                        AdapterName = AdapterName,
                        TargetHwnd = targetHwnd,
                        RootHwnd = rootHwnd,
                        MonitorDpi = dpi,
                        DocumentZoomPercent = zoomPercent,
                        StatusSummary = "请将 Word 切换为打印布局"
                    };
                }

                if (lightweight)
                {
                    return new StampTargetProbe
                    {
                        IsValidTarget = true,
                        AdapterName = AdapterName,
                        TargetHwnd = targetHwnd,
                        RootHwnd = rootHwnd,
                        MonitorDpi = dpi,
                        DocumentZoomPercent = zoomPercent,
                        StatusSummary = "Word 文档"
                    };
                }

                if (!TryMapScreenPointInWordWindow(
                        wordWindow,
                        targetHwnd,
                        screenPointPx,
                        dpi,
                        zoomPercent,
                        40.0,
                        1.0,
                        1.0,
                        1.0,
                        true,
                        true,
                        true,
                        out PageCoordinateMapping mapping,
                        out dynamic probeAnchorRange,
                        out Vector2D probeAnchorScreenPx,
                        out string errorReason))
                {
                    return new StampTargetProbe
                    {
                        IsValidTarget = false,
                        AdapterName = AdapterName,
                        TargetHwnd = targetHwnd,
                        RootHwnd = rootHwnd,
                        MonitorDpi = dpi,
                        DocumentZoomPercent = zoomPercent,
                        StatusSummary = errorReason
                    };
                }

                StorePageReference(targetHwnd, zoomPercent, dpi, mapping, probeAnchorRange, probeAnchorScreenPx);

                return new StampTargetProbe
                {
                    IsValidTarget = true,
                    AdapterName = AdapterName,
                    TargetHwnd = targetHwnd,
                    RootHwnd = rootHwnd,
                    MonitorDpi = dpi,
                    DocumentZoomPercent = zoomPercent,
                    PageNumber = mapping.PageNumber,
                    PageLeftPt = mapping.CenterPageLeftPt,
                    PageTopPt = mapping.CenterPageTopPt,
                    StatusSummary = $"第 {mapping.PageNumber} 页"
                };
            }
            catch
            {
                InvalidateThreadLocalWindowCache();
                return new StampTargetProbe
                {
                    IsValidTarget = false,
                    AdapterName = AdapterName,
                    TargetHwnd = targetHwnd,
                    RootHwnd = rootHwnd,
                    MonitorDpi = dpi,
                    StatusSummary = "请移到 Word 文档页面"
                };
            }
        }

        public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd)
        {
            if (request == null || request.Seal == null || request.ImpressionBitmap == null)
                return StampPlacementResult.Rejected(AdapterName, "请移到 Word 文档页面");

            if (request.TryGetInvalidationReason(out string earlyInvalidReason))
            {
                return StampPlacementResult.Rejected(AdapterName, earlyInvalidReason, abortedByEpoch: true);
            }

            if (request.ExpectedTargetHwnd != IntPtr.Zero && targetHwnd != request.ExpectedTargetHwnd)
            {
                return StampPlacementResult.Rejected(
                    AdapterName,
                    "目标文档窗口已变化，已取消本次盖章",
                    abortedByEpoch: true);
            }

            IntPtr actualRootHwnd = targetHwnd != IntPtr.Zero
                ? NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT)
                : IntPtr.Zero;

            if (request.ExpectedRootHwnd != IntPtr.Zero && actualRootHwnd != request.ExpectedRootHwnd)
            {
                return StampPlacementResult.Rejected(
                    AdapterName,
                    "目标文档窗口已变化，已取消本次盖章",
                    abortedByEpoch: true);
            }

            string cls = NativeMethods.GetWindowClassName(targetHwnd);
            if (!string.Equals(cls, "_WwG", StringComparison.OrdinalIgnoreCase))
            {
                return StampPlacementResult.Rejected(
                    AdapterName,
                    "请移到 Word 文档页面");
            }

            var pt = new NativeMethods.POINT((int)Math.Round(request.ScreenPointPx.X), (int)Math.Round(request.ScreenPointPx.Y));
            double dpi = NativeMethods.QueryDpiForPointOrWindow(pt, targetHwnd);

            try
            {
                dynamic wordWindow = AcquireWordWindowFromHwnd(targetHwnd);
                if (wordWindow == null)
                {
                    return StampPlacementResult.Rejected(
                        AdapterName,
                        "请移到 Word 文档页面");
                }

                return ApplyStampToWordWindow(wordWindow, request, dpi, targetHwnd, actualRootHwnd);
            }
            catch (Exception ex)
            {
                InvalidateThreadLocalWindowCache();
                StampPlacementResult failed = StampPlacementResult.Rejected(AdapterName, "当前无法连接 Word，请重试");
                failed.Diagnostics = "apply: " + StampDiagnosticsLog.Describe(ex);
                return failed;
            }
        }

        /// <summary>
        /// Core Word COM placement pipeline. Can be invoked either from live screen hit-testing
        /// or from the automated disposable-document smoke test.
        /// </summary>
        public StampPlacementResult ApplyStampToWordWindow(
            dynamic wordWindow,
            StampRequest request,
            double monitorDpi,
            IntPtr targetWwGHwnd = default,
            IntPtr expectedRootHwnd = default)
        {
            if (request == null || request.Seal == null || request.ImpressionBitmap == null)
                return StampPlacementResult.Rejected(AdapterName, "请移到 Word 文档页面");

            if (request.TryGetInvalidationReason(out string preMapInvalidReason))
            {
                return StampPlacementResult.Rejected(AdapterName, preMapInvalidReason, abortedByEpoch: true);
            }

            var timer = new PhaseTimer();
            if (!TryReadWindowViewAndZoom(wordWindow, out int viewType, out double zoomPercent))
            {
                viewType = wdPrintView;
                zoomPercent = 100.0;
            }
            timer.Mark("view");

            if (viewType != wdPrintView)
            {
                return StampPlacementResult.Rejected(
                    AdapterName,
                    "请将 Word 切换为打印布局");
            }

            // Explicitly reject stamping if the preview extent at this DPI and Word zoom exceeds the 2400px canvas cap
            double inkW = CoordinateTransformer.MillimetersToScreenPixels(request.PhysicalSizeMm, monitorDpi, zoomPercent);
            double inkH = inkW * Math.Max(0.02, request.Seal.InkAspectRatio);
            double halfDiag = 0.5 * Math.Sqrt(inkW * inkW + inkH * inkH);
            double innerR = Math.Max(38.0, halfDiag + 14.0);
            double outerR = innerR * 1.2025;
            double dpiScale = Math.Max(72.0, monitorDpi) / 96.0;
            int unclampedCanvasPx = ((int)Math.Ceiling(Math.Max(220.0 * dpiScale, outerR * 1.36 + 64.0 * dpiScale) * 2.0) + 31) & ~31;

            if (unclampedCanvasPx > StampRenderer.MaxSupportedCanvasSizePx)
            {
                return StampPlacementResult.Rejected(
                    AdapterName,
                    "请缩小 Word 视图比例或减小印章尺寸后再盖章");
            }

            IntPtr effectiveWwGHwnd = targetWwGHwnd != IntPtr.Zero ? targetWwGHwnd : request.ExpectedTargetHwnd;
            IntPtr effectiveRootHwnd = expectedRootHwnd != IntPtr.Zero ? expectedRootHwnd : request.ExpectedRootHwnd;

            // Encode the embedded PNG on the thread pool while this thread does the Word round trips;
            // its size depends only on the seal and physical size, not on where it lands
            CoordinateTransformer.ComputeShapeSizePoints(
                request.PhysicalSizeMm,
                request.Seal.InkAspectRatio,
                request.Seal.InkToCanvasWidthRatio,
                request.Seal.InkToCanvasHeightRatio,
                out _,
                out _,
                out double shapeWidthPt,
                out double shapeHeightPt);
            Bitmap impression = request.ImpressionBitmap;
            string pngPath = NewTempImpressionPath();
            var preparedPng = new PreparedImpression(
                Task.Run(() => SaveImpressionForEmbedding(impression, shapeWidthPt, shapeHeightPt, pngPath)),
                pngPath);

            dynamic anchorRange = null;
            dynamic wordDoc = null;
            try
            {
                bool fromCache = TryMapFromPageReference(
                    wordWindow,
                    effectiveWwGHwnd,
                    request,
                    monitorDpi,
                    zoomPercent,
                    out PageCoordinateMapping mapping,
                    out anchorRange,
                    out Vector2D mappedAnchorScreenPx);

                string errorReason = null;
                bool mapped = fromCache || TryMapScreenPointInWordWindow(
                    wordWindow,
                    effectiveWwGHwnd,
                    request.ScreenPointPx,
                    monitorDpi,
                    zoomPercent,
                    request.PhysicalSizeMm,
                    request.Seal.InkAspectRatio,
                    request.Seal.InkToCanvasWidthRatio,
                    request.Seal.InkToCanvasHeightRatio,
                    false,
                    true,
                    true, // view type was just read above
                    out mapping,
                    out anchorRange,
                    out mappedAnchorScreenPx,
                    out errorReason);
                timer.Mark(fromCache ? "map(cached)" : "map");

                if (!mapped)
                {
                    StampPlacementResult unmapped = StampPlacementResult.Rejected(AdapterName, errorReason);
                    unmapped.Diagnostics = $"{timer} | map: {errorReason} at ({request.ScreenPointPx.X:F0},{request.ScreenPointPx.Y:F0})";
                    return unmapped;
                }

                wordDoc = wordWindow.Document;
                return InjectStampShapeAtMapping(
                    wordDoc,
                    anchorRange,
                    mapping,
                    request,
                    wordWindow,
                    effectiveWwGHwnd,
                    effectiveRootHwnd,
                    zoomPercent,
                    mappedAnchorScreenPx,
                    timer,
                    preparedPng);
            }
            finally
            {
                SafeReleaseComObject(anchorRange);
                SafeReleaseComObject(wordDoc);
                preparedPng.WaitAndDelete();
            }
        }

        /// <summary>A PNG being written in the background for embedding; the owner deletes it when done.</summary>
        public sealed class PreparedImpression
        {
            public PreparedImpression(Task ready, string path)
            {
                Ready = ready;
                Path = path;
            }

            public Task Ready { get; }
            public string Path { get; }

            internal void WaitAndDelete()
            {
                try { Ready.Wait(); } catch { }
                try
                {
                    if (File.Exists(Path))
                        File.Delete(Path);
                }
                catch
                {
                    // Ignore temp file cleanup error
                }
            }
        }

        private static string NewTempImpressionPath()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "DesktopStamp_Impressions");
            Directory.CreateDirectory(tempDir);
            return Path.Combine(tempDir, $"seal_{Guid.NewGuid():N}.png");
        }

        /// <summary>
        /// Injects the floating seal Shape into <paramref name="wordDocument"/> at the verified <paramref name="mapping"/>.
        /// Evaluates the managed cancellation/freshness/target/view guard IMMEDIATELY before Shapes.AddPicture
        /// (after all slow COM mapping and PNG file preparation), and rolls back by deleting the newly created
        /// shape if invalidation or failure occurs during post-AddPicture configuration.
        /// </summary>
        public StampPlacementResult InjectStampShapeAtMapping(
            dynamic wordDocument,
            dynamic anchorRange,
            PageCoordinateMapping mapping,
            StampRequest request,
            dynamic wordWindow = null,
            IntPtr targetWwGHwnd = default,
            IntPtr expectedRootHwnd = default,
            double mappedZoomPercent = 0.0,
            Vector2D? mappedAnchorScreenPx = null,
            PhaseTimer timer = null,
            PreparedImpression preparedPng = null)
        {
            timer = timer ?? new PhaseTimer();
            string failureDetail = null;
            StampPlacementResult result = InjectStampShapeCore(
                wordDocument,
                anchorRange,
                mapping,
                request,
                wordWindow,
                targetWwGHwnd,
                expectedRootHwnd,
                mappedZoomPercent,
                mappedAnchorScreenPx,
                timer,
                preparedPng,
                ref failureDetail);
            result.Diagnostics = string.IsNullOrEmpty(failureDetail) ? timer.ToString() : $"{timer} | {failureDetail}";
            return result;
        }

        private StampPlacementResult InjectStampShapeCore(
            dynamic wordDocument,
            dynamic anchorRange,
            PageCoordinateMapping mapping,
            StampRequest request,
            dynamic wordWindow,
            IntPtr targetWwGHwnd,
            IntPtr expectedRootHwnd,
            double mappedZoomPercent,
            Vector2D? mappedAnchorScreenPx,
            PhaseTimer timer,
            PreparedImpression preparedPng,
            ref string failureDetail)
        {
            if (wordDocument == null)
                return StampPlacementResult.Rejected(AdapterName, "请移到 Word 文档页面");
            if (mapping == null || !mapping.IsValid)
                return StampPlacementResult.Rejected(AdapterName, "请移到 Word 文档页面");
            if (request == null || request.ImpressionBitmap == null)
                return StampPlacementResult.Rejected(AdapterName, "请移到 Word 文档页面");

            IntPtr checkWwGHwnd = targetWwGHwnd != IntPtr.Zero ? targetWwGHwnd : request.ExpectedTargetHwnd;
            IntPtr checkRootHwnd = expectedRootHwnd != IntPtr.Zero ? expectedRootHwnd : request.ExpectedRootHwnd;

            // A prepared PNG is owned (and deleted) by the caller; otherwise write and own one here
            bool ownsTempFile = preparedPng == null;
            string tempFile = ownsTempFile ? NewTempImpressionPath() : preparedPng.Path;
            dynamic shape = null;
            dynamic shapesCollection = null;
            dynamic undoRecord = null;
            dynamic application = null;
            bool screenUpdatingSuspended = false;

            try
            {
                // 1. Complete all slow image/file disk preparation BEFORE the pre-mutation guards
                if (ownsTempFile)
                {
                    SaveImpressionForEmbedding(request.ImpressionBitmap, mapping.ShapeWidthPt, mapping.ShapeHeightPt, tempFile);
                    timer.Mark("png");
                }

                // 2. Deterministic hook for testing invalidation after slow mapping + file prep, right before mutation
                request.OnBeforeMutationHook?.Invoke();

                // 3. Managed Pre-Mutation Guard before COM view verification
                if (request.TryGetInvalidationReason(out string preMutationRejectReason))
                {
                    return StampPlacementResult.Rejected(
                        AdapterName,
                        preMutationRejectReason,
                        abortedByEpoch: true);
                }

                // 4. Target / Root HWND & Word View/Zoom/Scroll Identity Guard before AddPicture
                if (!VerifyTargetAndWordViewBeforeMutation(
                        wordWindow,
                        checkWwGHwnd,
                        checkRootHwnd,
                        mappedZoomPercent,
                        anchorRange,
                        mappedAnchorScreenPx,
                        out string viewMismatchReason))
                {
                    return StampPlacementResult.Rejected(
                        AdapterName,
                        viewMismatchReason,
                        abortedByEpoch: true);
                }

                object linkToFile = false;
                object saveWithDocument = true;
                object leftObj = (float)mapping.ShapeLeftPt;
                object topObj = (float)mapping.ShapeTopPt;
                object widthObj = (float)mapping.ShapeWidthPt;
                object heightObj = (float)mapping.ShapeHeightPt;
                object anchorObj = anchorRange ?? Type.Missing;

                shapesCollection = wordDocument.Shapes;
                timer.Mark("verify");

                if (!ownsTempFile)
                {
                    preparedPng.Ready.Wait(); // encoded in parallel with the Word round trips above
                    timer.Mark("png-wait");
                }

                // Two-phase click stamping: everything above is read-only and may run during the press animation;
                // wait here for paper impact before touching the document
                string permitRejectReason = request.AwaitMutationPermit?.Invoke();
                if (permitRejectReason != null)
                {
                    return StampPlacementResult.Rejected(
                        AdapterName,
                        permitRejectReason,
                        abortedByEpoch: true);
                }
                timer.Mark("await-impact");

                // 5. Final Managed Pre-Mutation Guard AFTER all blocking COM reads (VerifyTargetAndWordViewBeforeMutation + wordDocument.Shapes)
                // and IMMEDIATELY before shapesCollection.AddPicture
                if (request.TryGetInvalidationReason(out string finalPreAddRejectReason))
                {
                    return StampPlacementResult.Rejected(
                        AdapterName,
                        finalPreAddRejectReason,
                        abortedByEpoch: true);
                }

                // Group insertion and all formatting into one undo step so a single Ctrl+Z removes the stamp,
                // and suspend repaint so Word lays the page out once instead of after every property set
                try { application = wordDocument.Application; } catch { application = null; }
                undoRecord = TryBeginUndoRecord(application);
                screenUpdatingSuspended = TrySuspendScreenUpdating(application);

                shape = shapesCollection.AddPicture(
                    tempFile,
                    linkToFile,
                    saveWithDocument,
                    leftObj,
                    topObj,
                    widthObj,
                    heightObj,
                    anchorObj);
                timer.Mark("add");

                try
                {
                    // Every property set below is a cross-process call followed by a Word re-layout (~10-20ms each),
                    // and this runs after paper impact, so only the essential ones are made. AddPicture already applied
                    // the exact Width/Height, and new pictures already allow overlap.

                    // WrapFormat = Behind Text (wdWrapBehind = 5): black text stays fully on top of the red ink
                    // and text layout is never disturbed.
                    dynamic wrapFormat = null;
                    try
                    {
                        wrapFormat = shape.WrapFormat;
                        wrapFormat.Type = wdWrapBehind;
                    }
                    finally
                    {
                        SafeReleaseComObject(wrapFormat);
                    }

                    // Configure explicit page-relative positioning BEFORE setting final Left/Top
                    shape.RelativeHorizontalPosition = wdRelativeHorizontalPositionPage;
                    shape.RelativeVerticalPosition = wdRelativeVerticalPositionPage;
                    shape.Left = (float)mapping.ShapeLeftPt;
                    shape.Top = (float)mapping.ShapeTopPt;

                    // Word rotates Shapes around their geometric center (Left + Width/2, Top + Height/2)
                    float effectiveAngle = (float)request.EffectiveAngleDeg;
                    if (Math.Abs(effectiveAngle) > 0.001f)
                        shape.Rotation = effectiveAngle;

                    // The stamp is complete visually: let Word paint it now. The alt-text below is invisible metadata
                    // and still lands in the same undo record.
                    if (screenUpdatingSuspended)
                    {
                        try { application.ScreenUpdating = true; } catch { }
                        screenUpdatingSuspended = false;
                    }
                    timer.Mark("format");

                    try
                    {
                        if (request.Seal != null)
                        {
                            shape.AlternativeText = $"DesktopStamp|{request.Seal.Name}|{request.PhysicalSizeMm:F1}mm|{effectiveAngle:F2}deg|Page{mapping.PageNumber}";
                        }
                    }
                    catch
                    {
                        // Ignore optional metadata assignment failure
                    }

                    timer.Mark("alt-text");

                    // Deterministic hook for testing invalidation during post-AddPicture shape configuration
                    request.OnDuringConfigurationHook?.Invoke();

                    // 6. Post-Configuration Guard: if request was invalidated while AddPicture/configuration was in flight,
                    // transactionally roll back the newly inserted shape so no unwanted stamp remains in the document!
                    if (request.TryGetInvalidationReason(out string postConfigRejectReason))
                    {
                        try { shape.Delete(); } catch { }
                        SafeReleaseComObject(shape);
                        shape = null;
                        return StampPlacementResult.Rejected(
                            AdapterName,
                            postConfigRejectReason,
                            abortedByEpoch: true,
                            rolledBack: true);
                    }

                    // No second COM view verification here: the pre-AddPicture check plus the managed epoch guards
                    // (wheel/keyboard scroll, zoom, cancel) cover the ~100ms configuration window, and the extra
                    // round trips cost ~30ms on every stamp.

                    return new StampPlacementResult
                    {
                        Success = true,
                        FailedClosed = false,
                        AdapterName = AdapterName,
                        Message = $"已盖章 · 第 {mapping.PageNumber} 页",
                        PageNumber = mapping.PageNumber,
                        PlacedCenterLeftPt = mapping.CenterPageLeftPt,
                        PlacedCenterTopPt = mapping.CenterPageTopPt,
                        PlacedShapeLeftPt = mapping.ShapeLeftPt,
                        PlacedShapeTopPt = mapping.ShapeTopPt,
                        PlacedWidthPt = mapping.ShapeWidthPt,
                        PlacedHeightPt = mapping.ShapeHeightPt,
                        PlacedInkWidthPt = mapping.InkWidthPt,
                        PlacedInkHeightPt = mapping.InkHeightPt,
                        PlacedRotationDeg = effectiveAngle
                    };
                }
                catch (Exception ex)
                {
                    failureDetail = "configure: " + StampDiagnosticsLog.Describe(ex);

                    // Roll back only the newly created shape so no partially configured image remains in the document
                    if (shape != null)
                    {
                        try { shape.Delete(); } catch { }
                        SafeReleaseComObject(shape);
                        shape = null;
                    }

                    return StampPlacementResult.Rejected(
                        AdapterName,
                        "请移到 Word 文档页面",
                        rolledBack: true);
                }
            }
            catch (Exception ex)
            {
                failureDetail = "insert: " + StampDiagnosticsLog.Describe(ex);
                if (shape != null)
                {
                    try { shape.Delete(); } catch { }
                    SafeReleaseComObject(shape);
                    shape = null;
                }

                return StampPlacementResult.Rejected(AdapterName, "当前无法在 Word 中插入印章，请重试");
            }
            finally
            {
                if (screenUpdatingSuspended)
                {
                    try { application.ScreenUpdating = true; } catch { }
                }
                EndUndoRecord(undoRecord);
                SafeReleaseComObject(application);
                SafeReleaseComObject(shape);
                SafeReleaseComObject(shapesCollection);
                if (ownsTempFile)
                {
                    try
                    {
                        if (File.Exists(tempFile))
                            File.Delete(tempFile);
                    }
                    catch
                    {
                        // Ignore temp file cleanup error
                    }
                }
            }
        }

        /// <summary>
        /// Writes the impression as PNG, downsampled to <see cref="EmbeddedImageDpi"/> at the printed shape size
        /// (never upsampled). The aspect ratio is unchanged, so ink size and position in the document are unaffected.
        /// </summary>
        private static void SaveImpressionForEmbedding(Bitmap impression, double shapeWidthPt, double shapeHeightPt, string path)
        {
            int targetW = (int)Math.Ceiling(shapeWidthPt / CoordinateTransformer.PointsPerInch * EmbeddedImageDpi);
            int targetH = (int)Math.Ceiling(shapeHeightPt / CoordinateTransformer.PointsPerInch * EmbeddedImageDpi);
            if (targetW < 8 || targetH < 8 || targetW >= impression.Width || targetH >= impression.Height)
            {
                impression.Save(path, ImageFormat.Png);
                return;
            }

            using (var scaled = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                using (var attributes = new ImageAttributes())
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe along the canvas edges
                    g.DrawImage(
                        impression,
                        new Rectangle(0, 0, targetW, targetH),
                        0,
                        0,
                        impression.Width,
                        impression.Height,
                        GraphicsUnit.Pixel,
                        attributes);
                }
                scaled.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>Turns off Word repaint; returns true only if it was on and is now off (so the caller restores it).</summary>
        private static bool TrySuspendScreenUpdating(dynamic application)
        {
            try
            {
                if (!Convert.ToBoolean(application.ScreenUpdating))
                    return false;
                application.ScreenUpdating = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static dynamic TryBeginUndoRecord(dynamic application)
        {
            dynamic undoRecord = null;
            try
            {
                undoRecord = application.UndoRecord;
                if (Convert.ToBoolean(undoRecord.IsRecordingCustomRecord))
                {
                    // Another custom record is open; nesting would merge the stamp into it
                    SafeReleaseComObject(undoRecord);
                    return null;
                }
                undoRecord.StartCustomRecord("盖章");
                return undoRecord;
            }
            catch
            {
                SafeReleaseComObject(undoRecord);
                return null;
            }
        }

        private static void EndUndoRecord(dynamic undoRecord)
        {
            if (undoRecord == null)
                return;
            try { undoRecord.EndCustomRecord(); } catch { }
            SafeReleaseComObject(undoRecord);
        }

        private static bool VerifyTargetAndWordViewBeforeMutation(
            dynamic wordWindow,
            IntPtr expectedWwGHwnd,
            IntPtr expectedRootHwnd,
            double mappedZoomPercent,
            dynamic anchorRange,
            Vector2D? expectedAnchorScreenPx,
            out string rejectionReason)
        {
            rejectionReason = null;

            if (expectedWwGHwnd != IntPtr.Zero)
            {
                if (!NativeMethods.IsWindow(expectedWwGHwnd) || !NativeMethods.IsWindowVisible(expectedWwGHwnd))
                {
                    rejectionReason = "目标文档窗口已变化，已取消本次盖章";
                    return false;
                }

                string currentCls = NativeMethods.GetWindowClassName(expectedWwGHwnd);
                if (!string.Equals(currentCls, "_WwG", StringComparison.OrdinalIgnoreCase))
                {
                    rejectionReason = "目标文档窗口已变化，已取消本次盖章";
                    return false;
                }

                if (expectedRootHwnd != IntPtr.Zero)
                {
                    IntPtr currentRoot = NativeMethods.GetAncestor(expectedWwGHwnd, NativeMethods.GA_ROOT);
                    if (currentRoot != expectedRootHwnd)
                    {
                        rejectionReason = "目标文档窗口已变化，已取消本次盖章";
                        return false;
                    }
                }
            }

            if (wordWindow != null)
            {
                if ((expectedWwGHwnd != IntPtr.Zero || expectedRootHwnd != IntPtr.Zero) &&
                    !DoesWordWindowMatchTargetHwnd(wordWindow, expectedWwGHwnd, expectedRootHwnd))
                {
                    rejectionReason = "目标文档窗口已变化，已取消本次盖章";
                    return false;
                }

                if (TryReadWindowViewAndZoom(wordWindow, out int currentViewType, out double currentZoom))
                {
                    if (currentViewType != wdPrintView)
                    {
                        rejectionReason = "请将 Word 切换为打印布局";
                        return false;
                    }

                    if (mappedZoomPercent > 10.0 && Math.Abs(currentZoom - mappedZoomPercent) > 0.5)
                    {
                        rejectionReason = "文档视图已变化，已取消本次盖章";
                        return false;
                    }
                }

                // Verify anchor screen coordinates have not scrolled away since mapping
                if (anchorRange != null && expectedAnchorScreenPx.HasValue)
                {
                    try
                    {
                        int curAnchorLeft = 0, curAnchorTop = 0, curAnchorW = 0, curAnchorH = 0;
                        wordWindow.GetPoint(
                            out curAnchorLeft,
                            out curAnchorTop,
                            out curAnchorW,
                            out curAnchorH,
                            anchorRange);

                        if (Math.Abs(curAnchorLeft - expectedAnchorScreenPx.Value.X) > 2.0 ||
                            Math.Abs(curAnchorTop - expectedAnchorScreenPx.Value.Y) > 2.0)
                        {
                            rejectionReason = "文档视图已变化，已取消本次盖章";
                            return false;
                        }
                    }
                    catch
                    {
                        rejectionReason = "文档视图已变化，已取消本次盖章";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryReadWindowViewAndZoom(dynamic wordWindow, out int viewType, out double zoomPercent)
        {
            viewType = wdPrintView;
            zoomPercent = 100.0;
            dynamic view = null;
            dynamic zoom = null;
            try
            {
                view = wordWindow.View;
                if (view == null)
                    return false;

                try { viewType = Convert.ToInt32(view.Type); } catch { viewType = wdPrintView; }
                try
                {
                    zoom = view.Zoom;
                    if (zoom != null)
                    {
                        zoomPercent = Convert.ToDouble(zoom.Percentage);
                    }
                }
                catch
                {
                    zoomPercent = 100.0;
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                SafeReleaseComObject(zoom);
                SafeReleaseComObject(view);
            }
        }

        private static bool TryMapScreenPointInWordWindow(
            dynamic wordWindow,
            IntPtr wwgHwnd,
            Vector2D targetScreenPx,
            double monitorDpi,
            double zoomPercent,
            double physicalSizeMm,
            double aspectRatio,
            double inkToCanvasWidthRatio,
            double inkToCanvasHeightRatio,
            bool allowCachedPageDimensions,
            bool keepAnchorRange,
            bool skipViewTypeCheck,
            out PageCoordinateMapping mapping,
            out dynamic resolvedAnchorRange,
            out Vector2D mappedAnchorScreenPx,
            out string errorReason)
        {
            mapping = null;
            resolvedAnchorRange = null;
            mappedAnchorScreenPx = new Vector2D(0, 0);
            errorReason = null;

            dynamic hitObject = null;
            dynamic range = null;

            try
            {
                if (!skipViewTypeCheck)
                {
                    try
                    {
                        int viewType = Convert.ToInt32(wordWindow.View.Type);
                        if (viewType != wdPrintView)
                        {
                            errorReason = "请将 Word 切换为打印布局";
                            return false;
                        }
                    }
                    catch
                    {
                        // Continue if View.Type cannot be read
                    }
                }

                int sx = (int)Math.Round(targetScreenPx.X);
                int sy = (int)Math.Round(targetScreenPx.Y);

                try
                {
                    hitObject = wordWindow.RangeFromPoint(sx, sy);
                }
                catch
                {
                    errorReason = "请移到 Word 文档页面";
                    return false;
                }

                if (hitObject == null)
                {
                    errorReason = "请移到 Word 文档页面";
                    return false;
                }

                // RangeFromPoint returns either a Word.Range or a Word.Shape (if hovering over an existing shape).
                try
                {
                    int _ = Convert.ToInt32(hitObject.Start);
                    range = hitObject.Duplicate;
                }
                catch
                {
                    dynamic anchor = null;
                    try
                    {
                        anchor = hitObject.Anchor;
                        range = anchor.Duplicate;
                    }
                    catch
                    {
                        errorReason = "请移到 Word 文档页面";
                        return false;
                    }
                    finally
                    {
                        SafeReleaseComObject(anchor);
                    }
                }

                range.Collapse(wdCollapseStart);

                IntPtr rootKey = wwgHwnd != IntPtr.Zero ? NativeMethods.GetAncestor(wwgHwnd, NativeMethods.GA_ROOT) : IntPtr.Zero;
                var sizing = new SealSizing(physicalSizeMm, aspectRatio, inkToCanvasWidthRatio, inkToCanvasHeightRatio);

                // RangeFromPoint snaps to the nearest text, which over blank page areas (below the last line,
                // after a page break) is the first character of the NEXT page. Never trust its page number:
                // accept the hit range only if the target pixel lies inside the hit range's own page, otherwise
                // test neighbouring pages using their first character as the reference.
                bool mapped = TryMapAgainstReference(
                    wordWindow, range, rootKey, targetScreenPx, monitorDpi, zoomPercent, sizing,
                    allowCachedPageDimensions, out mapping, out mappedAnchorScreenPx);

                if (!mapped)
                {
                    int hitPage = Convert.ToInt32(range.Information[wdActiveEndPageNumber]);
                    dynamic document = null;
                    try
                    {
                        document = wordWindow.Document;
                        foreach (int offset in NeighbourPageOffsets)
                        {
                            // Pages past the end are rejected by TryGetPageStartRange (GoTo clamps to the last page)
                            int candidatePage = hitPage + offset;
                            if (candidatePage < 1)
                                continue;

                            dynamic pageStart = TryGetPageStartRange(document, candidatePage);
                            if (pageStart == null)
                                continue;

                            if (TryMapAgainstReference(
                                    wordWindow, pageStart, rootKey, targetScreenPx, monitorDpi, zoomPercent, sizing,
                                    allowCachedPageDimensions, out mapping, out mappedAnchorScreenPx))
                            {
                                SafeReleaseComObject(range);
                                range = pageStart;
                                mapped = true;
                                break;
                            }

                            SafeReleaseComObject(pageStart);
                        }
                    }
                    finally
                    {
                        SafeReleaseComObject(document);
                    }
                }

                if (!mapped)
                {
                    errorReason = "请移到 Word 文档页面";
                    return false;
                }

                if (keepAnchorRange)
                {
                    resolvedAnchorRange = range;
                    range = null; // Ownership transferred to caller
                }

                return true;
            }
            catch
            {
                errorReason = "请移到 Word 文档页面";
                return false;
            }
            finally
            {
                SafeReleaseComObject(range);
                SafeReleaseComObject(hitObject);
            }
        }

        private static void StorePageReference(
            IntPtr wwgHwnd,
            double zoomPercent,
            double monitorDpi,
            PageCoordinateMapping mapping,
            object anchorRange,
            Vector2D anchorScreenPx)
        {
            ClearPageReference();
            _tlsPageReference = new PageReference
            {
                WwGHwnd = wwgHwnd,
                ZoomPercent = zoomPercent,
                MonitorDpi = monitorDpi,
                PageNumber = mapping.PageNumber,
                PageOriginScreenPx = mapping.PageOriginScreenPx,
                PageWidthPt = mapping.PageWidthPt,
                PageHeightPt = mapping.PageHeightPt,
                AnchorRange = anchorRange,
                AnchorScreenPx = anchorScreenPx
            };
        }

        private static void ClearPageReference()
        {
            PageReference reference = _tlsPageReference;
            _tlsPageReference = null;
            if (reference != null)
                SafeReleaseComObject(reference.AnchorRange);
        }

        /// <summary>
        /// Maps the stamp point using the page cached by the last full probe, if it is the same window, zoom and DPI,
        /// the reference character is still at the same screen pixel (no scroll/reflow since), and the point lies on
        /// that page. Returns false (caller does the full mapping) in every other case.
        /// </summary>
        private static bool TryMapFromPageReference(
            dynamic wordWindow,
            IntPtr wwgHwnd,
            StampRequest request,
            double monitorDpi,
            double zoomPercent,
            out PageCoordinateMapping mapping,
            out dynamic anchorRange,
            out Vector2D anchorScreenPx)
        {
            mapping = null;
            anchorRange = null;
            anchorScreenPx = new Vector2D(0, 0);

            PageReference reference = _tlsPageReference;
            if (reference == null ||
                reference.WwGHwnd != wwgHwnd ||
                Math.Abs(reference.ZoomPercent - zoomPercent) > 0.01 ||
                Math.Abs(reference.MonitorDpi - monitorDpi) > 0.01)
            {
                return false;
            }

            try
            {
                int left, top, width, height;
                wordWindow.GetPoint(out left, out top, out width, out height, reference.AnchorRange);
                if (Math.Abs(left - reference.AnchorScreenPx.X) > 0.5 || Math.Abs(top - reference.AnchorScreenPx.Y) > 0.5)
                {
                    ClearPageReference();
                    return false;
                }
            }
            catch
            {
                ClearPageReference();
                return false;
            }

            mapping = CoordinateTransformer.MapScreenToPagePoints(
                request.ScreenPointPx,
                reference.PageOriginScreenPx,
                0.0,
                0.0,
                reference.PageWidthPt,
                reference.PageHeightPt,
                monitorDpi,
                zoomPercent,
                request.PhysicalSizeMm,
                request.Seal.InkAspectRatio,
                request.Seal.InkToCanvasWidthRatio,
                reference.PageNumber,
                request.Seal.InkToCanvasHeightRatio);
            if (!mapping.IsValid)
                return false;

            try
            {
                // Caller owns and releases its anchor; the cached range stays alive for the next stamp
                anchorRange = ((dynamic)reference.AnchorRange).Duplicate;
            }
            catch
            {
                ClearPageReference();
                return false;
            }
            anchorScreenPx = reference.AnchorScreenPx;
            return true;
        }

        private sealed class SealSizing
        {
            public readonly double PhysicalSizeMm;
            public readonly double AspectRatio;
            public readonly double InkToCanvasWidthRatio;
            public readonly double InkToCanvasHeightRatio;

            public SealSizing(double physicalSizeMm, double aspectRatio, double inkToCanvasWidthRatio, double inkToCanvasHeightRatio)
            {
                PhysicalSizeMm = physicalSizeMm;
                AspectRatio = aspectRatio;
                InkToCanvasWidthRatio = inkToCanvasWidthRatio;
                InkToCanvasHeightRatio = inkToCanvasHeightRatio;
            }
        }

        // Neighbour pages tried when the hit range's page does not contain the target (nearest first;
        // +/-2 covers side-by-side multi-page layouts at low zoom)
        private static readonly int[] NeighbourPageOffsets = { -1, 1, -2, 2 };

        /// <summary>
        /// Maps <paramref name="targetScreenPx"/> onto the page containing <paramref name="referenceRange"/>, using that
        /// range's page-relative position (Information) and on-screen position (GetPoint) to locate the page origin.
        /// Returns false when the target lies outside that page.
        /// </summary>
        private static bool TryMapAgainstReference(
            dynamic wordWindow,
            dynamic referenceRange,
            IntPtr rootKey,
            Vector2D targetScreenPx,
            double monitorDpi,
            double zoomPercent,
            SealSizing sizing,
            bool allowCachedPageDimensions,
            out PageCoordinateMapping mapping,
            out Vector2D referenceScreenPx)
        {
            mapping = null;
            referenceScreenPx = new Vector2D(0, 0);

            int pageNumber = Convert.ToInt32(referenceRange.Information[wdActiveEndPageNumber]);
            double referencePageLeftPt = Convert.ToDouble(referenceRange.Information[wdHorizontalPositionRelativeToPage]);
            double referencePageTopPt = Convert.ToDouble(referenceRange.Information[wdVerticalPositionRelativeToPage]);

            int screenLeftPx = 0, screenTopPx = 0, screenW = 0, screenH = 0;
            try
            {
                wordWindow.GetPoint(out screenLeftPx, out screenTopPx, out screenW, out screenH, referenceRange);
            }
            catch
            {
                return false;
            }

            ReadPageDimensions(referenceRange, rootKey, pageNumber, allowCachedPageDimensions, out double pageWidthPt, out double pageHeightPt);

            referenceScreenPx = new Vector2D(screenLeftPx, screenTopPx);
            mapping = CoordinateTransformer.MapScreenToPagePoints(
                targetScreenPx,
                referenceScreenPx,
                referencePageLeftPt,
                referencePageTopPt,
                pageWidthPt,
                pageHeightPt,
                monitorDpi,
                zoomPercent,
                sizing.PhysicalSizeMm,
                sizing.AspectRatio,
                sizing.InkToCanvasWidthRatio,
                pageNumber,
                sizing.InkToCanvasHeightRatio);

            return mapping.IsValid;
        }

        private static void ReadPageDimensions(
            dynamic range,
            IntPtr rootKey,
            int pageNumber,
            bool allowCachedPageDimensions,
            out double pageWidthPt,
            out double pageHeightPt)
        {
            long nowTicks = Environment.TickCount & int.MaxValue;
            if (allowCachedPageDimensions &&
                rootKey != IntPtr.Zero &&
                rootKey == _tlsCachedPageSetupRootHwnd &&
                pageNumber == _tlsCachedPageSetupPageNumber &&
                _tlsCachedPageWidthPt > 10.0 &&
                _tlsCachedPageHeightPt > 10.0 &&
                Math.Abs(nowTicks - _tlsCachedPageSetupTickMs) < 2500)
            {
                pageWidthPt = _tlsCachedPageWidthPt;
                pageHeightPt = _tlsCachedPageHeightPt;
                return;
            }

            dynamic pageSetup = null;
            try
            {
                pageSetup = range.PageSetup;
                pageWidthPt = Convert.ToDouble(pageSetup.PageWidth);
                pageHeightPt = Convert.ToDouble(pageSetup.PageHeight);

                if (rootKey != IntPtr.Zero && pageWidthPt > 10.0 && pageHeightPt > 10.0)
                {
                    _tlsCachedPageSetupRootHwnd = rootKey;
                    _tlsCachedPageSetupPageNumber = pageNumber;
                    _tlsCachedPageWidthPt = pageWidthPt;
                    _tlsCachedPageHeightPt = pageHeightPt;
                    _tlsCachedPageSetupTickMs = nowTicks;
                }
            }
            finally
            {
                SafeReleaseComObject(pageSetup);
            }
        }

        /// <summary>
        /// Returns a collapsed range at the first character of <paramref name="pageNumber"/>, or null if Word
        /// resolves the page to a different one (e.g. past the end of the document).
        /// </summary>
        private static dynamic TryGetPageStartRange(dynamic document, int pageNumber)
        {
            dynamic pageStart = null;
            try
            {
                pageStart = document.GoTo(wdGoToPage, wdGoToAbsolute, pageNumber);
                pageStart.Collapse(wdCollapseStart);
                if (Convert.ToInt32(pageStart.Information[wdActiveEndPageNumber]) == pageNumber)
                    return pageStart;
            }
            catch
            {
            }

            SafeReleaseComObject(pageStart);
            return null;
        }

        /// <summary>
        /// Verifies whether a candidate Word.Window COM object corresponds to the hovered target HWND / root OpusApp HWND.
        /// Never activates or changes focus of any window.
        /// </summary>
        public static bool DoesWordWindowMatchTargetHwnd(dynamic candidateWindow, IntPtr targetWwGHwnd, IntPtr expectedRootHwnd)
        {
            if (candidateWindow == null || (targetWwGHwnd == IntPtr.Zero && expectedRootHwnd == IntPtr.Zero))
                return false;

            try
            {
                int winHwndInt = Convert.ToInt32(candidateWindow.Hwnd);
                var winHwnd = new IntPtr(winHwndInt);
                if (winHwnd == IntPtr.Zero)
                    return false;

                if (expectedRootHwnd != IntPtr.Zero && winHwnd == expectedRootHwnd)
                    return true;
                if (targetWwGHwnd != IntPtr.Zero && (winHwnd == targetWwGHwnd || NativeMethods.IsChild(winHwnd, targetWwGHwnd)))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static dynamic FindMatchingWindowInApplication(dynamic wordApp, IntPtr targetWwGHwnd, IntPtr expectedRootHwnd)
        {
            if (wordApp == null)
                return null;

            dynamic windows = null;
            try
            {
                windows = wordApp.Windows;
                int count = Convert.ToInt32(windows.Count);
                for (int i = 1; i <= count; i++)
                {
                    dynamic candidate = windows[i];
                    if (DoesWordWindowMatchTargetHwnd(candidate, targetWwGHwnd, expectedRootHwnd))
                    {
                        return candidate;
                    }
                    SafeReleaseComObject(candidate);
                }
            }
            catch
            {
                // Ignore enumeration error
            }
            finally
            {
                SafeReleaseComObject(windows);
            }

            return null;
        }

        private static dynamic AcquireWordWindowFromHwnd(IntPtr wwgHwnd)
        {
            if (wwgHwnd == IntPtr.Zero || !NativeMethods.IsWindow(wwgHwnd))
            {
                InvalidateThreadLocalWindowCache();
                return null;
            }

            IntPtr expectedRootHwnd = NativeMethods.GetAncestor(wwgHwnd, NativeMethods.GA_ROOT);

            // 0. Fast thread-local cache hit: re-verify that the cached Word.Window still matches the hovered HWND/root HWND
            if (_tlsCachedWordWindow != null &&
                _tlsCachedWwGHwnd == wwgHwnd &&
                _tlsCachedRootHwnd == expectedRootHwnd)
            {
                if (DoesWordWindowMatchTargetHwnd(_tlsCachedWordWindow, wwgHwnd, expectedRootHwnd))
                {
                    return _tlsCachedWordWindow;
                }
                InvalidateThreadLocalWindowCache();
            }

            // 1. Primary path: MSAA AccessibleObjectFromWindow(OBJID_NATIVEOM) directly on the hovered _WwG child window
            try
            {
                Guid iid = IID_IDispatch;
                int hr = NativeMethods.AccessibleObjectFromWindow(
                    wwgHwnd,
                    NativeMethods.OBJID_NATIVEOM,
                    ref iid,
                    out object nativeObj);

                if (hr >= 0 && nativeObj != null)
                {
                    dynamic dynObj = nativeObj;
                    try
                    {
                        var _ = dynObj.View;
                        if (DoesWordWindowMatchTargetHwnd(dynObj, wwgHwnd, expectedRootHwnd))
                        {
                            CacheThreadLocalWindow(wwgHwnd, expectedRootHwnd, dynObj);
                            return dynObj;
                        }
                    }
                    catch
                    {
                        // dynObj may expose .Application
                    }

                    // Search dynObj.Application.Windows for the exact window matching expectedRootHwnd (never blindly use ActiveWindow)
                    dynamic appFromObj = null;
                    try
                    {
                        appFromObj = dynObj.Application;
                        dynamic matchedFromApp = FindMatchingWindowInApplication(appFromObj, wwgHwnd, expectedRootHwnd);
                        if (matchedFromApp != null)
                        {
                            SafeReleaseComObject(dynObj);
                            CacheThreadLocalWindow(wwgHwnd, expectedRootHwnd, matchedFromApp);
                            return matchedFromApp;
                        }
                    }
                    catch
                    {
                        // Fallback below
                    }
                    finally
                    {
                        SafeReleaseComObject(appFromObj);
                    }

                    SafeReleaseComObject(dynObj);
                }
            }
            catch
            {
                // Fallback to ROT search below
            }

            // 2. Fallback path: ROT Word.Application, strictly verifying Window.Hwnd matches expectedRootHwnd
            dynamic rotApp = null;
            try
            {
                rotApp = Marshal.GetActiveObject("Word.Application");
                dynamic matched = FindMatchingWindowInApplication(rotApp, wwgHwnd, expectedRootHwnd);
                if (matched != null)
                {
                    CacheThreadLocalWindow(wwgHwnd, expectedRootHwnd, matched);
                    return matched;
                }
            }
            catch
            {
                // Word is not running or inaccessible
            }
            finally
            {
                SafeReleaseComObject(rotApp);
            }

            // Fail closed if no Word.Window matches the hovered HWND
            return null;
        }

        private static void CacheThreadLocalWindow(IntPtr wwgHwnd, IntPtr rootHwnd, object wordWindow)
        {
            if (!ReferenceEquals(_tlsCachedWordWindow, wordWindow))
            {
                SafeReleaseComObject(_tlsCachedWordWindow);
            }
            _tlsCachedWwGHwnd = wwgHwnd;
            _tlsCachedRootHwnd = rootHwnd;
            _tlsCachedWordWindow = wordWindow;
        }

        public static void ClearThreadLocalCache()
        {
            InvalidateThreadLocalWindowCache();
            _tlsCachedPageSetupRootHwnd = IntPtr.Zero;
            _tlsCachedPageSetupPageNumber = 0;
            _tlsCachedPageWidthPt = 0.0;
            _tlsCachedPageHeightPt = 0.0;
            _tlsCachedPageSetupTickMs = 0;
        }

        private static void InvalidateThreadLocalWindowCache()
        {
            ClearPageReference();
            SafeReleaseComObject(_tlsCachedWordWindow);
            _tlsCachedWordWindow = null;
            _tlsCachedWwGHwnd = IntPtr.Zero;
            _tlsCachedRootHwnd = IntPtr.Zero;
        }

        private static void SafeReleaseComObject(object comObj)
        {
            if (comObj != null && Marshal.IsComObject(comObj))
            {
                try
                {
                    Marshal.ReleaseComObject(comObj);
                }
                catch
                {
                }
            }
        }
    }
}
