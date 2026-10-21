using System;

namespace DesktopStamp.Core
{
    /// <summary>
    /// Coherent lifecycle states of the desktop physical stamp.
    /// </summary>
    public enum StampState
    {
        /// <summary>
        /// Upright stamp parked near desktop corner; hover breathes crimson glow and shows active seal name.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Left-drag pickup from parked state: spring-damper lag, velocity-dependent tilt,
        /// and smooth pose transition from upright side view through tilted view to top-down reticle.
        /// </summary>
        DraggingPickup = 1,

        /// <summary>
        /// Suspended top-down optical reticle over target document; center aperture is transparent
        /// with a semitransparent red seal ghost.
        /// </summary>
        Aiming = 2,

        /// <summary>
        /// Fast Z-compression, paper impact, angular jitter, shockwave ripple, and spring rebound,
        /// returning automatically to Aiming for repeated stamping.
        /// </summary>
        Stamping = 3,

        /// <summary>
        /// Smooth arc return from Aiming back to the parked upright position on Right-Click or Esc.
        /// </summary>
        Returning = 4
    }

    /// <summary>
    /// Physical size lock modes for the seal imprint.
    /// </summary>
    public enum SizeLockMode
    {
        /// <summary>
        /// Free scaling via Ctrl + Mouse Wheel between MinPhysicalSizeMm and MaxPhysicalSizeMm.
        /// </summary>
        Unlocked = 0,

        /// <summary>
        /// Locked to 20.0 mm physical seal width (e.g. personal chop / small seal).
        /// </summary>
        Locked20mm = 20,

        /// <summary>
        /// Locked to 40.0 mm physical seal width (e.g. standard circular/oval official seal size).
        /// </summary>
        Locked40mm = 40
    }

    /// <summary>
    /// 2D double-precision vector / screen coordinate.
    /// </summary>
    public struct Vector2D : IEquatable<Vector2D>
    {
        public double X { get; }
        public double Y { get; }

        public Vector2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double Length => Math.Sqrt(X * X + Y * Y);

        public static Vector2D operator +(Vector2D a, Vector2D b) => new Vector2D(a.X + b.X, a.Y + b.Y);
        public static Vector2D operator -(Vector2D a, Vector2D b) => new Vector2D(a.X - b.X, a.Y - b.Y);
        public static Vector2D operator *(Vector2D a, double s) => new Vector2D(a.X * s, a.Y * s);

        public static double Distance(Vector2D a, Vector2D b) => (a - b).Length;

        public static Vector2D Lerp(Vector2D a, Vector2D b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return new Vector2D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        public static Vector2D QuadraticBezier(Vector2D p0, Vector2D p1, Vector2D p2, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            double u = 1.0 - t;
            return p0 * (u * u) + p1 * (2.0 * u * t) + p2 * (t * t);
        }

        public bool Equals(Vector2D other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Vector2D other && Equals(other);
        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();
        public override string ToString() => $"({X:F1}, {Y:F1})";
    }
}
