using System;

namespace DesktopStamp.Core
{
    /// <summary>
    /// Result of transforming a screen pixel coordinate into a Word page coordinate space.
    /// </summary>
    public sealed class PageCoordinateMapping
    {
        public bool IsValid { get; set; }
        public string FailureReason { get; set; }
        public int PageNumber { get; set; }

        /// <summary>
        /// Computed screen pixel origin (top-left corner) of the target page.
        /// </summary>
        public Vector2D PageOriginScreenPx { get; set; }

        /// <summary>
        /// Computed screen pixels per PostScript point (1/72 inch), accounting for monitor DPI and Word zoom.
        /// </summary>
        public double PixelsPerPoint { get; set; }

        /// <summary>
        /// Size of the target page in points.
        /// </summary>
        public double PageWidthPt { get; set; }
        public double PageHeightPt { get; set; }

        /// <summary>
        /// Target stamp center relative to the top-left corner of the page, in points.
        /// </summary>
        public double CenterPageLeftPt { get; set; }
        public double CenterPageTopPt { get; set; }

        /// <summary>
        /// Shape width and height in points (accounting for physical mm and any feather margin ratio).
        /// </summary>
        public double ShapeWidthPt { get; set; }
        public double ShapeHeightPt { get; set; }

        /// <summary>
        /// Center-anchored Shape.Left and Shape.Top in points relative to the page.
        /// </summary>
        public double ShapeLeftPt => CenterPageLeftPt - ShapeWidthPt * 0.5;
        public double ShapeTopPt => CenterPageTopPt - ShapeHeightPt * 0.5;

        /// <summary>
        /// Physical ink width and height in points (excluding feather margin).
        /// </summary>
        public double InkWidthPt { get; set; }
        public double InkHeightPt { get; set; }
    }

    /// <summary>
    /// Pure, deterministic coordinate transformation engine between physical millimeters,
    /// PostScript points (1/72 inch), monitor DPI, Word View Zoom, and page-relative coordinates.
    /// </summary>
    public static class CoordinateTransformer
    {
        public const double MillimetersPerInch = 25.4;
        public const double PointsPerInch = 72.0;
        public const double PointsPerMillimeter = PointsPerInch / MillimetersPerInch; // ~2.834645669291339

        /// <summary>
        /// Converts physical millimeters to PostScript points (1 pt = 1/72 inch).
        /// </summary>
        public static double MillimetersToPoints(double millimeters)
        {
            if (millimeters < 0 || double.IsNaN(millimeters) || double.IsInfinity(millimeters))
                throw new ArgumentOutOfRangeException(nameof(millimeters), "Millimeters must be a non-negative finite number.");
            return millimeters * PointsPerMillimeter;
        }

        /// <summary>
        /// Converts PostScript points to physical millimeters.
        /// </summary>
        public static double PointsToMillimeters(double points)
        {
            if (points < 0 || double.IsNaN(points) || double.IsInfinity(points))
                throw new ArgumentOutOfRangeException(nameof(points), "Points must be a non-negative finite number.");
            return points / PointsPerMillimeter;
        }

        /// <summary>
        /// Computes the screen pixel scale factor (pixels per PostScript point) given monitor DPI and view zoom percentage.
        /// At 96 DPI and 100% zoom, 1 point = 96 / 72 = 1.333333 pixels.
        /// </summary>
        public static double ComputePixelsPerPoint(double monitorDpi, double zoomPercent)
        {
            if (monitorDpi <= 0 || double.IsNaN(monitorDpi))
                monitorDpi = 96.0;
            if (zoomPercent <= 0 || double.IsNaN(zoomPercent))
                zoomPercent = 100.0;

            return (monitorDpi / PointsPerInch) * (zoomPercent / 100.0);
        }

        /// <summary>
        /// Computes the screen pixel diameter of a physical millimeter dimension at the given monitor DPI and view zoom.
        /// </summary>
        public static double MillimetersToScreenPixels(double millimeters, double monitorDpi, double zoomPercent)
        {
            double points = MillimetersToPoints(millimeters);
            double pxPerPt = ComputePixelsPerPoint(monitorDpi, zoomPercent);
            return points * pxPerPt;
        }

        /// <summary>
        /// Ink and whole-bitmap (shape) size in points for a seal printed <paramref name="physicalInkWidthMm"/> wide.
        /// Independent of where it is placed, so the embedded image can be prepared before page mapping finishes.
        /// </summary>
        public static void ComputeShapeSizePoints(
            double physicalInkWidthMm,
            double aspectRatio,
            double inkToCanvasWidthRatio,
            double inkToCanvasHeightRatio,
            out double inkWidthPt,
            out double inkHeightPt,
            out double shapeWidthPt,
            out double shapeHeightPt)
        {
            if (aspectRatio <= 0.02 || double.IsNaN(aspectRatio))
                aspectRatio = 1.0;
            if (inkToCanvasWidthRatio <= 0.05 || inkToCanvasWidthRatio > 1.0 || double.IsNaN(inkToCanvasWidthRatio))
                inkToCanvasWidthRatio = 1.0;
            if (inkToCanvasHeightRatio <= 0.05 || inkToCanvasHeightRatio > 1.0 || double.IsNaN(inkToCanvasHeightRatio))
                inkToCanvasHeightRatio = inkToCanvasWidthRatio;

            inkWidthPt = MillimetersToPoints(physicalInkWidthMm);
            inkHeightPt = inkWidthPt * aspectRatio;

            // Compensate independently for horizontal and vertical feather margins around the tight ink bounding box
            // so that non-square seals preserve their exact canvas aspect ratio and both visible ink width and height
            // match the true physical target dimensions in points/mm.
            shapeWidthPt = inkWidthPt / inkToCanvasWidthRatio;
            shapeHeightPt = inkHeightPt / inkToCanvasHeightRatio;
        }

        /// <summary>
        /// Maps an absolute screen pixel coordinate to exact page-relative coordinates in points.
        /// Uses a verified reference anchor on the same page whose both page-relative coordinates (in points)
        /// and screen coordinates (in pixels, from Word Window.GetPoint) are known, cancelling out character-snap offsets,
        /// scroll offsets, ribbon height, ruler offsets, and multi-page layout offsets.
        /// </summary>
        /// <param name="targetScreenPx">Absolute screen pixel where the reticle center is located.</param>
        /// <param name="anchorScreenPx">Screen pixel top-left of the reference Range on the target page (from Window.GetPoint).</param>
        /// <param name="anchorPageLeftPt">Horizontal position of the reference Range relative to the page in points (wdHorizontalPositionRelativeToPage).</param>
        /// <param name="anchorPageTopPt">Vertical position of the reference Range relative to the page in points (wdVerticalPositionRelativeToPage).</param>
        /// <param name="pageWidthPt">Total width of the page in points.</param>
        /// <param name="pageHeightPt">Total height of the page in points.</param>
        /// <param name="monitorDpi">Per-monitor DPI for the target window.</param>
        /// <param name="zoomPercent">Word active view zoom percentage (e.g. 100).</param>
        /// <param name="physicalInkWidthMm">Requested physical ink width of the seal in millimeters (e.g. 40.0 or 20.0).</param>
        /// <param name="aspectRatio">Height / Width aspect ratio of the tight seal ink imprint (1.0 for circular/square).</param>
        /// <param name="inkToCanvasWidthRatio">Ratio of tight ink width to total bitmap canvas width.</param>
        /// <param name="pageNumber">1-based page number in the document.</param>
        /// <param name="inkToCanvasHeightRatio">Ratio of tight ink height to total bitmap canvas height (defaults to inkToCanvasWidthRatio if omitted/zero).</param>
        public static PageCoordinateMapping MapScreenToPagePoints(
            Vector2D targetScreenPx,
            Vector2D anchorScreenPx,
            double anchorPageLeftPt,
            double anchorPageTopPt,
            double pageWidthPt,
            double pageHeightPt,
            double monitorDpi,
            double zoomPercent,
            double physicalInkWidthMm,
            double aspectRatio = 1.0,
            double inkToCanvasWidthRatio = 1.0,
            int pageNumber = 1,
            double inkToCanvasHeightRatio = 0.0)
        {
            if (pageWidthPt <= 0 || pageHeightPt <= 0)
            {
                return new PageCoordinateMapping
                {
                    IsValid = false,
                    FailureReason = "Invalid page dimensions returned by host."
                };
            }

            if (anchorPageLeftPt < 0 || anchorPageTopPt < 0)
            {
                return new PageCoordinateMapping
                {
                    IsValid = false,
                    FailureReason = "Reference range page-relative position is unavailable (e.g. non-Print Layout view)."
                };
            }

            double pxPerPt = ComputePixelsPerPoint(monitorDpi, zoomPercent);
            if (pxPerPt <= 0.01)
            {
                return new PageCoordinateMapping
                {
                    IsValid = false,
                    FailureReason = "Computed PixelsPerPoint scale factor is out of range."
                };
            }

            // Reconstruct the exact screen pixel coordinate of the page's (0, 0) top-left corner:
            double pageOriginX = anchorScreenPx.X - anchorPageLeftPt * pxPerPt;
            double pageOriginY = anchorScreenPx.Y - anchorPageTopPt * pxPerPt;
            var pageOriginScreenPx = new Vector2D(pageOriginX, pageOriginY);

            // Convert the target reticle screen pixel into page points:
            double centerPageLeftPt = (targetScreenPx.X - pageOriginX) / pxPerPt;
            double centerPageTopPt = (targetScreenPx.Y - pageOriginY) / pxPerPt;

            // Fail closed if the cursor is outside the physical page bounds (e.g. in the gray workspace around the page):
            if (centerPageLeftPt < 0.0 || centerPageLeftPt > pageWidthPt ||
                centerPageTopPt < 0.0 || centerPageTopPt > pageHeightPt)
            {
                return new PageCoordinateMapping
                {
                    IsValid = false,
                    PageNumber = pageNumber,
                    PageOriginScreenPx = pageOriginScreenPx,
                    PixelsPerPoint = pxPerPt,
                    CenterPageLeftPt = centerPageLeftPt,
                    CenterPageTopPt = centerPageTopPt,
                    FailureReason = $"Target coordinate ({centerPageLeftPt:F1}pt, {centerPageTopPt:F1}pt) is outside physical page bounds ({pageWidthPt:F1}pt x {pageHeightPt:F1}pt)."
                };
            }

            ComputeShapeSizePoints(
                physicalInkWidthMm,
                aspectRatio,
                inkToCanvasWidthRatio,
                inkToCanvasHeightRatio,
                out double inkWidthPt,
                out double inkHeightPt,
                out double shapeWidthPt,
                out double shapeHeightPt);

            return new PageCoordinateMapping
            {
                IsValid = true,
                PageNumber = pageNumber,
                PageOriginScreenPx = pageOriginScreenPx,
                PixelsPerPoint = pxPerPt,
                PageWidthPt = pageWidthPt,
                PageHeightPt = pageHeightPt,
                CenterPageLeftPt = centerPageLeftPt,
                CenterPageTopPt = centerPageTopPt,
                InkWidthPt = inkWidthPt,
                InkHeightPt = inkHeightPt,
                ShapeWidthPt = shapeWidthPt,
                ShapeHeightPt = shapeHeightPt
            };
        }
    }
}
