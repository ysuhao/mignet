using System;

namespace DesktopStamp.Core
{
    /// <summary>
    /// Single coherent, elapsed-time (dt) stable state machine governing the desktop stamp's
    /// pose, spring-damper motion, velocity-dependent tilt, aiming reticle, stamping Z-compression,
    /// per-impression jitter, and return arc.
    /// </summary>
    public sealed class StampStateMachine
    {
        private readonly StampConfiguration _config;
        private readonly Random _rng;

        private Vector2D _returnStartPosition;
        private Vector2D _returnControlPoint;
        private double _returnStartMorph;
        private double _returnStartTiltDeg;
        private Vector2D _pickupGrabOffset;
        private double _returnElapsedSeconds;
        private double _stampElapsedSeconds;
        private double _cooldownRemainingSeconds;
        private double _pickupTimeSeconds;

        public StampConfiguration Configuration => _config;
        public StampState CurrentState { get; private set; } = StampState.Idle;
        public SizeLockMode SizeLock { get; private set; } = SizeLockMode.Unlocked;

        /// <summary>
        /// Physical ink width in millimeters (20mm, 40mm, or custom when unlocked).
        /// </summary>
        public double PhysicalSizeMm { get; private set; }

        /// <summary>
        /// Parked dock coordinate in physical screen pixels.
        /// </summary>
        public Vector2D ParkedPosition { get; private set; }

        /// <summary>
        /// Current smoothed position of the stamp/reticle center in physical screen pixels.
        /// </summary>
        public Vector2D CurrentPosition { get; private set; }

        /// <summary>
        /// Target cursor position in physical screen pixels.
        /// </summary>
        public Vector2D TargetPosition { get; private set; }

        /// <summary>
        /// Current velocity in screen pixels per second.
        /// </summary>
        public Vector2D Velocity { get; private set; }

        /// <summary>
        /// Whether the pointer is currently hovering over the upright stamp body in Idle mode.
        /// </summary>
        public bool IsHoveredInIdle { get; private set; }

        /// <summary>
        /// Smooth 0..1 hover transition factor for label reveal and base glow amplification.
        /// </summary>
        public double HoverProgress { get; private set; }

        /// <summary>
        /// Continuous breathing phase in radians for the crimson base seam glow when hovered.
        /// </summary>
        public double HoverBreathPhase { get; private set; }

        /// <summary>
        /// Pose morph progress: 0.0 = upright side view (idle), 0.5 = lifted tilted view (drag), 1.0 = top-down reticle (aiming).
        /// </summary>
        public double MorphProgress { get; private set; }

        /// <summary>
        /// Velocity-induced dynamic tilt angle in degrees during pickup/return.
        /// </summary>
        public double TiltAngleDeg { get; private set; }

        /// <summary>
        /// Subtle tactile press compression scale (dips toward 0.97 on initial press and relaxes to 1.0).
        /// </summary>
        public double PressScale { get; private set; } = 1.0;

        /// <summary>
        /// User-set base rotation angle in degrees (-180..180) controlled by Alt+Wheel or wheel on outer ring.
        /// </summary>
        public double BaseRotationDeg { get; private set; }

        /// <summary>
        /// Signed random angular jitter (±1° to ±3°) generated at the moment of the most recent stamp impression.
        /// </summary>
        public double LastImpressionJitterDeg { get; private set; }

        /// <summary>
        /// Total angle in degrees for the current/most recent stamp impression (BaseRotationDeg + LastImpressionJitterDeg).
        /// </summary>
        public double EffectiveImpressionAngleDeg => NormalizeAngle(BaseRotationDeg + LastImpressionJitterDeg);

        /// <summary>
        /// Normalized 0..1 progress through the Stamping state.
        /// </summary>
        public double StampCycleProgress { get; private set; }

        /// <summary>
        /// Normalized 0..1 downward Z-compression (peaks at 1.0 at the moment of paper impact).
        /// </summary>
        public double ZCompression { get; private set; }

        /// <summary>
        /// Normalized 0..1 progress of the expanding post-impact air shockwave ripple.
        /// </summary>
        public double ImpactRippleProgress { get; private set; } = 1.0;

        /// <summary>
        /// Vertical bounce offset in pixels when a new seal image is dropped onto the idle stamp.
        /// </summary>
        public double DropBounceOffsetPx { get; private set; }

        /// <summary>
        /// Crimson flash intensity (0..1) when a new seal image is dropped onto the idle stamp or on impact.
        /// </summary>
        public double CrimsonFlashIntensity { get; private set; }

        /// <summary>
        /// True when the state machine is parked in Idle with no active hover, flash, bounce, or residual motion.
        /// </summary>
        public bool IsSettledIdle =>
            CurrentState == StampState.Idle &&
            !IsHoveredInIdle &&
            HoverProgress < 0.002 &&
            MorphProgress < 0.001 &&
            Math.Abs(TiltAngleDeg) < 0.02 &&
            Math.Abs(PressScale - 1.0) < 0.002 &&
            CrimsonFlashIntensity <= 0.001 &&
            _dropBouncePhase <= 0.0 &&
            ImpactRippleProgress >= 1.0 &&
            _cooldownRemainingSeconds <= 0.0;

        /// <summary>
        /// True when in Aiming state and spring tracking + morph convergence have settled at the target point.
        /// </summary>
        public bool IsSettledAiming =>
            CurrentState == StampState.Aiming &&
            Vector2D.Distance(CurrentPosition, TargetPosition) < 0.35 &&
            Velocity.Length < 2.0 &&
            MorphProgress > 0.998 &&
            Math.Abs(TiltAngleDeg) < 0.05 &&
            CrimsonFlashIntensity <= 0.001 &&
            ImpactRippleProgress >= 1.0;

        private double _dropBouncePhase;
        private bool _impactFiredInCurrentStamp;
        private Vector2D _stampClickPosition;

        /// <summary>
        /// The immutable screen coordinate captured at accepted LEFT MOUSE DOWN for the current stamp plunge.
        /// </summary>
        public Vector2D StampClickPosition => _stampClickPosition;

        /// <summary>
        /// Fired synchronously at accepted LEFT MOUSE DOWN inside TryTriggerStamp, allowing the UI/host layer
        /// to snapshot the immutable click request (point, target/root HWND, epochs, size, baseAngle, jitter, seal)
        /// before the ~60ms plunge animation reaches paper impact.
        /// Arguments: (clickScreenPoint, physicalSizeMm, baseRotationDeg, impressionJitterDeg).
        /// </summary>
        public event Action<Vector2D, double, double, double> StampTriggered;

        /// <summary>
        /// Fired exactly once per Stamping cycle at the moment of maximum Z-compression (paper contact).
        /// </summary>
        public event Action<Vector2D, double, double> StampImpactCommitted;

        public StampStateMachine(StampConfiguration config = null, Vector2D? initialParkedPosition = null, int? randomSeed = null)
        {
            _config = config ?? new StampConfiguration();
            _rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
            PhysicalSizeMm = _config.DefaultPhysicalSizeMm;

            var startPos = initialParkedPosition ?? new Vector2D(1600, 900);
            ParkedPosition = startPos;
            CurrentPosition = startPos;
            TargetPosition = startPos;
        }

        public void SetParkedPosition(Vector2D parkedPosition, bool snapIfIdle = true)
        {
            ParkedPosition = parkedPosition;
            if (snapIfIdle && CurrentState == StampState.Idle)
            {
                CurrentPosition = parkedPosition;
                TargetPosition = parkedPosition;
                Velocity = new Vector2D(0, 0);
            }
        }

        public void SetHoverInIdle(bool isHovered)
        {
            IsHoveredInIdle = (CurrentState == StampState.Idle) && isHovered;
        }

        /// <summary>
        /// Begins dragging pickup from Idle or mid-Returning mode when the user left-presses/drags the stamp.
        /// Preserves current position/morph continuity and records the grab offset so there is no abrupt jump.
        /// </summary>
        public bool BeginPickupDrag(Vector2D cursorPosition)
        {
            if (CurrentState != StampState.Idle && CurrentState != StampState.Returning)
                return false;

            Vector2D rawGrabOffset = CurrentPosition - cursorPosition;
            _pickupGrabOffset = rawGrabOffset.Length <= 80.0 ? rawGrabOffset : new Vector2D(0, 0);
            CurrentState = StampState.DraggingPickup;
            TargetPosition = cursorPosition;
            _pickupTimeSeconds = 0.0;
            PressScale = _config.ReducedMotion ? 1.0 : 0.97;
            IsHoveredInIdle = false;

            if (_config.ReducedMotion)
            {
                _pickupGrabOffset = new Vector2D(0, 0);
                MorphProgress = 1.0;
                TiltAngleDeg = 0.0;
            }

            return true;
        }

        /// <summary>
        /// Updates pointer position across any state.
        /// </summary>
        public void OnPointerMove(Vector2D cursorPosition)
        {
            if (CurrentState == StampState.DraggingPickup ||
                CurrentState == StampState.Aiming)
            {
                TargetPosition = cursorPosition;
            }
            else if (CurrentState == StampState.Stamping && _impactFiredInCurrentStamp)
            {
                // Only update target position after paper contact has occurred so pre-impact click position stays locked
                TargetPosition = cursorPosition;
            }
        }

        /// <summary>
        /// Releases left mouse button after pickup drag -> stays suspended in Aiming mode.
        /// Smoothly converges any remaining morph/tilt instead of snapping discontinuously.
        /// </summary>
        public bool CompletePickupToAiming(Vector2D cursorPosition)
        {
            if (CurrentState != StampState.DraggingPickup)
                return false;

            TargetPosition = cursorPosition;
            CurrentState = StampState.Aiming;
            _pickupGrabOffset = new Vector2D(0, 0);

            if (_config.ReducedMotion || MorphProgress > 0.96)
            {
                MorphProgress = 1.0;
                TiltAngleDeg = 0.0;
            }

            return true;
        }

        /// <summary>
        /// Commits a stamp impression in Aiming mode (Left Click).
        /// Enforces debounce cooldown and state guard against accidental double-stamping,
        /// and fires StampTriggered synchronously so the immutable click snapshot is captured at LEFT DOWN.
        /// </summary>
        public bool TryTriggerStamp(Vector2D cursorPosition)
        {
            if (CurrentState != StampState.Aiming)
                return false;

            if (_cooldownRemainingSeconds > 0.0)
                return false;

            _stampClickPosition = cursorPosition;
            TargetPosition = cursorPosition;
            CurrentPosition = cursorPosition;
            Velocity = new Vector2D(0, 0);
            MorphProgress = 1.0;
            TiltAngleDeg = 0.0;

            CurrentState = StampState.Stamping;
            _stampElapsedSeconds = 0.0;
            StampCycleProgress = 0.0;
            ZCompression = 0.0;
            PressScale = 0.97;
            _impactFiredInCurrentStamp = false;
            _cooldownRemainingSeconds = _config.StampDebounceSeconds;

            // Generate signed random angular jitter in ±[1.0°, 3.0°]:
            double magnitude = _config.MinImpressionJitterDeg +
                               _rng.NextDouble() * (_config.MaxImpressionJitterDeg - _config.MinImpressionJitterDeg);
            int sign = _rng.Next(0, 2) == 0 ? -1 : 1;
            LastImpressionJitterDeg = sign * magnitude;

            StampTriggered?.Invoke(_stampClickPosition, PhysicalSizeMm, BaseRotationDeg, LastImpressionJitterDeg);

            return true;
        }

        /// <summary>
        /// Right-click in Aiming or Esc key returns along a smooth cubic ease-out arc to the parked upright position.
        /// Preserves current MorphProgress when cancelling a partial pickup so the pose never jumps backward.
        /// </summary>
        public bool BeginReturnToParked()
        {
            if (CurrentState == StampState.Idle || CurrentState == StampState.Returning)
                return false;

            CurrentState = StampState.Returning;
            _returnStartPosition = CurrentPosition;
            _returnStartMorph = Math.Max(0.0, Math.Min(1.0, MorphProgress));
            _returnStartTiltDeg = TiltAngleDeg;
            _returnElapsedSeconds = 0.0;
            _pickupGrabOffset = new Vector2D(0, 0);
            TargetPosition = ParkedPosition;
            Velocity = new Vector2D(0, 0);

            // Compute an elevated arc control point above the midpoint between CurrentPosition and ParkedPosition:
            Vector2D mid = Vector2D.Lerp(_returnStartPosition, ParkedPosition, 0.5);
            double dist = Vector2D.Distance(_returnStartPosition, ParkedPosition);
            double arcLift = _config.ReducedMotion
                ? 0.0
                : Math.Min(210.0, Math.Max(60.0, dist * 0.28));
            _returnControlPoint = new Vector2D(mid.X, mid.Y - arcLift);

            return true;
        }

        /// <summary>
        /// Adjusts physical stamp size via Ctrl + Mouse Wheel only while SizeLock is Unlocked.
        /// Selecting Locked20mm or Locked40mm locks the physical size until explicitly unlocked.
        /// </summary>
        public bool AdjustPhysicalSizeByWheel(int wheelDeltaNotches)
        {
            if (wheelDeltaNotches == 0)
                return false;

            if (SizeLock != SizeLockMode.Unlocked)
                return false;

            double nextSize = PhysicalSizeMm + wheelDeltaNotches * _config.ScaleStepMmPerWheelNotch;
            PhysicalSizeMm = Math.Max(_config.MinPhysicalSizeMm, Math.Min(_config.MaxPhysicalSizeMm, nextSize));
            return true;
        }

        public void SetSizeLock(SizeLockMode mode)
        {
            SizeLock = mode;
            if (mode == SizeLockMode.Locked20mm)
            {
                PhysicalSizeMm = 20.0;
            }
            else if (mode == SizeLockMode.Locked40mm)
            {
                PhysicalSizeMm = 40.0;
            }
        }

        public void SetPhysicalSizeMm(double sizeMm)
        {
            SizeLock = SizeLockMode.Unlocked;
            PhysicalSizeMm = Math.Max(_config.MinPhysicalSizeMm, Math.Min(_config.MaxPhysicalSizeMm, sizeMm));
        }

        /// <summary>
        /// Rotates the active seal via Alt+Wheel or Wheel on outer ring.
        /// </summary>
        public void RotateByWheel(int wheelDeltaNotches)
        {
            if (wheelDeltaNotches == 0)
                return;

            BaseRotationDeg = NormalizeAngle(BaseRotationDeg + wheelDeltaNotches * _config.RotationDegPerWheelNotch);
        }

        public void ResetRotation()
        {
            BaseRotationDeg = 0.0;
            LastImpressionJitterDeg = 0.0;
        }

        /// <summary>
        /// Triggers the brief bounce and crimson flash when a seal image is dropped onto the idle stamp.
        /// </summary>
        public void TriggerSealLoadedFeedback()
        {
            _dropBouncePhase = _config.ReducedMotion ? 0.0 : 0.001;
            CrimsonFlashIntensity = 1.0;
        }

        /// <summary>
        /// Advances the state machine by <paramref name="dtSeconds"/> using fixed sub-stepping for frame-rate independence.
        /// </summary>
        public void Update(double dtSeconds)
        {
            if (dtSeconds <= 0.0 || double.IsNaN(dtSeconds) || double.IsInfinity(dtSeconds))
                return;

            // Clamp maximum frame delta to 0.25s to avoid huge jumps after OS sleep/debugger pause
            double clampedDt = Math.Min(0.25, dtSeconds);
            const double maxSubStep = 1.0 / 240.0;

            int steps = Math.Max(1, (int)Math.Ceiling(clampedDt / maxSubStep));
            double subDt = clampedDt / steps;

            for (int i = 0; i < steps; i++)
            {
                StepPhysics(subDt);
            }
        }

        private void StepPhysics(double dt)
        {
            if (_cooldownRemainingSeconds > 0.0)
            {
                _cooldownRemainingSeconds = Math.Max(0.0, _cooldownRemainingSeconds - dt);
            }

            // Smooth hover transition
            double hoverTarget = (CurrentState == StampState.Idle && IsHoveredInIdle) ? 1.0 : 0.0;
            HoverProgress += (hoverTarget - HoverProgress) * Math.Min(1.0, dt * 12.0);
            if (hoverTarget == 0.0 && HoverProgress < 0.001)
            {
                HoverProgress = 0.0;
            }

            // Advance breathing phase only when hover or flash is active so settled Idle is 100% static
            if (IsHoveredInIdle || HoverProgress > 0.001 || CrimsonFlashIntensity > 0.001)
            {
                HoverBreathPhase = (HoverBreathPhase + dt * 2.6) % (Math.PI * 2.0);
            }

            // Relax tactile press compression toward 1.0
            if (Math.Abs(1.0 - PressScale) > 0.0005)
            {
                PressScale += (1.0 - PressScale) * Math.Min(1.0, dt * 18.0);
            }
            else
            {
                PressScale = 1.0;
            }

            // Decay crimson flash
            if (CrimsonFlashIntensity > 0.0)
            {
                CrimsonFlashIntensity = Math.Max(0.0, CrimsonFlashIntensity - dt * 2.8);
            }

            // Advance drop bounce animation if active
            if (_dropBouncePhase > 0.0)
            {
                _dropBouncePhase += dt * 3.6;
                if (_dropBouncePhase >= 1.0)
                {
                    _dropBouncePhase = 0.0;
                    DropBounceOffsetPx = 0.0;
                }
                else
                {
                    // Damped sinusoidal vertical bounce
                    DropBounceOffsetPx = -18.0 * Math.Exp(-4.2 * _dropBouncePhase) * Math.Sin(_dropBouncePhase * Math.PI * 2.5);
                }
            }

            // Advance post-impact shockwave ripple
            if (ImpactRippleProgress < 1.0)
            {
                ImpactRippleProgress = Math.Min(1.0, ImpactRippleProgress + dt * 3.6);
            }

            switch (CurrentState)
            {
                case StampState.Idle:
                    CurrentPosition = ParkedPosition;
                    Velocity = new Vector2D(0, 0);
                    MorphProgress = Math.Max(0.0, MorphProgress - dt * 10.0);
                    if (MorphProgress < 0.001) MorphProgress = 0.0;
                    TiltAngleDeg += (0.0 - TiltAngleDeg) * Math.Min(1.0, dt * 14.0);
                    if (Math.Abs(TiltAngleDeg) < 0.01) TiltAngleDeg = 0.0;
                    ZCompression = 0.0;
                    break;

                case StampState.DraggingPickup:
                    _pickupTimeSeconds += dt;
                    _pickupGrabOffset = _pickupGrabOffset * Math.Exp(-24.0 * dt);
                    if (_pickupGrabOffset.Length < 0.25)
                    {
                        _pickupGrabOffset = new Vector2D(0, 0);
                    }

                    Vector2D effectiveDragTarget = TargetPosition + _pickupGrabOffset;
                    IntegrateSpringDamperToTarget(
                        effectiveDragTarget,
                        _config.ReducedMotion ? 900.0 : _config.PickupSpringStiffness,
                        _config.ReducedMotion ? 60.0 : _config.PickupSpringDamping,
                        dt);

                    if (_config.ReducedMotion)
                    {
                        TiltAngleDeg = 0.0;
                        MorphProgress = 1.0;
                    }
                    else
                    {
                        // Velocity-dependent tilt (clamped to [-22, +22] degrees)
                        double targetTilt = Math.Max(-22.0, Math.Min(22.0, Velocity.X * 0.016));
                        TiltAngleDeg += (targetTilt - TiltAngleDeg) * Math.Min(1.0, dt * 16.0);

                        // Smooth flip from upright side view (0.0) through tilted drag view (0.5) to top-down reticle (1.0)
                        double distFromPark = Vector2D.Distance(CurrentPosition, ParkedPosition);
                        double distMorph = Math.Min(1.0, distFromPark / Math.Max(1.0, _config.PickupMorphDistancePx));
                        double timeMorph = Math.Min(1.0, _pickupTimeSeconds / 0.26);
                        double desiredMorph = Math.Max(distMorph, timeMorph * 0.88);
                        MorphProgress += (desiredMorph - MorphProgress) * Math.Min(1.0, dt * 15.0);
                    }
                    break;

                case StampState.Aiming:
                    IntegrateSpringDamperToTarget(
                        TargetPosition,
                        _config.ReducedMotion ? 1100.0 : _config.AimingSpringStiffness,
                        _config.ReducedMotion ? 68.0 : _config.AimingSpringDamping,
                        dt);

                    double convRate = Math.Max(8.0, _config.ReleaseMorphConvergenceRate);
                    MorphProgress += (1.0 - MorphProgress) * Math.Min(1.0, dt * convRate);
                    if (1.0 - MorphProgress < 0.0015)
                    {
                        MorphProgress = 1.0;
                    }

                    TiltAngleDeg += (0.0 - TiltAngleDeg) * Math.Min(1.0, dt * convRate);
                    if (Math.Abs(TiltAngleDeg) < 0.02)
                    {
                        TiltAngleDeg = 0.0;
                    }

                    ZCompression = 0.0;
                    break;

                case StampState.Stamping:
                    _stampElapsedSeconds += dt;
                    double duration = _config.ReducedMotion
                        ? Math.Min(0.11, Math.Max(0.05, _config.StampCycleDurationSeconds))
                        : Math.Max(0.05, _config.StampCycleDurationSeconds);
                    StampCycleProgress = Math.Min(1.0, _stampElapsedSeconds / duration);

                    double impactT = _config.StampImpactNormalizedTime;
                    if (StampCycleProgress <= impactT)
                    {
                        CurrentPosition = _stampClickPosition;
                        // Fast downward plunge with cubic ease-out into paper contact
                        double u = StampCycleProgress / Math.Max(0.01, impactT);
                        ZCompression = CubicEaseOut(u);
                    }
                    else
                    {
                        // Fire impact callback once at peak compression using the immutable click position
                        if (!_impactFiredInCurrentStamp)
                        {
                            _impactFiredInCurrentStamp = true;
                            CurrentPosition = _stampClickPosition;
                            ImpactRippleProgress = _config.ReducedMotion ? 1.0 : 0.0;
                            CrimsonFlashIntensity = 0.9;
                            StampImpactCommitted?.Invoke(_stampClickPosition, PhysicalSizeMm, EffectiveImpressionAngleDeg);
                        }

                        // Responsive rebound back to aiming plane
                        double u = (StampCycleProgress - impactT) / Math.Max(0.01, 1.0 - impactT);
                        ZCompression = Math.Max(0.0, 1.0 - CubicEaseOut(u));
                    }

                    if (StampCycleProgress >= 1.0)
                    {
                        ZCompression = 0.0;
                        StampCycleProgress = 0.0;
                        CurrentState = StampState.Aiming;
                    }
                    break;

                case StampState.Returning:
                    _returnElapsedSeconds += dt;
                    double returnDuration = _config.ReducedMotion
                        ? Math.Min(0.12, Math.Max(0.06, _config.ReturnArcDurationSeconds))
                        : Math.Max(0.08, _config.ReturnArcDurationSeconds);
                    double t = Math.Min(1.0, _returnElapsedSeconds / returnDuration);
                    // Responsive ease-out starts with immediate velocity on Right-Click/Esc and settles gently into the dock
                    double easedT = EaseOutReturn(t);

                    Vector2D prevPos = CurrentPosition;
                    CurrentPosition = Vector2D.QuadraticBezier(_returnStartPosition, _returnControlPoint, ParkedPosition, easedT);
                    Velocity = (CurrentPosition - prevPos) * (1.0 / Math.Max(1e-5, dt));

                    // Reverse morph smoothly from the actual morph at cancellation time back to upright (0.0)
                    MorphProgress = _returnStartMorph * (1.0 - easedT);
                    if (_config.ReducedMotion)
                    {
                        TiltAngleDeg = 0.0;
                    }
                    else
                    {
                        double returnTilt = Math.Max(-16.0, Math.Min(16.0, Velocity.X * 0.011)) * (1.0 - t);
                        TiltAngleDeg += (returnTilt - TiltAngleDeg) * Math.Min(1.0, dt * 16.0);
                    }

                    if (t >= 1.0)
                    {
                        CurrentPosition = ParkedPosition;
                        Velocity = new Vector2D(0, 0);
                        MorphProgress = 0.0;
                        TiltAngleDeg = 0.0;
                        CrimsonFlashIntensity = 0.48; // Brief crimson base glow on return
                        CurrentState = StampState.Idle;
                    }
                    break;
            }
        }

        private void IntegrateSpringDamperToTarget(Vector2D target, double stiffness, double damping, double dt)
        {
            Vector2D displacement = CurrentPosition - target;
            Vector2D springForce = displacement * (-stiffness);
            Vector2D dampingForce = Velocity * (-damping);
            Vector2D acceleration = springForce + dampingForce;

            Velocity = Velocity + acceleration * dt;
            CurrentPosition = CurrentPosition + Velocity * dt;
        }

        private static double EaseOutReturn(double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return 1.0 - Math.Pow(1.0 - t, 1.55);
        }

        private static double CubicEaseOut(double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            double inv = 1.0 - t;
            return 1.0 - inv * inv * inv;
        }

        public static double NormalizeAngle(double angleDeg)
        {
            double a = angleDeg % 360.0;
            if (a <= -180.0) a += 360.0;
            if (a > 180.0) a -= 360.0;
            return a;
        }
    }
}
