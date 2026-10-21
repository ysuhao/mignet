using System;
using System.Drawing;
using DesktopStamp.Core;

namespace DesktopStamp.HostAdapters
{
    /// <summary>
    /// Documented extension point for PDF reader applications (Acrobat, SumatraPDF, Edge PDF).
    /// Explicitly rejects live stamping operations in v1 (fail-closed).
    /// </summary>
    public sealed class PdfStampHostAdapter : IStampHostAdapter
    {
        public string AdapterName => "PdfStampHostAdapter";
        public bool IsLiveIntegrationSupported => false;

        public bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName)
        {
            if (string.Equals(rootClassName, "AcrobatSDIWindow", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootClassName, "SUMATRA_PDF_FRAME", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(processName, "AcroRd32", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "Acrobat", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "SumatraPDF", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight)
        {
            return new StampTargetProbe
            {
                IsValidTarget = false,
                AdapterName = AdapterName,
                TargetHwnd = targetHwnd,
                StatusSummary = "请移到 Word 文档页面"
            };
        }

        public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd)
        {
            return StampPlacementResult.Rejected(
                AdapterName,
                "请移到 Word 文档页面");
        }
    }

    /// <summary>
    /// Documented extension point for Microsoft Excel worksheets.
    /// Explicitly rejects live stamping operations in v1 (fail-closed).
    /// </summary>
    public sealed class ExcelStampHostAdapter : IStampHostAdapter
    {
        public string AdapterName => "ExcelStampHostAdapter";
        public bool IsLiveIntegrationSupported => false;

        public bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName)
        {
            if (string.Equals(rootClassName, "XLMAIN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(windowClassName, "EXCEL7", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "EXCEL", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight)
        {
            return new StampTargetProbe
            {
                IsValidTarget = false,
                AdapterName = AdapterName,
                TargetHwnd = targetHwnd,
                StatusSummary = "请移到 Word 文档页面"
            };
        }

        public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd)
        {
            return StampPlacementResult.Rejected(
                AdapterName,
                "请移到 Word 文档页面");
        }
    }

    /// <summary>
    /// Documented extension point for screen-capture / raster screenshot stamping.
    /// Explicitly rejects live window stamping and screenshot stamping operations in v1 (fail-closed).
    /// </summary>
    public sealed class ScreenshotStampHostAdapter : IScreenshotStampAdapter
    {
        public string AdapterName => "ScreenshotStampHostAdapter";
        public bool IsLiveIntegrationSupported => false;

        public bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName)
        {
            return false;
        }

        public StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight)
        {
            return new StampTargetProbe
            {
                IsValidTarget = false,
                AdapterName = AdapterName,
                TargetHwnd = targetHwnd,
                StatusSummary = "请移到 Word 文档页面"
            };
        }

        public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd)
        {
            return StampPlacementResult.Rejected(
                AdapterName,
                "请移到 Word 文档页面");
        }

        public StampPlacementResult TryStampScreenshotRegion(Rectangle screenRegionPx, StampRequest request)
        {
            return StampPlacementResult.Rejected(
                AdapterName,
                "请移到 Word 文档页面");
        }
    }
}
