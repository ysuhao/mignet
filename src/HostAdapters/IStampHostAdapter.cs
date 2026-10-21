using System;
using System.Diagnostics;
using System.Drawing;
using DesktopStamp.Core;
using DesktopStamp.Imaging;

namespace DesktopStamp.HostAdapters
{
    /// <summary>
    /// Request payload for stamping an impression into a target host document.
    /// Carries the immutable click-time snapshot (point, expected target/root HWND, epochs,
    /// physical size, angles, seal, and creation timestamp) plus managed pre-mutation and
    /// post-configuration invalidation guards.
    /// </summary>
    public sealed class StampRequest
    {
        public int RequestId { get; set; }
        public Vector2D ScreenPointPx { get; set; }
        public IntPtr ExpectedTargetHwnd { get; set; }
        public IntPtr ExpectedRootHwnd { get; set; }
        public SealAsset Seal { get; set; }
        public Bitmap ImpressionBitmap { get; set; }
        public double PhysicalSizeMm { get; set; }
        public double BaseAngleDeg { get; set; }
        public double JitterAngleDeg { get; set; }
        public double EffectiveAngleDeg => StampStateMachine.NormalizeAngle(BaseAngleDeg + JitterAngleDeg);
        public int ImpressionSeed { get; set; }
        public int ClickViewEpoch { get; set; }
        public int ClickCancelEpoch { get; set; }
        public long CreatedTimestampTicks { get; set; }
        public double MaxAllowedAgeMs { get; set; } = 2500.0;

        /// <summary>
        /// Managed freshness/cancellation/epoch guard evaluated immediately before document mutation
        /// (after slow COM mapping and temp file I/O) and again after post-insert shape configuration.
        /// Returns a non-empty concise Chinese rejection message if invalidated, or null if still valid.
        /// </summary>
        public Func<string> CheckInvalidationReason { get; set; }

        /// <summary>
        /// Optional deterministic test hook invoked after slow coordinate mapping and temp PNG write,
        /// immediately prior to the pre-mutation guard check and Shapes.AddPicture.
        /// </summary>
        public Action OnBeforeMutationHook { get; set; }

        /// <summary>
        /// Optional gate invoked right before the document is mutated (after all read-only preparation).
        /// Blocks until mutation is allowed and returns null, or returns a rejection reason.
        /// </summary>
        public Func<string> AwaitMutationPermit { get; set; }

        /// <summary>
        /// Optional deterministic test hook invoked after Shapes.AddPicture during shape property configuration,
        /// immediately prior to the post-configuration guard check and rollback decision.
        /// </summary>
        public Action OnDuringConfigurationHook { get; set; }

        public bool TryGetInvalidationReason(out string reason)
        {
            if (CheckInvalidationReason != null)
            {
                string customReason = CheckInvalidationReason();
                if (!string.IsNullOrEmpty(customReason))
                {
                    reason = customReason;
                    return true;
                }
            }

            if (CreatedTimestampTicks > 0 && MaxAllowedAgeMs > 0.0)
            {
                double ageMs = (Stopwatch.GetTimestamp() - CreatedTimestampTicks) * 1000.0 / Stopwatch.Frequency;
                if (ageMs > MaxAllowedAgeMs)
                {
                    reason = "盖章请求已超时，请重新点击盖章";
                    return true;
                }
            }

            reason = null;
            return false;
        }
    }

    /// <summary>
    /// Result of probing the desktop window beneath the aiming reticle.
    /// </summary>
    public sealed class StampTargetProbe
    {
        public bool IsValidTarget { get; set; }
        public string AdapterName { get; set; }
        public string StatusSummary { get; set; }
        public IntPtr TargetHwnd { get; set; }
        public IntPtr RootHwnd { get; set; }
        public double MonitorDpi { get; set; } = 96.0;
        public double DocumentZoomPercent { get; set; } = 100.0;
        public int PageNumber { get; set; }
        public double PageLeftPt { get; set; }
        public double PageTopPt { get; set; }
    }

    /// <summary>
    /// Result of attempting to apply a stamp to a host document.
    /// </summary>
    public sealed class StampPlacementResult
    {
        public bool Success { get; set; }
        public bool FailedClosed { get; set; }
        public bool WasAbortedByEpoch { get; set; }
        public bool WasRolledBack { get; set; }
        public string AdapterName { get; set; }
        public string Message { get; set; }
        public int PageNumber { get; set; }
        public double PlacedCenterLeftPt { get; set; }
        public double PlacedCenterTopPt { get; set; }
        public double PlacedShapeLeftPt { get; set; }
        public double PlacedShapeTopPt { get; set; }
        public double PlacedWidthPt { get; set; }
        public double PlacedHeightPt { get; set; }
        public double PlacedInkWidthPt { get; set; }
        public double PlacedInkHeightPt { get; set; }
        public double PlacedRotationDeg { get; set; }
        public string ShapeName { get; set; }

        /// <summary>
        /// Developer-facing detail (phase timings, internal failure cause) for the log; never shown in the UI.
        /// </summary>
        public string Diagnostics { get; set; }

        public static StampPlacementResult Rejected(
            string adapterName,
            string reason,
            bool abortedByEpoch = false,
            bool rolledBack = false) => new StampPlacementResult
        {
            Success = false,
            FailedClosed = true,
            WasAbortedByEpoch = abortedByEpoch,
            WasRolledBack = rolledBack,
            AdapterName = adapterName,
            Message = reason
        };
    }

    /// <summary>
    /// Host adapter contract decoupling visual overlay/interaction from document injection.
    /// </summary>
    public interface IStampHostAdapter
    {
        string AdapterName { get; }
        bool IsLiveIntegrationSupported { get; }

        bool CanHandleWindow(IntPtr targetHwnd, string windowClassName, string rootClassName, string processName);

        /// <param name="lightweight">
        /// True while the cursor is moving: only verify the window/view and read zoom (a few cheap calls) so the
        /// host application is not flooded with layout hit-tests. False when the cursor rests: full page mapping.
        /// </param>
        StampTargetProbe ProbeTarget(Vector2D screenPointPx, IntPtr targetHwnd, bool lightweight);

        StampPlacementResult TryApplyStamp(StampRequest request, IntPtr targetHwnd);
    }

    /// <summary>
    /// Documented extension point interface for future screenshot/composited capture stamping.
    /// </summary>
    public interface IScreenshotStampAdapter : IStampHostAdapter
    {
        StampPlacementResult TryStampScreenshotRegion(Rectangle screenRegionPx, StampRequest request);
    }
}
