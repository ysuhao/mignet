using System;
using System.Drawing;

namespace DesktopStamp.Core
{
    /// <summary>
    /// Tuning constants and physical parameters for the desktop stamp state machine.
    /// </summary>
    public sealed class StampConfiguration
    {
        public double DefaultPhysicalSizeMm { get; set; } = 40.0;
        public double MinPhysicalSizeMm { get; set; } = 12.0;
        public double MaxPhysicalSizeMm { get; set; } = 75.0;
        public double ScaleStepMmPerWheelNotch { get; set; } = 1.5;
        public double RotationDegPerWheelNotch { get; set; } = 3.0;

        /// <summary>
        /// Spring stiffness during DraggingPickup (gives tactile physical lag when lifting).
        /// </summary>
        public double PickupSpringStiffness { get; set; } = 210.0;

        /// <summary>
        /// Spring damping during DraggingPickup.
        /// </summary>
        public double PickupSpringDamping { get; set; } = 26.0;

        /// <summary>
        /// Spring stiffness during Aiming (responsive precision tracking).
        /// </summary>
        public double AimingSpringStiffness { get; set; } = 720.0;

        /// <summary>
        /// Spring damping during Aiming.
        /// </summary>
        public double AimingSpringDamping { get; set; } = 50.0;

        /// <summary>
        /// Distance (in pixels) over which dragging from Idle completes the morph to top-down reticle.
        /// </summary>
        public double PickupMorphDistancePx { get; set; } = 140.0;

        /// <summary>
        /// Convergence speed for completing remaining morph/tilt after releasing pickup into Aiming.
        /// </summary>
        public double ReleaseMorphConvergenceRate { get; set; } = 20.0;

        /// <summary>
        /// Duration of the Stamping Z-compression and rebound cycle in seconds.
        /// </summary>
        public double StampCycleDurationSeconds { get; set; } = 0.21;

        /// <summary>
        /// Normalized progress within StampCycleDurationSeconds at which paper impact occurs.
        /// </summary>
        public double StampImpactNormalizedTime { get; set; } = 0.30;

        /// <summary>
        /// Minimum cooldown between consecutive stamp impressions to prevent accidental double-stamping.
        /// </summary>
        public double StampDebounceSeconds { get; set; } = 0.26;

        /// <summary>
        /// Duration of the smooth arc return from Aiming to Idle in seconds.
        /// </summary>
        public double ReturnArcDurationSeconds { get; set; } = 0.28;

        /// <summary>
        /// Minimum magnitude of per-impression random angular jitter in degrees.
        /// </summary>
        public double MinImpressionJitterDeg { get; set; } = 1.0;

        /// <summary>
        /// Maximum magnitude of per-impression random angular jitter in degrees.
        /// </summary>
        public double MaxImpressionJitterDeg { get; set; } = 3.0;

        /// <summary>
        /// Honors OS reduced-motion accessibility setting when true.
        /// </summary>
        public bool ReducedMotion { get; set; } = false;

        /// <summary>
        /// Computes a DPI-aware, work-area clamped parked center point so the idle 3D stamp body,
        /// ambient shadow, and hover caption never clip outside the screen edges at 100%-225% DPI.
        /// </summary>
        public static Point ComputeSafeParkedCenter(Rectangle workArea, double dpiScale)
        {
            double clampedDpi = Math.Max(1.0, Math.Min(3.0, dpiScale));
            // Idle sprite is 132x156 logical px + drop shadow & caption clearance
            int marginRight = (int)Math.Round(118.0 * clampedDpi);
            int marginBottom = (int)Math.Round(126.0 * clampedDpi);
            int minMarginLeft = (int)Math.Round(96.0 * clampedDpi);
            int minMarginTop = (int)Math.Round(104.0 * clampedDpi);

            int targetX = workArea.Right - marginRight;
            int targetY = workArea.Bottom - marginBottom;

            return ClampParkedCenterToWorkArea(new Point(targetX, targetY), workArea, clampedDpi, minMarginLeft, minMarginTop);
        }

        public static Point ClampParkedCenterToWorkArea(
            Point candidateCenter,
            Rectangle workArea,
            double dpiScale,
            int? minHorizontalMarginPx = null,
            int? minVerticalMarginPx = null)
        {
            double clampedDpi = Math.Max(1.0, Math.Min(3.0, dpiScale));
            int padX = minHorizontalMarginPx ?? (int)Math.Round(92.0 * clampedDpi);
            int padY = minVerticalMarginPx ?? (int)Math.Round(102.0 * clampedDpi);

            int minX = workArea.Left + padX;
            int maxX = Math.Max(minX, workArea.Right - padX);
            int minY = workArea.Top + padY;
            int maxY = Math.Max(minY, workArea.Bottom - padY);

            int x = Math.Max(minX, Math.Min(maxX, candidateCenter.X));
            int y = Math.Max(minY, Math.Min(maxY, candidateCenter.Y));
            return new Point(x, y);
        }
    }
}
