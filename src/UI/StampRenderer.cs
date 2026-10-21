using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DesktopStamp.Core;
using DesktopStamp.HostAdapters;
using DesktopStamp.Imaging;

namespace DesktopStamp.UI
{
    /// <summary>
    /// Per-pixel alpha compositor for the desktop physical stamp.
    /// Integrates transparent PNG sprites (with normalized occupied-alpha anchors) alongside
    /// bounded pre-scaled Format32bppPArgb caches for idle/drag bodies, optical reticle annulus +
    /// 72 metrological ticks, seal ghost previews, and HUD/hover/toast pills.
    /// </summary>
    public sealed class StampRenderer : IDisposable
    {
        private sealed class CachedSpriteLayer : IDisposable
        {
            public SpriteMetricAsset SourceAsset;
            public int TargetOccupiedHeightKey;
            public int OpacityByte;
            public Bitmap Bitmap;
            public float AnchorOffsetX;
            public float AnchorOffsetY;
            public float DrawWidth;
            public float DrawHeight;

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                    Bitmap = null;
                }
            }
        }

        private sealed class CachedReticleAnnulus : IDisposable
        {
            public int InnerRadiusKey;
            public int OuterRadiusKey;
            public int OpacityByte;
            public Bitmap Bitmap;
            public float HalfSizePx;

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                    Bitmap = null;
                }
            }
        }

        private sealed class CachedSealGhost : IDisposable
        {
            public Bitmap SourceMasterBitmap;
            public int WidthPx;
            public int HeightPx;
            public int OpacityByte;
            public Bitmap Bitmap;

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                    Bitmap = null;
                }
            }
        }

        private sealed class CachedTextPill : IDisposable
        {
            public string Text;
            public int DpiKey;
            public int StyleKey;
            public int AlphaByte;
            public Bitmap Bitmap;

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                    Bitmap = null;
                }
            }
        }

        private sealed class CachedAimingComposite : IDisposable
        {
            public Bitmap SourceMasterBitmap;
            public int InnerRadiusKey;
            public int OuterRadiusKey;
            public int GhostWidthKey;
            public int GhostHeightKey;
            public int GhostOpacityByte;
            public int RingOpacityByte;
            public int AngleKey;
            public Bitmap Bitmap;
            public float HalfSizePx;

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                    Bitmap = null;
                }
            }
        }

        private readonly AssetRepository _assets;
        private CachedSpriteLayer _cachedIdleSprite;
        private CachedSpriteLayer _cachedDragSprite;
        private CachedReticleAnnulus _cachedReticleAnnulus;
        private CachedSealGhost _cachedSealGhost;
        private readonly CachedAimingComposite[] _cachedAimingComposites = new CachedAimingComposite[4];
        private int _aimingCompositeWriteIndex;
        private readonly CachedTextPill[] _cachedHudPills = new CachedTextPill[4];
        private int _hudPillWriteIndex;
        private CachedTextPill _cachedHoverPill;
        private CachedTextPill _cachedToastPill;
        private bool _disposed;

        public long SpriteCacheHits { get; private set; }
        public long SpriteCacheMisses { get; private set; }
        public long ReticleCacheHits { get; private set; }
        public long ReticleCacheMisses { get; private set; }
        public long GhostCacheHits { get; private set; }
        public long GhostCacheMisses { get; private set; }
        public long PillCacheHits { get; private set; }
        public long PillCacheMisses { get; private set; }

        /// <summary>
        /// Visual height of the occupied upright stamp body in screen pixels at 96 DPI (scales with monitor DPI).
        /// </summary>
        public const float BaseIdleBodyHeightPxAt96Dpi = 156f;

        /// <summary>
        /// Maximum supported square overlay canvas dimension in pixels.
        /// Preview extents requiring a canvas larger than this limit are explicitly rejected before stamping
        /// rather than silently shrinking or clipping the seal ghost.
        /// </summary>
        public const int MaxSupportedCanvasSizePx = 2400;

        public StampRenderer(AssetRepository assets)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        }

        public void InvalidateCaches()
        {
            _cachedIdleSprite?.Dispose();
            _cachedIdleSprite = null;

            _cachedDragSprite?.Dispose();
            _cachedDragSprite = null;

            _cachedReticleAnnulus?.Dispose();
            _cachedReticleAnnulus = null;

            _cachedSealGhost?.Dispose();
            _cachedSealGhost = null;

            for (int i = 0; i < _cachedAimingComposites.Length; i++)
            {
                _cachedAimingComposites[i]?.Dispose();
                _cachedAimingComposites[i] = null;
            }

            for (int i = 0; i < _cachedHudPills.Length; i++)
            {
                _cachedHudPills[i]?.Dispose();
                _cachedHudPills[i] = null;
            }

            _cachedHoverPill?.Dispose();
            _cachedHoverPill = null;

            _cachedToastPill?.Dispose();
            _cachedToastPill = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            InvalidateCaches();
        }

        /// <summary>
        /// Computes the exact unclamped screen pixel dimensions of the seal ink imprint and the
        /// enclosing inner aperture radius and outer ring radius of the aiming reticle.
        /// Always uses the bounding half-diagonal of (inkWidthPx, inkHeightPx) so square chops (aspect 1:1),
        /// wide signatures, and tall rectangular chops have all four corners guaranteed inside the open aperture.
        /// </summary>
        public void GetReticleRadiiPx(
            double physicalSizeMm,
            double monitorDpi,
            double documentZoomPercent,
            out float inkWidthPx,
            out float inkHeightPx,
            out float innerApertureRadiusPx,
            out float outerRingRadiusPx,
            double inkAspectRatio = 1.0)
        {
            if (inkAspectRatio <= 0.02 || double.IsNaN(inkAspectRatio))
                inkAspectRatio = 1.0;

            inkWidthPx = (float)CoordinateTransformer.MillimetersToScreenPixels(
                physicalSizeMm,
                monitorDpi,
                documentZoomPercent);

            inkHeightPx = inkWidthPx * (float)inkAspectRatio;

            // Always use the bounding half-diagonal so square chops (1:1) as well as wide/tall rectangular seals
            // never have their corners clipped by the circular aperture at any rotation angle.
            float sealEnvelopeRadiusPx = 0.5f * (float)Math.Sqrt(
                (double)inkWidthPx * inkWidthPx + (double)inkHeightPx * inkHeightPx);

            innerApertureRadiusPx = Math.Max(38f, sealEnvelopeRadiusPx + 14f);

            SpriteMetricAsset retSprite = _assets.ReticleSprite;
            if (retSprite != null && retSprite.InnerApertureRadiusPx > 10f && retSprite.OuterRadiusPx > retSprite.InnerApertureRadiusPx)
            {
                float ratio = retSprite.OuterRadiusPx / retSprite.InnerApertureRadiusPx;
                outerRingRadiusPx = innerApertureRadiusPx * ratio;
            }
            else
            {
                outerRingRadiusPx = innerApertureRadiusPx * 1.19f;
            }
        }

        /// <summary>
        /// Overload preserving backward compatibility for square/circular callers.
        /// </summary>
        public void GetReticleRadiiPx(
            double physicalSizeMm,
            double monitorDpi,
            double documentZoomPercent,
            out float inkWidthPx,
            out float innerApertureRadiusPx,
            out float outerRingRadiusPx,
            double inkAspectRatio = 1.0)
        {
            GetReticleRadiiPx(
                physicalSizeMm,
                monitorDpi,
                documentZoomPercent,
                out inkWidthPx,
                out _,
                out innerApertureRadiusPx,
                out outerRingRadiusPx,
                inkAspectRatio);
        }

        /// <summary>
        /// Computes the unclamped square canvas dimension in pixels required to fit the reticle,
        /// seal ghost, shockwave ripple, and HUD pill without clipping.
        /// </summary>
        public int ComputeUnclampedRequiredCanvasSize(
            double physicalSizeMm,
            double inkAspectRatio,
            double monitorDpi,
            double documentZoomPercent)
        {
            float dpiScale = (float)(Math.Max(72.0, monitorDpi) / 96.0);
            GetReticleRadiiPx(
                physicalSizeMm,
                monitorDpi,
                documentZoomPercent,
                out _,
                out _,
                out _,
                out float outerRingR,
                inkAspectRatio);

            float requiredRadius = Math.Max(220f * dpiScale, outerRingR * 1.36f + 64f * dpiScale);
            int rawDiameter = (int)Math.Ceiling(requiredRadius * 2.0f);
            return (Math.Max(460, rawDiameter) + 31) & ~31;
        }

        /// <summary>
        /// Checks whether the preview extent for the given physical size, aspect ratio, DPI, and zoom
        /// fits within the 2400px maximum overlay canvas limit. If false, returns a concise zoom-out message
        /// so callers can block stamping without silently shrinking the ink preview.
        /// </summary>
        public bool IsPreviewExtentSupported(
            double physicalSizeMm,
            double inkAspectRatio,
            double monitorDpi,
            double documentZoomPercent,
            out int unclampedCanvasSizePx,
            out string unsupportedMessage)
        {
            unclampedCanvasSizePx = ComputeUnclampedRequiredCanvasSize(
                physicalSizeMm,
                inkAspectRatio,
                monitorDpi,
                documentZoomPercent);

            if (unclampedCanvasSizePx > MaxSupportedCanvasSizePx)
            {
                unsupportedMessage = "请缩小 Word 视图比例或减小印章尺寸后再盖章";
                return false;
            }

            unsupportedMessage = null;
            return true;
        }

        /// <summary>
        /// Computes the allocated square canvas dimension in pixels (capped at MaxSupportedCanvasSizePx).
        /// </summary>
        public int ComputeRequiredCanvasSize(
            double physicalSizeMm,
            double inkAspectRatio,
            double monitorDpi,
            double documentZoomPercent)
        {
            int unclamped = ComputeUnclampedRequiredCanvasSize(
                physicalSizeMm,
                inkAspectRatio,
                monitorDpi,
                documentZoomPercent);
            return Math.Min(MaxSupportedCanvasSizePx, unclamped);
        }

        /// <summary>
        /// Hit-tests whether a screen point lies within the upright stamp's physical silhouette in Idle mode
        /// (never using the raw padded image canvas rectangle).
        /// </summary>
        public bool IsPointOverIdleBody(Vector2D screenPoint, Vector2D parkedCenter, double monitorDpi)
        {
            float dpiScale = (float)(Math.Max(72.0, monitorDpi) / 96.0);
            float bodyH = BaseIdleBodyHeightPxAt96Dpi * dpiScale;
            float bodyW = bodyH * 0.94f;

            double dx = screenPoint.X - parkedCenter.X;
            double dy = screenPoint.Y - parkedCenter.Y;

            // Normalized coordinates relative to occupied body center: x in [-0.5, 0.5], y in [-0.5, 0.5]
            double nx = dx / bodyW;
            double ny = dy / bodyH;

            if (ny < -0.52 || ny > 0.52 || Math.Abs(nx) > 0.52)
                return false;

            // Upper rounded cap region: y in [-0.50, -0.15], width ~ 0.72 of base
            if (ny < -0.15)
            {
                return Math.Abs(nx) <= 0.37;
            }
            // Narrow cylindrical grip neck: y in [-0.15, +0.16], width ~ 0.48 of base
            if (ny < 0.16)
            {
                return Math.Abs(nx) <= 0.26;
            }
            // Wide lower base puck: y in [+0.16, +0.50], full width
            return Math.Abs(nx) <= 0.50;
        }

        /// <summary>
        /// Hit-tests whether a screen point lies strictly on the outer metal annulus of the aiming reticle
        /// (preserving the transparent center aperture as a true open hole).
        /// </summary>
        public bool IsPointOnOuterReticleRing(
            Vector2D screenPoint,
            Vector2D reticleCenter,
            double physicalSizeMm,
            double monitorDpi,
            double documentZoomPercent,
            double inkAspectRatio = 1.0)
        {
            GetReticleRadiiPx(
                physicalSizeMm,
                monitorDpi,
                documentZoomPercent,
                out _,
                out _,
                out float innerR,
                out float outerR,
                inkAspectRatio);

            double dist = Vector2D.Distance(screenPoint, reticleCenter);
            return dist >= (innerR - 4f) && dist <= (outerR + 8f);
        }

        /// <summary>
        /// Renders the complete per-pixel alpha frame for the current state machine state.
        /// Center of the canvas corresponds to <paramref name="stateMachine"/>.CurrentPosition.
        /// </summary>
        public void RenderFrame(
            Graphics g,
            int canvasWidth,
            int canvasHeight,
            StampStateMachine stateMachine,
            SealAsset activeSeal,
            StampTargetProbe targetProbe,
            double monitorDpi,
            string transientToastMessage = null,
            double transientToastAlpha = 0.0)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float cx = canvasWidth * 0.5f;
            float cy = canvasHeight * 0.5f;
            float dpiScale = (float)(Math.Max(72.0, monitorDpi) / 96.0);

            double morph = stateMachine.MorphProgress;
            double zComp = stateMachine.ZCompression;
            double aspect = activeSeal != null ? activeSeal.InkAspectRatio : 1.0;

            // 1. Single-pose handoff without opacity dropout:
            // - morph < 0.72: upright/lifted solid body (opacity stays in [0.84, 1.0] so it never vanishes at handoff)
            // - morph >= 0.72: top-down aiming reticle (opacity ramps from 0.86 -> 1.0, or 1.0 in Aiming/Stamping)
            if (morph < 0.72)
            {
                float bodyOpacity = morph < 0.56
                    ? 1.0f
                    : (float)(1.0 - 0.16 * Math.Max(0.0, Math.Min(1.0, (morph - 0.56) / 0.16)));

                RenderSolidBodyPose(
                    g,
                    cx,
                    cy + (float)stateMachine.DropBounceOffsetPx,
                    dpiScale,
                    stateMachine,
                    activeSeal,
                    bodyOpacity);
            }
            else
            {
                float reticleAlpha = (float)(0.86 + 0.14 * Math.Max(0.0, Math.Min(1.0, (morph - 0.72) / 0.16)));
                if (stateMachine.CurrentState == StampState.Aiming && morph >= 0.995 ||
                    stateMachine.CurrentState == StampState.Stamping)
                {
                    reticleAlpha = 1.0f;
                }

                double zoomPercent = (targetProbe != null && targetProbe.IsValidTarget && targetProbe.DocumentZoomPercent > 10)
                    ? targetProbe.DocumentZoomPercent
                    : 100.0;

                RenderAimingReticleAndGhost(
                    g,
                    cx,
                    cy,
                    dpiScale,
                    stateMachine,
                    activeSeal,
                    targetProbe,
                    monitorDpi,
                    zoomPercent,
                    reticleAlpha,
                    zComp);
            }

            // 3. Render Post-Impact Air Shockwave Ripple if active
            if (stateMachine.ImpactRippleProgress < 1.0)
            {
                double zoomPercent = (targetProbe != null && targetProbe.IsValidTarget && targetProbe.DocumentZoomPercent > 10)
                    ? targetProbe.DocumentZoomPercent
                    : 100.0;

                GetReticleRadiiPx(
                    stateMachine.PhysicalSizeMm,
                    monitorDpi,
                    zoomPercent,
                    out _,
                    out _,
                    out _,
                    out float outerR,
                    aspect);

                RenderImpactRipple(g, cx, cy, outerR, (float)stateMachine.ImpactRippleProgress);
            }

            // 4. Render Transient Toast Message (e.g. seal loaded or unsupported file warning)
            if (!string.IsNullOrWhiteSpace(transientToastMessage) && transientToastAlpha > 0.02)
            {
                RenderToastBanner(g, cx, cy - 115f * dpiScale, dpiScale, transientToastMessage, (float)transientToastAlpha);
            }
        }

        private void RenderSolidBodyPose(
            Graphics g,
            float cx,
            float cy,
            float dpiScale,
            StampStateMachine sm,
            SealAsset activeSeal,
            float masterOpacity)
        {
            float targetBodyHeight = BaseIdleBodyHeightPxAt96Dpi * dpiScale;
            float pressScale = (float)sm.PressScale;
            double morph = sm.MorphProgress;

            // Shadow & base contact position
            float baseBottomY = cy + targetBodyHeight * 0.48f;
            float baseWidth = targetBodyHeight * 0.94f;

            // Lift height increases with morph
            float liftOffsetPx = (float)(morph * 22.0 * dpiScale);
            float shadowSpread = 1.0f + (float)morph * 0.28f;
            int shadowAlpha = (int)Math.Round(135 * (1.0 - morph * 0.65) * masterOpacity);

            if (shadowAlpha > 4)
            {
                DrawSoftEllipticalShadow(
                    g,
                    cx,
                    baseBottomY + 4f * dpiScale,
                    baseWidth * 0.92f * shadowSpread,
                    baseWidth * 0.16f * shadowSpread,
                    shadowAlpha);
            }

            // Dynamic crimson base contact glow (breathes on hover in Idle, flashes on drop/return, fades when lifted)
            double breath = 0.5 + 0.5 * Math.Sin(sm.HoverBreathPhase);
            double idleGlowStrength = 0.22 + sm.HoverProgress * (0.38 + 0.40 * breath) + sm.CrimsonFlashIntensity * 0.75;
            idleGlowStrength *= Math.Max(0.0, 1.0 - morph * 1.4) * masterOpacity;
            idleGlowStrength = Math.Max(0.0, Math.Min(1.0, idleGlowStrength));

            if (idleGlowStrength > 0.02)
            {
                int glowAlpha = (int)Math.Round(idleGlowStrength * 165);
                DrawCrimsonBaseGlow(
                    g,
                    cx,
                    baseBottomY - 5f * dpiScale,
                    baseWidth * 0.88f,
                    baseWidth * 0.18f,
                    glowAlpha);
            }

            // Render a single readable rigid body per frame (never crossfading two incompatible silhouettes at once):
            // - morph < 0.08: upright IdleSprite
            // - morph >= 0.08: lifted/tilted DragSprite with subtle vertical foreshortening as it approaches top-down handoff
            bool useLiftedDragPose = morph >= 0.08;
            float verticalSquash = useLiftedDragPose
                ? 1.0f - (float)Math.Max(0.0, Math.Min(0.14, (morph - 0.22) * 0.26))
                : 1.0f;

            GraphicsState state = g.Save();
            g.TranslateTransform(cx, cy - liftOffsetPx);
            if (Math.Abs(sm.TiltAngleDeg) > 0.05)
            {
                g.RotateTransform((float)sm.TiltAngleDeg);
            }
            if (Math.Abs(verticalSquash - 1.0f) > 0.005f || Math.Abs(pressScale - 1.0f) > 0.002f)
            {
                g.ScaleTransform(pressScale, verticalSquash * pressScale);
            }

            if (!useLiftedDragPose)
            {
                if (_assets.IdleSprite?.Bitmap != null)
                {
                    DrawCachedNormalizedSprite(g, ref _cachedIdleSprite, _assets.IdleSprite, 0f, 0f, targetBodyHeight, masterOpacity);
                }
                else
                {
                    DrawProceduralStampBody(g, 0f, 0f, targetBodyHeight, isTilted: false, masterOpacity);
                }
            }
            else
            {
                SpriteMetricAsset liftedSprite = _assets.DragSprite ?? _assets.IdleSprite;
                if (liftedSprite?.Bitmap != null)
                {
                    DrawCachedNormalizedSprite(g, ref _cachedDragSprite, liftedSprite, 0f, 0f, targetBodyHeight * 1.02f, masterOpacity);
                }
                else
                {
                    DrawProceduralStampBody(g, 0f, 0f, targetBodyHeight * 1.02f, isTilted: true, masterOpacity);
                }
            }

            // Extra hover crimson seam highlight on the idle body
            if (idleGlowStrength > 0.28 && morph < 0.2)
            {
                float seamY = targetBodyHeight * 0.40f;
                float seamW = baseWidth * 0.82f;
                int seamA = (int)Math.Round((idleGlowStrength - 0.22) * 180 * masterOpacity);
                seamA = Math.Max(0, Math.Min(210, seamA));
                using (var seamPen = new Pen(Color.FromArgb(seamA, 235, 32, 42), 2.2f * dpiScale))
                {
                    g.DrawArc(seamPen, -seamW * 0.5f, seamY - 5f * dpiScale, seamW, 11f * dpiScale, 8f, 164f);
                }
            }

            g.Restore(state);

            // Hover active seal name label in tiny sans-serif text
            if (sm.HoverProgress > 0.03 && activeSeal != null && morph < 0.15)
            {
                float labelAlpha = (float)(sm.HoverProgress * (1.0 - morph / 0.15) * masterOpacity);
                RenderHoverSealNameLabel(
                    g,
                    cx,
                    cy - targetBodyHeight * 0.56f,
                    dpiScale,
                    $"{activeSeal.Name} · {sm.PhysicalSizeMm:F0}mm",
                    labelAlpha);
            }
        }

        private void RenderAimingReticleAndGhost(
            Graphics g,
            float cx,
            float cy,
            float dpiScale,
            StampStateMachine sm,
            SealAsset activeSeal,
            StampTargetProbe targetProbe,
            double monitorDpi,
            double documentZoomPercent,
            float reticleAlpha,
            double zCompression)
        {
            double inkAspect = activeSeal != null ? activeSeal.InkAspectRatio : 1.0;

            GetReticleRadiiPx(
                sm.PhysicalSizeMm,
                monitorDpi,
                documentZoomPercent,
                out float inkWidthPx,
                out float inkHeightPx,
                out float innerApertureR,
                out float outerRingR,
                inkAspect);

            // During Stamping Z-compression, scale down slightly (1.0 -> 0.92) to convey fast downward plunge
            float compressionScale = 1.0f - (float)(zCompression * 0.085);
            float effectiveInnerR = innerApertureR * compressionScale;
            float effectiveOuterR = outerRingR * compressionScale;

            // Display angle: BaseRotationDeg during Aiming, EffectiveImpressionAngleDeg during Stamping
            float displayAngleDeg = sm.CurrentState == StampState.Stamping
                ? (float)sm.EffectiveImpressionAngleDeg
                : (float)sm.BaseRotationDeg;

            // 1. Tight contact shadow around outer ring during Stamping plunge
            if (zCompression > 0.05)
            {
                int plungeShadowAlpha = (int)Math.Round(zCompression * 82 * reticleAlpha);
                using (var shadowPen = new Pen(Color.FromArgb(plungeShadowAlpha, 10, 10, 12), 2.2f * dpiScale))
                {
                    g.DrawEllipse(
                        shadowPen,
                        cx - effectiveOuterR - 1f,
                        cy - effectiveOuterR - 1f,
                        (effectiveOuterR + 1f) * 2f,
                        (effectiveOuterR + 1f) * 2f);
                }
            }

            // 2, 3 & 4. Cached Rotated Aiming Composite (Seal Ghost + Outer Optical Ring + 72 Metrological Ticks)
            // Keyed by uncompressed radii/opacity so Stamping Z-compression and Returning fade reuse the cached bitmap!
            float baseCanvasW = 0f;
            float baseCanvasH = 0f;
            float baseGhostOpacity = 0f;
            if (activeSeal?.MasterBitmap != null)
            {
                baseCanvasW = inkWidthPx / (float)Math.Max(0.05, activeSeal.InkToCanvasWidthRatio);
                baseCanvasH = inkHeightPx / (float)Math.Max(0.05, activeSeal.InkToCanvasHeightRatio);
                baseGhostOpacity = 0.50f;
            }

            float baseRingOpacity = 0.82f;
            DrawCachedAimingComposite(
                g,
                cx,
                cy,
                activeSeal?.MasterBitmap,
                innerApertureR,
                outerRingR,
                baseCanvasW,
                baseCanvasH,
                baseGhostOpacity,
                baseRingOpacity,
                displayAngleDeg,
                compressionScale,
                reticleAlpha);

            // 5. Fixed 12-o'clock reference index marker just outside the outer ring
            int indexAlpha = (int)Math.Round(210 * reticleAlpha);
            if (indexAlpha > 10)
            {
                float markerTop = cy - effectiveOuterR - 6f * dpiScale;
                using (var indexPen = new Pen(Color.FromArgb(indexAlpha, 224, 36, 44), 2.0f * dpiScale))
                {
                    g.DrawLine(indexPen, cx, markerTop - 5f * dpiScale, cx, markerTop + 2f * dpiScale);
                }
            }

            // 6. Subtle non-intrusive HUD readout below the outer ring (hidden during brief impact/ripple so ripple never crosses HUD)
            bool showHud = reticleAlpha > 0.4f &&
                           sm.CurrentState != StampState.Stamping &&
                           sm.ImpactRippleProgress >= 0.92;
            if (showHud)
            {
                bool extentSupported = IsPreviewExtentSupported(
                    sm.PhysicalSizeMm,
                    inkAspect,
                    monitorDpi,
                    documentZoomPercent,
                    out _,
                    out string oversizeMessage);

                bool isValidWordTarget = extentSupported && targetProbe != null && targetProbe.IsValidTarget;
                string lockTag = sm.SizeLock != SizeLockMode.Unlocked ? " · 已锁定" : "";
                string targetTag;
                if (!extentSupported)
                {
                    targetTag = oversizeMessage;
                }
                else if (isValidWordTarget)
                {
                    targetTag = $"第 {targetProbe.PageNumber} 页";
                }
                else if (targetProbe != null && !string.IsNullOrWhiteSpace(targetProbe.StatusSummary))
                {
                    targetTag = targetProbe.StatusSummary;
                }
                else
                {
                    targetTag = "请移到 Word 文档页面";
                }

                string hudText = $"{sm.PhysicalSizeMm:F0}mm{lockTag} · {displayAngleDeg:+0.0;-0.0;0.0}° · {targetTag}";

                float hudY = Math.Min(cy * 2f - 22f * dpiScale, cy + effectiveOuterR + 14f * dpiScale);
                RenderReticleHudPill(
                    g,
                    cx,
                    hudY,
                    dpiScale,
                    hudText,
                    isValidWordTarget,
                    reticleAlpha * 0.88f);
            }
        }

        private void DrawCachedNormalizedSprite(
            Graphics g,
            ref CachedSpriteLayer cacheSlot,
            SpriteMetricAsset sprite,
            float centerX,
            float centerY,
            float targetOccupiedHeightPx,
            float opacity)
        {
            if (sprite?.Bitmap == null || opacity <= 0.01f)
                return;

            int heightKey = Math.Max(8, (int)Math.Round(targetOccupiedHeightPx));

            if (cacheSlot == null ||
                !ReferenceEquals(cacheSlot.SourceAsset, sprite) ||
                cacheSlot.TargetOccupiedHeightKey != heightKey ||
                cacheSlot.Bitmap == null)
            {
                SpriteCacheMisses++;
                cacheSlot?.Dispose();
                cacheSlot = BuildCachedSpriteLayer(sprite, heightKey, 255);
            }
            else
            {
                SpriteCacheHits++;
            }

            var destRect = new RectangleF(
                centerX - cacheSlot.AnchorOffsetX,
                centerY - cacheSlot.AnchorOffsetY,
                cacheSlot.DrawWidth,
                cacheSlot.DrawHeight);

            DrawBitmapWithOpacity(g, cacheSlot.Bitmap, destRect, opacity);
        }

        private static CachedSpriteLayer BuildCachedSpriteLayer(SpriteMetricAsset sprite, int heightKey, int opacityByte)
        {
            Rectangle occ = sprite.OccupiedBounds;
            int pad = 4;
            int cropX = Math.Max(0, occ.X - pad);
            int cropY = Math.Max(0, occ.Y - pad);
            int cropRight = Math.Min(sprite.Bitmap.Width, occ.Right + pad);
            int cropBottom = Math.Min(sprite.Bitmap.Height, occ.Bottom + pad);
            int cropW = Math.Max(1, cropRight - cropX);
            int cropH = Math.Max(1, cropBottom - cropY);

            float scale = heightKey / Math.Max(1f, (float)occ.Height);
            int scaledW = Math.Max(4, (int)Math.Ceiling(cropW * scale));
            int scaledH = Math.Max(4, (int)Math.Ceiling(cropH * scale));

            PointF occCenter = sprite.OccupiedCenter;
            float anchorOffsetX = (occCenter.X - cropX) * scale;
            float anchorOffsetY = (occCenter.Y - cropY) * scale;

            var bmp = new Bitmap(scaledW, scaledH, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(bmp))
            {
                cg.Clear(Color.Transparent);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;

                var destRect = new RectangleF(0, 0, scaledW, scaledH);
                var srcRect = new RectangleF(cropX, cropY, cropW, cropH);
                float alpha = opacityByte / 255f;

                if (opacityByte >= 254)
                {
                    cg.DrawImage(sprite.Bitmap, destRect, srcRect, GraphicsUnit.Pixel);
                }
                else
                {
                    var matrix = new ColorMatrix
                    {
                        Matrix00 = 1f,
                        Matrix11 = 1f,
                        Matrix22 = 1f,
                        Matrix33 = alpha,
                        Matrix44 = 1f
                    };
                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                        PointF[] destPoints =
                        {
                            new PointF(0, 0),
                            new PointF(scaledW, 0),
                            new PointF(0, scaledH)
                        };
                        cg.DrawImage(sprite.Bitmap, destPoints, srcRect, GraphicsUnit.Pixel, attrs);
                    }
                }
            }

            return new CachedSpriteLayer
            {
                SourceAsset = sprite,
                TargetOccupiedHeightKey = heightKey,
                OpacityByte = opacityByte,
                Bitmap = bmp,
                AnchorOffsetX = anchorOffsetX,
                AnchorOffsetY = anchorOffsetY,
                DrawWidth = scaledW,
                DrawHeight = scaledH
            };
        }

        private void DrawCachedSealGhost(Graphics g, Bitmap masterBitmap, float canvasW, float canvasH, float ghostOpacity)
        {
            int wKey = Math.Max(4, (int)Math.Round(canvasW));
            int hKey = Math.Max(4, (int)Math.Round(canvasH));
            int opacityByte = QuantizeAlphaByte(ghostOpacity);
            if (opacityByte <= 2)
                return;

            if (_cachedSealGhost == null ||
                !ReferenceEquals(_cachedSealGhost.SourceMasterBitmap, masterBitmap) ||
                _cachedSealGhost.WidthPx != wKey ||
                _cachedSealGhost.HeightPx != hKey ||
                _cachedSealGhost.OpacityByte != opacityByte ||
                _cachedSealGhost.Bitmap == null)
            {
                GhostCacheMisses++;
                _cachedSealGhost?.Dispose();
                _cachedSealGhost = BuildCachedSealGhost(masterBitmap, wKey, hKey, opacityByte);
            }
            else
            {
                GhostCacheHits++;
            }

            g.DrawImage(
                _cachedSealGhost.Bitmap,
                -canvasW * 0.5f,
                -canvasH * 0.5f,
                canvasW,
                canvasH);
        }

        private static CachedSealGhost BuildCachedSealGhost(Bitmap masterBitmap, int widthPx, int heightPx, int opacityByte)
        {
            var bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(bmp))
            {
                cg.Clear(Color.Transparent);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;

                DrawBitmapWithOpacity(
                    cg,
                    masterBitmap,
                    new RectangleF(0, 0, widthPx, heightPx),
                    opacityByte / 255f);
            }

            return new CachedSealGhost
            {
                SourceMasterBitmap = masterBitmap,
                WidthPx = widthPx,
                HeightPx = heightPx,
                OpacityByte = opacityByte,
                Bitmap = bmp
            };
        }

        private void DrawCachedReticleAnnulus(Graphics g, float effectiveInnerR, float effectiveOuterR, float ringOpacity)
        {
            int innerKey = Math.Max(10, (int)Math.Round(effectiveInnerR * 2f));
            int outerKey = Math.Max(innerKey + 4, (int)Math.Round(effectiveOuterR * 2f));
            int opacityByte = QuantizeAlphaByte(ringOpacity);
            if (opacityByte <= 2)
                return;

            if (_cachedReticleAnnulus == null ||
                _cachedReticleAnnulus.InnerRadiusKey != innerKey ||
                _cachedReticleAnnulus.OuterRadiusKey != outerKey ||
                _cachedReticleAnnulus.OpacityByte != opacityByte ||
                _cachedReticleAnnulus.Bitmap == null)
            {
                ReticleCacheMisses++;
                _cachedReticleAnnulus?.Dispose();
                _cachedReticleAnnulus = BuildCachedReticleAnnulus(
                    innerKey * 0.5f,
                    outerKey * 0.5f,
                    innerKey,
                    outerKey,
                    opacityByte);
            }
            else
            {
                ReticleCacheHits++;
            }

            float half = _cachedReticleAnnulus.HalfSizePx;
            g.DrawImage(_cachedReticleAnnulus.Bitmap, -half, -half, half * 2f, half * 2f);
        }

        private CachedReticleAnnulus BuildCachedReticleAnnulus(
            float effectiveInnerR,
            float effectiveOuterR,
            int innerKey,
            int outerKey,
            int opacityByte)
        {
            int halfInt = (int)Math.Ceiling(effectiveOuterR) + 6;
            int size = halfInt * 2;
            float ringOpacity = opacityByte / 255f;

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(bmp))
            {
                cg.Clear(Color.Transparent);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.TranslateTransform(halfInt, halfInt);

                if (_assets.ReticleSprite?.Bitmap != null && _assets.ReticleSprite.OuterRadiusPx > 10f)
                {
                    SpriteMetricAsset ret = _assets.ReticleSprite;
                    float scale = effectiveOuterR / ret.OuterRadiusPx;
                    float drawW = ret.Bitmap.Width * scale;
                    float drawH = ret.Bitmap.Height * scale;
                    PointF occCenter = ret.OccupiedCenter;
                    var ringDestRect = new RectangleF(-occCenter.X * scale, -occCenter.Y * scale, drawW, drawH);

                    using (var apertureHolePath = new GraphicsPath())
                    {
                        float clipHoleR = Math.Max(1f, effectiveInnerR - 1f);
                        apertureHolePath.AddEllipse(-clipHoleR, -clipHoleR, clipHoleR * 2f, clipHoleR * 2f);
                        using (var annularClip = new Region(ringDestRect))
                        {
                            annularClip.Exclude(apertureHolePath);
                            Region previousClip = cg.Clip;
                            cg.Clip = annularClip;
                            DrawBitmapWithOpacity(cg, ret.Bitmap, ringDestRect, ringOpacity);
                            cg.Clip = previousClip;
                        }
                    }
                }
                else
                {
                    DrawProceduralReticleRing(cg, effectiveInnerR, effectiveOuterR, ringOpacity);
                }

                DrawMetrologicalAnnulusTicks(cg, effectiveInnerR, effectiveOuterR, ringOpacity);
            }

            return new CachedReticleAnnulus
            {
                InnerRadiusKey = innerKey,
                OuterRadiusKey = outerKey,
                OpacityByte = opacityByte,
                Bitmap = bmp,
                HalfSizePx = halfInt
            };
        }

        private void DrawCachedAimingComposite(
            Graphics g,
            float cx,
            float cy,
            Bitmap masterBitmap,
            float effectiveInnerR,
            float effectiveOuterR,
            float canvasW,
            float canvasH,
            float ghostOpacity,
            float ringOpacity,
            float displayAngleDeg,
            float compressionScale,
            float masterAlpha)
        {
            int innerKey = Math.Max(10, (int)Math.Round(effectiveInnerR * 2f));
            int outerKey = Math.Max(innerKey + 4, (int)Math.Round(effectiveOuterR * 2f));
            int wKey = masterBitmap != null ? Math.Max(4, (int)Math.Round(canvasW)) : 0;
            int hKey = masterBitmap != null ? Math.Max(4, (int)Math.Round(canvasH)) : 0;
            int ghostAlphaByte = masterBitmap != null ? QuantizeAlphaByte(ghostOpacity) : 0;
            int ringAlphaByte = QuantizeAlphaByte(ringOpacity);
            if (ringAlphaByte <= 2 && ghostAlphaByte <= 2)
                return;

            int angleKey = (int)Math.Round(StampStateMachine.NormalizeAngle(displayAngleDeg) * 20.0);

            CachedAimingComposite found = null;
            for (int i = 0; i < _cachedAimingComposites.Length; i++)
            {
                CachedAimingComposite cand = _cachedAimingComposites[i];
                if (cand != null &&
                    ReferenceEquals(cand.SourceMasterBitmap, masterBitmap) &&
                    cand.InnerRadiusKey == innerKey &&
                    cand.OuterRadiusKey == outerKey &&
                    cand.GhostWidthKey == wKey &&
                    cand.GhostHeightKey == hKey &&
                    cand.GhostOpacityByte == ghostAlphaByte &&
                    cand.RingOpacityByte == ringAlphaByte &&
                    cand.AngleKey == angleKey &&
                    cand.Bitmap != null)
                {
                    found = cand;
                    break;
                }
            }

            if (found != null)
            {
                ReticleCacheHits++;
                if (masterBitmap != null) GhostCacheHits++;
            }
            else
            {
                ReticleCacheMisses++;
                if (masterBitmap != null) GhostCacheMisses++;

                found = BuildCachedAimingComposite(
                    masterBitmap,
                    innerKey * 0.5f,
                    outerKey * 0.5f,
                    canvasW,
                    canvasH,
                    innerKey,
                    outerKey,
                    wKey,
                    hKey,
                    ghostAlphaByte,
                    ringAlphaByte,
                    angleKey,
                    displayAngleDeg);

                int slot = _aimingCompositeWriteIndex;
                _aimingCompositeWriteIndex = (_aimingCompositeWriteIndex + 1) % _cachedAimingComposites.Length;
                _cachedAimingComposites[slot]?.Dispose();
                _cachedAimingComposites[slot] = found;
            }

            float half = found.HalfSizePx * compressionScale;
            var destRect = new RectangleF(cx - half, cy - half, half * 2f, half * 2f);
            DrawBitmapWithOpacity(g, found.Bitmap, destRect, masterAlpha);
        }

        private CachedAimingComposite BuildCachedAimingComposite(
            Bitmap masterBitmap,
            float effectiveInnerR,
            float effectiveOuterR,
            float canvasW,
            float canvasH,
            int innerKey,
            int outerKey,
            int wKey,
            int hKey,
            int ghostAlphaByte,
            int ringAlphaByte,
            int angleKey,
            float displayAngleDeg)
        {
            float ghostHalfDiag = (masterBitmap != null && canvasW > 0 && canvasH > 0)
                ? 0.5f * (float)Math.Sqrt((double)canvasW * canvasW + (double)canvasH * canvasH)
                : 0f;
            float maxRadius = Math.Max(effectiveOuterR, ghostHalfDiag);
            int halfInt = (int)Math.Ceiling(maxRadius) + 6;
            int size = halfInt * 2;

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics cg = Graphics.FromImage(bmp))
            {
                cg.Clear(Color.Transparent);
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                cg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cg.TranslateTransform(halfInt, halfInt);
                if (Math.Abs(displayAngleDeg) > 0.01f)
                {
                    cg.RotateTransform(displayAngleDeg);
                }

                if (masterBitmap != null && ghostAlphaByte > 2 && canvasW > 1f && canvasH > 1f)
                {
                    DrawCachedSealGhost(cg, masterBitmap, canvasW, canvasH, ghostAlphaByte / 255f);
                }

                if (ringAlphaByte > 2)
                {
                    DrawCachedReticleAnnulus(cg, effectiveInnerR, effectiveOuterR, ringAlphaByte / 255f);
                }
            }

            return new CachedAimingComposite
            {
                SourceMasterBitmap = masterBitmap,
                InnerRadiusKey = innerKey,
                OuterRadiusKey = outerKey,
                GhostWidthKey = wKey,
                GhostHeightKey = hKey,
                GhostOpacityByte = ghostAlphaByte,
                RingOpacityByte = ringAlphaByte,
                AngleKey = angleKey,
                Bitmap = bmp,
                HalfSizePx = halfInt
            };
        }

        private static int QuantizeAlphaByte(float opacity)
        {
            int raw = (int)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 255f);
            if (raw >= 252) return 255;
            if (raw <= 2) return 0;
            return (raw + 2) & ~3;
        }

        private static void DrawMetrologicalAnnulusTicks(
            Graphics g,
            float innerR,
            float outerR,
            float opacity)
        {
            float bandWidth = Math.Max(4f, outerR - innerR);
            float tickStartR = innerR + bandWidth * 0.08f;

            int minorAlpha = (int)Math.Round(130 * opacity);
            int majorAlpha = (int)Math.Round(205 * opacity);
            int cardinalAlpha = (int)Math.Round(235 * opacity);

            using (var minorPen = new Pen(Color.FromArgb(minorAlpha, 235, 238, 244), 1.0f))
            using (var majorPen = new Pen(Color.FromArgb(majorAlpha, 245, 247, 252), 1.4f))
            using (var cardinalPen = new Pen(Color.FromArgb(cardinalAlpha, 228, 32, 42), 2.2f))
            {
                for (int deg = 0; deg < 360; deg += 5)
                {
                    double rad = (deg - 90) * Math.PI / 180.0;
                    float cos = (float)Math.Cos(rad);
                    float sin = (float)Math.Sin(rad);

                    float tickEndR;
                    Pen pen;
                    if (deg % 90 == 0)
                    {
                        tickEndR = innerR + bandWidth * 0.56f;
                        pen = cardinalPen;
                    }
                    else if (deg % 15 == 0)
                    {
                        tickEndR = innerR + bandWidth * 0.38f;
                        pen = majorPen;
                    }
                    else
                    {
                        tickEndR = innerR + bandWidth * 0.22f;
                        pen = minorPen;
                    }

                    g.DrawLine(
                        pen,
                        cos * tickStartR,
                        sin * tickStartR,
                        cos * tickEndR,
                        sin * tickEndR);
                }
            }
        }

        private static void RenderImpactRipple(Graphics g, float cx, float cy, float baseOuterR, float rippleProgress)
        {
            if (rippleProgress <= 0f || rippleProgress >= 1f)
                return;

            float eased = 1f - (1f - rippleProgress) * (1f - rippleProgress);
            float rAir = baseOuterR * (1.01f + eased * 0.11f);
            float rSeam = baseOuterR * (0.99f + eased * 0.05f);

            int aSeam = (int)Math.Round((1f - rippleProgress) * 52);
            int aAir = (int)Math.Round((1f - rippleProgress) * 44);

            if (aSeam > 3)
            {
                using (var crimsonPen = new Pen(Color.FromArgb(aSeam, 198, 32, 40), 1.0f))
                {
                    g.DrawEllipse(crimsonPen, cx - rSeam, cy - rSeam, rSeam * 2f, rSeam * 2f);
                }
            }

            if (aAir > 3)
            {
                using (var airPen = new Pen(Color.FromArgb(aAir, 215, 220, 228), 0.85f))
                {
                    g.DrawEllipse(airPen, cx - rAir, cy - rAir, rAir * 2f, rAir * 2f);
                }
            }
        }

        private static void DrawBitmapWithOpacity(Graphics g, Bitmap bmp, RectangleF destRect, float opacity)
        {
            opacity = Math.Max(0f, Math.Min(1f, opacity));
            if (opacity <= 0.005f)
                return;

            if (opacity >= 0.995f)
            {
                g.DrawImage(bmp, destRect);
                return;
            }

            var matrix = new ColorMatrix
            {
                Matrix00 = 1f,
                Matrix11 = 1f,
                Matrix22 = 1f,
                Matrix33 = opacity,
                Matrix44 = 1f
            };

            using (var attrs = new ImageAttributes())
            {
                attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                PointF[] destPoints = new[]
                {
                    new PointF(destRect.Left, destRect.Top),
                    new PointF(destRect.Right, destRect.Top),
                    new PointF(destRect.Left, destRect.Bottom)
                };

                g.DrawImage(
                    bmp,
                    destPoints,
                    new RectangleF(0, 0, bmp.Width, bmp.Height),
                    GraphicsUnit.Pixel,
                    attrs);
            }
        }

        private static void DrawSoftEllipticalShadow(Graphics g, float cx, float cy, float width, float height, int maxAlpha)
        {
            if (width <= 2f || height <= 2f || maxAlpha <= 2)
                return;

            for (int i = 4; i >= 1; i--)
            {
                float scale = 0.65f + i * 0.12f;
                float w = width * scale;
                float h = height * scale;
                int a = Math.Max(0, Math.Min(255, maxAlpha / (i + 1)));
                using (var brush = new SolidBrush(Color.FromArgb(a, 6, 7, 10)))
                {
                    g.FillEllipse(brush, cx - w * 0.5f, cy - h * 0.5f, w, h);
                }
            }
        }

        private static void DrawCrimsonBaseGlow(Graphics g, float cx, float cy, float width, float height, int maxAlpha)
        {
            if (width <= 2f || height <= 2f || maxAlpha <= 2)
                return;

            for (int i = 3; i >= 1; i--)
            {
                float scale = 0.72f + i * 0.14f;
                float w = width * scale;
                float h = height * scale;
                int a = Math.Max(0, Math.Min(255, maxAlpha / i));
                using (var brush = new SolidBrush(Color.FromArgb(a, 210, 22, 32)))
                {
                    g.FillEllipse(brush, cx - w * 0.5f, cy - h * 0.5f, w, h);
                }
            }
        }

        private void RenderHoverSealNameLabel(
            Graphics g,
            float cx,
            float topY,
            float dpiScale,
            string text,
            float alpha)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 235);
            if (a < 8) return;
            a = (a + 3) & ~7;
            if (a > 235) a = 235;

            int dpiKey = (int)Math.Round(dpiScale * 100f);
            if (_cachedHoverPill == null ||
                _cachedHoverPill.Text != text ||
                _cachedHoverPill.DpiKey != dpiKey ||
                _cachedHoverPill.AlphaByte != a ||
                _cachedHoverPill.Bitmap == null)
            {
                PillCacheMisses++;
                _cachedHoverPill?.Dispose();
                _cachedHoverPill = BuildCachedPillBitmap(
                    text,
                    8.5f * dpiScale,
                    10f * dpiScale,
                    4.5f * dpiScale,
                    maxWidthPx: 0f,
                    cornerRadiusPx: 0f,
                    Color.FromArgb((int)(a * 0.88f), 20, 21, 25),
                    Color.FromArgb((int)(a * 0.65f), 160, 36, 42),
                    1f,
                    Color.FromArgb(a, 238, 240, 245),
                    dpiKey,
                    styleKey: 1,
                    alphaByte: a);
            }
            else
            {
                PillCacheHits++;
            }

            Bitmap bmp = _cachedHoverPill.Bitmap;
            g.DrawImage(bmp, cx - bmp.Width * 0.5f, topY - bmp.Height * 0.5f, bmp.Width, bmp.Height);
        }

        private void RenderReticleHudPill(
            Graphics g,
            float cx,
            float centerY,
            float dpiScale,
            string text,
            bool isValidTarget,
            float alpha)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 225);
            if (a < 10) return;
            a = (a + 3) & ~7;
            if (a > 225) a = 225;

            int dpiKey = (int)Math.Round(dpiScale * 100f);
            int styleKey = isValidTarget ? 1 : 0;

            CachedTextPill found = null;
            for (int i = 0; i < _cachedHudPills.Length; i++)
            {
                CachedTextPill cand = _cachedHudPills[i];
                if (cand != null &&
                    cand.Text == text &&
                    cand.DpiKey == dpiKey &&
                    cand.StyleKey == styleKey &&
                    cand.AlphaByte == a &&
                    cand.Bitmap != null)
                {
                    found = cand;
                    break;
                }
            }

            if (found == null)
            {
                PillCacheMisses++;
                Color borderColor = isValidTarget
                    ? Color.FromArgb((int)(a * 0.75f), 205, 42, 48)
                    : Color.FromArgb((int)(a * 0.55f), 95, 100, 110);

                found = BuildCachedPillBitmap(
                    text,
                    10.5f * dpiScale,
                    9f * dpiScale,
                    3.5f * dpiScale,
                    maxWidthPx: 0f,
                    cornerRadiusPx: 0f,
                    Color.FromArgb((int)(a * 0.82f), 18, 19, 23),
                    borderColor,
                    1f,
                    Color.FromArgb(a, 232, 235, 242),
                    dpiKey,
                    styleKey,
                    a);

                int slot = _hudPillWriteIndex;
                _hudPillWriteIndex = (_hudPillWriteIndex + 1) % _cachedHudPills.Length;
                _cachedHudPills[slot]?.Dispose();
                _cachedHudPills[slot] = found;
            }
            else
            {
                PillCacheHits++;
            }

            Bitmap bmp = found.Bitmap;
            g.DrawImage(bmp, cx - bmp.Width * 0.5f, centerY - bmp.Height * 0.5f, bmp.Width, bmp.Height);
        }

        private void RenderToastBanner(
            Graphics g,
            float cx,
            float centerY,
            float dpiScale,
            string message,
            float alpha)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, alpha)) * 245);
            if (a < 10) return;
            a = (a + 3) & ~7;
            if (a > 245) a = 245;

            int dpiKey = (int)Math.Round(dpiScale * 100f);
            if (_cachedToastPill == null ||
                _cachedToastPill.Text != message ||
                _cachedToastPill.DpiKey != dpiKey ||
                _cachedToastPill.AlphaByte != a ||
                _cachedToastPill.Bitmap == null)
            {
                PillCacheMisses++;
                _cachedToastPill?.Dispose();
                _cachedToastPill = BuildCachedPillBitmap(
                    message,
                    11.5f * dpiScale,
                    12f * dpiScale,
                    5f * dpiScale,
                    maxWidthPx: 380f * dpiScale,
                    cornerRadiusPx: 6f * dpiScale,
                    Color.FromArgb((int)(a * 0.92f), 22, 23, 28),
                    Color.FromArgb((int)(a * 0.85f), 215, 38, 46),
                    1.2f,
                    Color.FromArgb(a, 245, 245, 248),
                    dpiKey,
                    styleKey: 2,
                    alphaByte: a);
            }
            else
            {
                PillCacheHits++;
            }

            Bitmap bmp = _cachedToastPill.Bitmap;
            g.DrawImage(bmp, cx - bmp.Width * 0.5f, centerY - bmp.Height * 0.5f, bmp.Width, bmp.Height);
        }

        private static CachedTextPill BuildCachedPillBitmap(
            string text,
            float fontSizePx,
            float padX,
            float padY,
            float maxWidthPx,
            float cornerRadiusPx,
            Color bgColor,
            Color borderColor,
            float borderWidth,
            Color textColor,
            int dpiKey,
            int styleKey,
            int alphaByte)
        {
            using (var font = new Font("Microsoft YaHei UI", fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF textSize;
                using (var measureBmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
                using (Graphics mg = Graphics.FromImage(measureBmp))
                {
                    textSize = mg.MeasureString(text, font);
                }

                float w = textSize.Width + padX * 2f;
                if (maxWidthPx > 1f)
                {
                    w = Math.Min(maxWidthPx, w);
                }
                float h = textSize.Height + padY * 2f;

                int bmpW = Math.Max(8, (int)Math.Ceiling(w) + 4);
                int bmpH = Math.Max(8, (int)Math.Ceiling(h) + 4);
                var bmp = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppPArgb);

                using (Graphics pg = Graphics.FromImage(bmp))
                {
                    pg.Clear(Color.Transparent);
                    pg.SmoothingMode = SmoothingMode.AntiAlias;
                    pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    var rect = new RectangleF((bmpW - w) * 0.5f, (bmpH - h) * 0.5f, w, h);
                    float radius = cornerRadiusPx > 0.5f ? cornerRadiusPx : h * 0.45f;

                    using (GraphicsPath path = CreateRoundedRectPath(rect, radius))
                    using (var bgBrush = new SolidBrush(bgColor))
                    using (var borderPen = new Pen(borderColor, borderWidth))
                    using (var textBrush = new SolidBrush(textColor))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        pg.FillPath(bgBrush, path);
                        pg.DrawPath(borderPen, path);
                        pg.DrawString(text, font, textBrush, rect, sf);
                    }
                }

                return new CachedTextPill
                {
                    Text = text,
                    DpiKey = dpiKey,
                    StyleKey = styleKey,
                    AlphaByte = alphaByte,
                    Bitmap = bmp
                };
            }
        }

        /// <summary>
        /// Native procedural geometry fallback for the upright / tilted anodized dark aluminum stamp body
        /// if sprite files are absent.
        /// </summary>
        private static void DrawProceduralStampBody(
            Graphics g,
            float cx,
            float cy,
            float bodyHeight,
            bool isTilted,
            float opacity)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 255);
            if (a < 5) return;

            float w = bodyHeight * 0.92f;
            float topY = cy - bodyHeight * 0.48f;
            float bottomY = cy + bodyHeight * 0.48f;

            using (var metalDark = new SolidBrush(Color.FromArgb(a, 26, 27, 31)))
            using (var metalMid = new SolidBrush(Color.FromArgb(a, 42, 44, 50)))
            using (var bevelPen = new Pen(Color.FromArgb((int)(a * 0.65f), 145, 150, 160), 1.4f))
            using (var crimsonPen = new Pen(Color.FromArgb((int)(a * 0.85f), 195, 24, 32), 2.0f))
            {
                // Base puck
                var baseRect = new RectangleF(cx - w * 0.48f, bottomY - bodyHeight * 0.28f, w * 0.96f, bodyHeight * 0.26f);
                g.FillRectangle(metalDark, baseRect);
                g.DrawRectangle(bevelPen, baseRect.X, baseRect.Y, baseRect.Width, baseRect.Height);

                // Neck
                var neckRect = new RectangleF(cx - w * 0.22f, topY + bodyHeight * 0.24f, w * 0.44f, bodyHeight * 0.48f);
                g.FillRectangle(metalMid, neckRect);

                // Cap
                var capRect = new RectangleF(cx - w * 0.36f, topY, w * 0.72f, bodyHeight * 0.28f);
                using (GraphicsPath capPath = CreateRoundedRectPath(capRect, bodyHeight * 0.10f))
                {
                    g.FillPath(metalDark, capPath);
                    g.DrawPath(bevelPen, capPath);
                }

                // Crimson bottom seam
                float seamY = isTilted ? baseRect.Bottom - 8f : baseRect.Bottom - 4f;
                g.DrawLine(crimsonPen, baseRect.Left + 4f, seamY, baseRect.Right - 4f, seamY);
            }
        }

        /// <summary>
        /// Native procedural geometry fallback for the top-down precision optical reticle ring
        /// if stamp-reticle-v1.png is absent.
        /// </summary>
        private static void DrawProceduralReticleRing(Graphics g, float innerR, float outerR, float opacity)
        {
            int a = (int)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 255);
            if (a < 5) return;

            using (var ringPath = new GraphicsPath())
            {
                ringPath.AddEllipse(-outerR, -outerR, outerR * 2f, outerR * 2f);
                ringPath.AddEllipse(-innerR, -innerR, innerR * 2f, innerR * 2f);

                using (var ringBrush = new SolidBrush(Color.FromArgb(a, 32, 34, 39)))
                using (var outerCrimsonPen = new Pen(Color.FromArgb((int)(a * 0.85f), 192, 26, 34), 1.8f))
                using (var innerBevelPen = new Pen(Color.FromArgb((int)(a * 0.70f), 160, 165, 175), 1.2f))
                {
                    g.FillPath(ringBrush, ringPath);
                    g.DrawEllipse(outerCrimsonPen, -outerR + 2f, -outerR + 2f, (outerR - 2f) * 2f, (outerR - 2f) * 2f);
                    g.DrawEllipse(innerBevelPen, -innerR, -innerR, innerR * 2f, innerR * 2f);
                }
            }
        }

        public static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(Math.Min(rect.Width, rect.Height), Math.Max(1f, radius * 2f));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
