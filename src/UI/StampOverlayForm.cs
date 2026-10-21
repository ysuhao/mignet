using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using DesktopStamp.Audio;
using DesktopStamp.Core;
using DesktopStamp.HostAdapters;
using DesktopStamp.Imaging;

namespace DesktopStamp.UI
{
    /// <summary>
    /// Frameless, borderless, always-on-top, non-activating per-pixel alpha native Windows overlay
    /// driving the physical desktop stamp.
    /// v2 Architecture:
    /// - Offloads all cross-process Word COM target probing and stamp insertion to AsyncStampHostWorker (background STA)
    /// - Reuses a persistent Win32 DIBSection + Memory DC across frames (zero per-frame HBITMAP/HDC churn)
    /// - Skips redundant rendering and UpdateLayeredWindow calls when visual/position state is unchanged
    /// - Uses a fast position-only UpdateLayeredWindow path when gliding across a page with unchanged reticle visuals
    /// - Coalesces high-frequency pointer and wheel input updates once per frame tick
    /// </summary>
    public sealed class StampOverlayForm : Form
    {
        private struct VisualStateSignature : IEquatable<VisualStateSignature>
        {
            public StampState State;
            public int PhysicalSizeKey;
            public SizeLockMode SizeLock;
            public int HoverProgressKey;
            public int HoverBreathKey;
            public int MorphKey;
            public int TiltKey;
            public int PressScaleKey;
            public int DisplayAngleKey;
            public int ZCompressionKey;
            public int RippleKey;
            public int DropBounceKey;
            public int FlashKey;
            public string SealId;
            public int DpiKey;
            public int ZoomKey;
            public bool IsValidTarget;
            public int PageNumber;
            public string StatusSummary;
            public string ToastMessage;
            public int ToastAlphaKey;

            public bool Equals(VisualStateSignature other)
            {
                return State == other.State &&
                       PhysicalSizeKey == other.PhysicalSizeKey &&
                       SizeLock == other.SizeLock &&
                       HoverProgressKey == other.HoverProgressKey &&
                       HoverBreathKey == other.HoverBreathKey &&
                       MorphKey == other.MorphKey &&
                       TiltKey == other.TiltKey &&
                       PressScaleKey == other.PressScaleKey &&
                       DisplayAngleKey == other.DisplayAngleKey &&
                       ZCompressionKey == other.ZCompressionKey &&
                       RippleKey == other.RippleKey &&
                       DropBounceKey == other.DropBounceKey &&
                       FlashKey == other.FlashKey &&
                       SealId == other.SealId &&
                       DpiKey == other.DpiKey &&
                       ZoomKey == other.ZoomKey &&
                       IsValidTarget == other.IsValidTarget &&
                       PageNumber == other.PageNumber &&
                       StatusSummary == other.StatusSummary &&
                       ToastMessage == other.ToastMessage &&
                       ToastAlphaKey == other.ToastAlphaKey;
            }
        }

        private const int ActiveTimerIntervalMs = 8;
        private const int SettledIdleTimerIntervalMs = 45;
        private const double MaxAcceptedProbeAgeMs = 1500.0;

        private int _currentCanvasSize = 460;

        private readonly AssetRepository _assetRepository;
        private readonly StampRenderer _renderer;
        private readonly StampStateMachine _stateMachine;
        private readonly CompositeStampHostRouter _hostRouter;
        private readonly AsyncStampHostWorker _asyncHostWorker;
        private readonly GlobalInputHook _globalHook;
        private readonly List<SealAsset> _seals = new List<SealAsset>();

        private SealAsset _activeSeal;
        private FrostedContextMenuForm _activeMenu;
        private Bitmap _frameBuffer;
        private Graphics _frameGraphics;

        // Persistent Win32 layered window DIB surface resources (allocated only on canvas resize)
        private IntPtr _persistentMemDc = IntPtr.Zero;
        private IntPtr _dibSectionHandle = IntPtr.Zero;
        private IntPtr _oldDibBitmapHandle = IntPtr.Zero;
        private IntPtr _dibBitsPtr = IntPtr.Zero;
        private int[] _premultiplyScratchBuffer;

        private readonly System.Windows.Forms.Timer _frameTimer;
        private readonly Stopwatch _stopwatch;
        private double _lastTickSeconds;
        private double _lastProbeRequestSeconds;
        private double _lastDpiQuerySeconds;
        private double _cachedMonitorDpi = 96.0;
        private bool _probePending;
        private long _lastConsumedProbeTimestamp;
        private Vector2D _lastProbedScreenPoint;
        private int _stationaryProbeStreak;
        private bool _restingFullProbeIssued;
        private StampTargetProbe _latestProbe;

        private int _pendingScaleNotches;
        private int _pendingRotateNotches;

        private bool _hasRenderedOnce;
        private bool _forceVisualRender = true;
        private VisualStateSignature _lastRenderedSignature;
        private int _lastPresentedLeft = int.MinValue;
        private int _lastPresentedTop = int.MinValue;

        private bool _leftButtonDownOnIdle;
        private int _impressionCounter;
        private AsyncStampWorkItem _pendingClickStampSnapshot;
        private string _lastPreImpactInvalidationReason;

        private string _toastMessage;
        private double _toastRemainingSeconds;

        public long TotalTimerTicks { get; private set; }
        public long FullRenderFrameCount { get; private set; }
        public long PositionOnlyPresentCount { get; private set; }
        public long LayeredFramesPresentedCount => FullRenderFrameCount + PositionOnlyPresentCount;
        public long SkippedIdleFrameCount { get; private set; }
        public int DibReallocationCount { get; private set; }
        public long RejectedStaleProbeCount { get; private set; }

        public StampStateMachine StateMachine => _stateMachine;
        public StampRenderer Renderer => _renderer;
        public AsyncStampHostWorker AsyncHostWorker => _asyncHostWorker;
        public SealAsset ActiveSeal => _activeSeal;
        public IReadOnlyList<SealAsset> Seals => _seals;
        public GlobalInputHook InputHook => _globalHook;
        public StampTargetProbe LatestProbe => _latestProbe;
        public AsyncStampWorkItem PendingClickStampSnapshot => _pendingClickStampSnapshot;
        public int CurrentCanvasSize => _currentCanvasSize;

        /// <summary>
        /// Indicates whether WS_EX_TRANSPARENT is currently set on the layered window's GWL_EXSTYLE
        /// for true cross-process OS mouse/wheel pass-through.
        /// </summary>
        public bool IsCrossProcessPassThroughActive
        {
            get
            {
                if (!IsHandleCreated || IsDisposed)
                    return false;
                int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
                return (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;
            }
        }

        public StampOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AllowDrop = true;
            Text = "DesktopPhysicalStampOverlay";
            Size = new Size(_currentCanvasSize, _currentCanvasSize);

            _assetRepository = new AssetRepository();
            _renderer = new StampRenderer(_assetRepository);

            // Compute DPI-aware initial parked position near the bottom-right corner of the primary working area
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            var initialCornerPt = new NativeMethods.POINT(workArea.Right - 100, workArea.Bottom - 100);
            _cachedMonitorDpi = NativeMethods.QueryDpiForPointOrWindow(initialCornerPt, IntPtr.Zero);
            double initialDpiScale = Math.Max(1.0, _cachedMonitorDpi / 96.0);
            Point safeParkedPt = StampConfiguration.ComputeSafeParkedCenter(workArea, initialDpiScale);
            var parkedPos = new Vector2D(safeParkedPt.X, safeParkedPt.Y);

            var config = new StampConfiguration
            {
                ReducedMotion = NativeMethods.IsReducedMotionPreferred()
            };

            _stateMachine = new StampStateMachine(config, parkedPos);
            _stateMachine.StampTriggered += OnStampTriggered;
            _stateMachine.StampImpactCommitted += OnStampImpactCommitted;

            _hostRouter = new CompositeStampHostRouter();
            _asyncHostWorker = new AsyncStampHostWorker(_hostRouter, OnAsyncHostWorkerResultReady);

            // Load built-in SAMPLE / 样章 seals
            _seals.AddRange(SealImageProcessor.CreateBuiltInSampleSeals());
            if (_seals.Count > 0)
            {
                _activeSeal = _seals[0];
            }

            AllocateCanvasAndDibResources(_currentCanvasSize);

            // Configure global input hook for Aiming / Dragging states.
            // All hook callbacks only capture state/coordinates and defer heavy work to the timer loop.
            _globalHook = new GlobalInputHook
            {
                QueryCurrentState = () => _stateMachine.CurrentState,
                IsPointOnOuterReticleRing = IsScreenPointOnOuterRingFast,
                IsPointInsideOwnMenu = pt =>
                    _activeMenu != null &&
                    !_activeMenu.IsDisposed &&
                    _activeMenu.Visible &&
                    _activeMenu.Bounds.Contains((int)Math.Round(pt.X), (int)Math.Round(pt.Y))
            };

            _globalHook.PointerMoved += OnGlobalPointerMoved;
            _globalHook.PickupDragReleased += OnGlobalPickupDragReleased;
            _globalHook.AimingLeftClickCommitted += OnGlobalAimingLeftClick;
            _globalHook.AimingRightClickCancelled += OnGlobalAimingRightClick;
            _globalHook.CtrlWheelScaled += OnGlobalCtrlWheelScaled;
            _globalHook.AltOrRingWheelRotated += OnGlobalAltOrRingWheelRotated;
            _globalHook.PlainWheelScrolled += OnGlobalPlainWheelScrolled;
            _globalHook.EscapePressed += OnGlobalEscapePressed;
            _globalHook.NavigationKeyPressed += OnGlobalNavigationKeyPressed;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _stopwatch = Stopwatch.StartNew();
            _lastTickSeconds = _stopwatch.Elapsed.TotalSeconds;

            _frameTimer = new System.Windows.Forms.Timer { Interval = ActiveTimerIntervalMs };
            _frameTimer.Tick += OnFrameTimerTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED |
                              NativeMethods.WS_EX_TOPMOST |
                              NativeMethods.WS_EX_TOOLWINDOW |
                              NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_globalHook.Install())
            {
                ShowToast("请重试拾取印章", 3.2);
            }
            SyncPassThroughWindowStyle();
            _forceVisualRender = true;
            _frameTimer.Start();
            PresentLayeredFrame();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed)
                return;

            try
            {
                Point currentParked = new Point(
                    (int)Math.Round(_stateMachine.ParkedPosition.X),
                    (int)Math.Round(_stateMachine.ParkedPosition.Y));
                Screen scr = Screen.FromPoint(currentParked) ?? Screen.PrimaryScreen;
                Rectangle workArea = scr.WorkingArea;
                _cachedMonitorDpi = QueryCurrentMonitorDpi();
                double dpiScale = Math.Max(1.0, _cachedMonitorDpi / 96.0);
                Point clamped = StampConfiguration.ClampParkedCenterToWorkArea(currentParked, workArea, dpiScale);
                _stateMachine.SetParkedPosition(new Vector2D(clamped.X, clamped.Y), snapIfIdle: true);
                _renderer.InvalidateCaches();
                _forceVisualRender = true;
                WakeTimerIfIdle();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Synchronizes WS_EX_TRANSPARENT on the layered window handle with the current state machine mode.
        /// </summary>
        public void SyncPassThroughWindowStyle()
        {
            if (!IsHandleCreated || IsDisposed)
                return;

            StampState state = _stateMachine.CurrentState;
            bool enablePassThrough =
                state == StampState.Aiming ||
                state == StampState.Stamping ||
                state == StampState.Returning;

            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            bool currentlyEnabled = (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;

            if (enablePassThrough != currentlyEnabled)
            {
                int nextStyle = enablePassThrough
                    ? (exStyle | NativeMethods.WS_EX_TRANSPARENT)
                    : (exStyle & ~NativeMethods.WS_EX_TRANSPARENT);

                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, nextStyle);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
                return;
            }

            if (m.Msg == NativeMethods.WM_NCHITTEST)
            {
                if (_stateMachine.CurrentState != StampState.Idle &&
                    _stateMachine.CurrentState != StampState.DraggingPickup)
                {
                    m.Result = new IntPtr(NativeMethods.HTTRANSPARENT);
                    return;
                }

                int lp = m.LParam.ToInt32();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);

                if (_renderer.IsPointOverIdleBody(new Vector2D(sx, sy), _stateMachine.CurrentPosition, _cachedMonitorDpi))
                {
                    m.Result = new IntPtr(NativeMethods.HTCLIENT);
                }
                else
                {
                    m.Result = new IntPtr(NativeMethods.HTTRANSPARENT);
                }
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var screenPt = GetCursorScreenVector();

            if (e.Button == MouseButtons.Left &&
                (_stateMachine.CurrentState == StampState.Idle || _stateMachine.CurrentState == StampState.Returning))
            {
                if (!_globalHook.IsInstalled && !_globalHook.Install())
                {
                    ShowToast("当前无法进入盖章模式，请稍后重试", 3.0);
                    return;
                }

                _leftButtonDownOnIdle = true;
                _stateMachine.BeginPickupDrag(screenPt);
                SyncPassThroughWindowStyle();
                WakeTimerIfIdle();
                StampAudioSynthesizer.PlaySoftDetentAsync();
            }
            else if (e.Button == MouseButtons.Right && _stateMachine.CurrentState == StampState.Idle)
            {
                OpenFrostedContextMenu(new Point((int)Math.Round(screenPt.X), (int)Math.Round(screenPt.Y)));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var screenPt = GetCursorScreenVector();

            if (_stateMachine.CurrentState == StampState.Idle)
            {
                bool overBody = _renderer.IsPointOverIdleBody(screenPt, _stateMachine.ParkedPosition, _cachedMonitorDpi);
                if (overBody != _stateMachine.IsHoveredInIdle)
                {
                    _stateMachine.SetHoverInIdle(overBody);
                    WakeTimerIfIdle();
                }
            }
            else if (_leftButtonDownOnIdle && _stateMachine.CurrentState == StampState.DraggingPickup)
            {
                _stateMachine.OnPointerMove(screenPt);
                WakeTimerIfIdle();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _leftButtonDownOnIdle)
            {
                _leftButtonDownOnIdle = false;
                var screenPt = GetCursorScreenVector();
                CompletePickupAndVerifyHook(screenPt);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_stateMachine.CurrentState == StampState.Idle && _stateMachine.IsHoveredInIdle)
            {
                _stateMachine.SetHoverInIdle(false);
                WakeTimerIfIdle();
            }
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                drgevent.Effect = DragDropEffects.Copy;
                _stateMachine.SetHoverInIdle(true);
                WakeTimerIfIdle();
            }
            else
            {
                drgevent.Effect = DragDropEffects.None;
            }
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            if (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (drgevent.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    ImportSealFromFile(files[0]);
                }
            }
        }

        public void ImportSealFromFile(string filePath)
        {
            SealImportResult result = SealImageProcessor.LoadAndProcessSeal(filePath);
            if (!result.Success)
            {
                _stateMachine.TriggerSealLoadedFeedback();
                ShowToast(result.ErrorMessage, 3.6);
                return;
            }

            _seals.Add(result.Asset);
            _activeSeal = result.Asset;
            _asyncHostWorker.AdvanceViewEpoch();
            InvalidatePendingClickStamp("已切换印章，请重新点击盖章");
            _renderer.InvalidateCaches();
            _forceVisualRender = true;
            _stateMachine.TriggerSealLoadedFeedback();
            WakeTimerIfIdle();
            StampAudioSynthesizer.PlaySoftDetentAsync();

            string alphaMode = result.Asset.PreservedOriginalAlpha ? "保留透明通道" : "智能抠取红章";
            ShowToast($"已载入「{result.Asset.Name}」({alphaMode})", 2.8);
        }

        private void CompletePickupAndVerifyHook(Vector2D screenPt)
        {
            if (_stateMachine.CurrentState != StampState.DraggingPickup)
                return;

            if (!_globalHook.IsInstalled && !_globalHook.Install())
            {
                _stateMachine.BeginReturnToParked();
                SyncPassThroughWindowStyle();
                ShowToast("已自动返回待命位置", 3.0);
                return;
            }

            if (_stateMachine.CompletePickupToAiming(screenPt))
            {
                SyncPassThroughWindowStyle();
                _probePending = true;
                _stationaryProbeStreak = 0;
                _asyncHostWorker.RequestProbe(screenPt, Handle);
                WakeTimerIfIdle();
            }
        }

        private void OnGlobalPointerMoved(Vector2D screenPt)
        {
            // Lightweight in-memory struct assignment only; coalesced cleanly into the frame timer loop
            _stateMachine.OnPointerMove(screenPt);
        }

        private void OnGlobalPickupDragReleased(Vector2D screenPt)
        {
            _leftButtonDownOnIdle = false;
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() => CompletePickupAndVerifyHook(screenPt)));
            }
            else
            {
                _stateMachine.CompletePickupToAiming(screenPt);
            }
        }

        private void OnGlobalAimingLeftClick(Vector2D screenPt)
        {
            double currentZoom = (_latestProbe != null && _latestProbe.IsValidTarget && _latestProbe.DocumentZoomPercent > 10)
                ? _latestProbe.DocumentZoomPercent
                : 100.0;

            if (!CanCommitStampAtExtent(_cachedMonitorDpi, currentZoom, out string extentError))
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() => ShowToast(extentError, 3.2)));
                }
                return;
            }

            _stateMachine.TryTriggerStamp(screenPt);
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    SyncPassThroughWindowStyle();
                    WakeTimerIfIdle();
                }));
            }
        }

        private void OnGlobalAimingRightClick(Vector2D screenPt)
        {
            _leftButtonDownOnIdle = false;
            _asyncHostWorker.AdvanceCancelEpoch();
            InvalidatePendingClickStamp("已取消本次盖章");
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    if (_stateMachine.BeginReturnToParked())
                    {
                        SyncPassThroughWindowStyle();
                        WakeTimerIfIdle();
                        StampAudioSynthesizer.PlaySoftDetentAsync();
                    }
                }));
            }
        }

        private void OnGlobalCtrlWheelScaled(int notches)
        {
            _asyncHostWorker.AdvanceViewEpoch();
            InvalidatePendingClickStamp("印章尺寸已调整，请重新对准后点击");
            Interlocked.Add(ref _pendingScaleNotches, notches);
        }

        private void OnGlobalAltOrRingWheelRotated(int notches)
        {
            _asyncHostWorker.AdvanceViewEpoch();
            InvalidatePendingClickStamp("印章角度已调整，请重新对准后点击");
            Interlocked.Add(ref _pendingRotateNotches, notches);
        }

        private void OnGlobalPlainWheelScrolled()
        {
            // Document view underneath is scrolling: advance view epoch to protect any in-flight stamp
            // and trigger an immediate background probe refresh.
            _asyncHostWorker.AdvanceViewEpoch();
            InvalidatePendingClickStamp("文档页面正在滚动，已取消落章，请停稳后重试");
            _probePending = true;
            _stationaryProbeStreak = 0;
        }

        private void OnGlobalNavigationKeyPressed(int vkCode)
        {
            // Keyboard navigation/scroll/zoom (PageUp/PageDown/Home/End/Arrows/etc.) passed through to Word:
            // advance view epoch and invalidate any pre-impact click snapshot without swallowing the key.
            _asyncHostWorker.AdvanceViewEpoch();
            InvalidatePendingClickStamp("文档视图已变化，已取消本次盖章");
            _probePending = true;
            _stationaryProbeStreak = 0;
        }

        private void OnGlobalEscapePressed()
        {
            _asyncHostWorker.AdvanceCancelEpoch();
            InvalidatePendingClickStamp("已取消本次盖章");
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    if (_activeMenu != null && !_activeMenu.IsDisposed && _activeMenu.Visible)
                    {
                        _activeMenu.Close();
                        return;
                    }

                    _leftButtonDownOnIdle = false;
                    if (_stateMachine.BeginReturnToParked())
                    {
                        SyncPassThroughWindowStyle();
                        WakeTimerIfIdle();
                        StampAudioSynthesizer.PlaySoftDetentAsync();
                    }
                }));
            }
        }

        public void InvalidatePendingClickStamp(string userFacingReason = null)
        {
            AsyncStampWorkItem pending = Interlocked.Exchange(ref _pendingClickStampSnapshot, null);
            if (pending != null)
            {
                _lastPreImpactInvalidationReason = string.IsNullOrWhiteSpace(userFacingReason)
                    ? "页面或印章状态已变更，请重新点击盖章"
                    : userFacingReason;
                _asyncHostWorker.AbortSnapshotBeforeImpact(pending, _lastPreImpactInvalidationReason);
            }
        }

        private void OnAsyncHostWorkerResultReady()
        {
            if (IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke(new Action(ConsumeAsyncHostWorkerResults));
                }
                catch
                {
                }
            }
        }

        public bool TryAcceptProbeSnapshot(AsyncHostProbeSnapshot snapshot)
        {
            if (snapshot?.Probe == null)
                return false;

            if (snapshot.IsStaleAtCompletion ||
                snapshot.CancelEpoch != _asyncHostWorker.CurrentCancelEpoch ||
                snapshot.ViewEpoch != _asyncHostWorker.CurrentViewEpoch)
            {
                RejectedStaleProbeCount++;
                return false;
            }

            if (snapshot.CompletedTimestamp > 0)
            {
                double ageMs = (Stopwatch.GetTimestamp() - snapshot.CompletedTimestamp) * 1000.0 / Stopwatch.Frequency;
                if (ageMs > MaxAcceptedProbeAgeMs)
                {
                    RejectedStaleProbeCount++;
                    return false;
                }
            }

            StampState currentState = _stateMachine.CurrentState;
            if (currentState != StampState.Aiming &&
                currentState != StampState.DraggingPickup &&
                currentState != StampState.Stamping)
            {
                RejectedStaleProbeCount++;
                return false;
            }

            if (snapshot.Probe.TargetHwnd != IntPtr.Zero && !NativeMethods.IsWindow(snapshot.Probe.TargetHwnd))
            {
                RejectedStaleProbeCount++;
                return false;
            }

            // If cursor moved significantly since the probe was requested, verify the underlying target window hasn't switched
            if (Vector2D.Distance(snapshot.ScreenPointPx, _stateMachine.TargetPosition) > 64.0)
            {
                IntPtr currentHwnd = _hostRouter.FindUnderlyingWindowAtPoint(
                    _stateMachine.TargetPosition,
                    IsHandleCreated ? Handle : IntPtr.Zero);
                if (currentHwnd != IntPtr.Zero &&
                    snapshot.Probe.TargetHwnd != IntPtr.Zero &&
                    currentHwnd != snapshot.Probe.TargetHwnd)
                {
                    RejectedStaleProbeCount++;
                    return false;
                }
            }

            _latestProbe = SanitizeProbeForUi(snapshot.Probe);
            if (_latestProbe != null && _latestProbe.MonitorDpi >= 72.0)
            {
                _cachedMonitorDpi = _latestProbe.MonitorDpi;
            }
            return true;
        }

        private static StampTargetProbe SanitizeProbeForUi(StampTargetProbe probe)
        {
            if (probe == null)
                return null;

            probe.StatusSummary = SanitizeUserFacingMessage(
                probe.StatusSummary,
                probe.IsValidTarget ? $"Word 第 {Math.Max(1, probe.PageNumber)} 页" : "请移到 Word 文档页面");
            return probe;
        }

        public static string SanitizeUserFacingMessage(string message, string fallback = "当前无法完成盖章，请重试")
        {
            if (string.IsNullOrWhiteSpace(message))
                return fallback;

            if (message.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("HRESULT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("0x8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("RPC_E_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("StackTrace", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.Length > 80)
            {
                return fallback;
            }

            return message;
        }

        public void ConsumeAsyncHostWorkerResults()
        {
            if (IsDisposed)
                return;

            AsyncHostProbeSnapshot snapshot = _asyncHostWorker.GetLatestProbeSnapshot();
            if (snapshot != null && snapshot.CompletedTimestamp != _lastConsumedProbeTimestamp)
            {
                _lastConsumedProbeTimestamp = snapshot.CompletedTimestamp;
                TryAcceptProbeSnapshot(snapshot);
            }

            int drained = 0;
            while (drained < 16 && _asyncHostWorker.TryConsumeStampCompletion(out AsyncStampCompletion completion))
            {
                drained++;
                if (completion?.Result == null)
                    continue;

                if (completion.Result.Success)
                {
                    ShowToast(
                        $"已盖章 · 第{completion.Result.PageNumber}页 ({completion.PhysicalSizeMm:F0}mm, {completion.EffectiveAngleDeg:+0.0;-0.0;0.0}°)",
                        1.4);
                }
                else if (completion.CancelEpoch != _asyncHostWorker.CurrentCancelEpoch &&
                         (_stateMachine.CurrentState == StampState.Returning || _stateMachine.CurrentState == StampState.Idle))
                {
                    ShowToast("已取消本次盖章", 1.4);
                }
                else
                {
                    ShowToast(SanitizeUserFacingMessage(completion.Result.Message), 2.2);
                }
            }
        }

        public bool CanCommitStampAtExtent(double monitorDpi, double documentZoomPercent, out string rejectionMessage)
        {
            double aspect = _activeSeal != null ? _activeSeal.InkAspectRatio : 1.0;
            return _renderer.IsPreviewExtentSupported(
                _stateMachine.PhysicalSizeMm,
                aspect,
                monitorDpi,
                documentZoomPercent,
                out _,
                out rejectionMessage);
        }

        private void OnStampTriggered(Vector2D clickScreenPoint, double physicalSizeMm, double baseRotationDeg, double jitterDeg)
        {
            _lastPreImpactInvalidationReason = null;
            if (_activeSeal == null)
            {
                _pendingClickStampSnapshot = null;
                return;
            }

            IntPtr overlayHwnd = IsHandleCreated ? Handle : IntPtr.Zero;
            IntPtr expectedTargetHwnd = _hostRouter.FindUnderlyingWindowAtPoint(clickScreenPoint, overlayHwnd);
            IntPtr expectedRootHwnd = expectedTargetHwnd != IntPtr.Zero
                ? NativeMethods.GetAncestor(expectedTargetHwnd, NativeMethods.GA_ROOT)
                : IntPtr.Zero;

            if (expectedTargetHwnd == IntPtr.Zero && _latestProbe != null && _latestProbe.TargetHwnd != IntPtr.Zero)
            {
                expectedTargetHwnd = _latestProbe.TargetHwnd;
                expectedRootHwnd = _latestProbe.RootHwnd != IntPtr.Zero
                    ? _latestProbe.RootHwnd
                    : NativeMethods.GetAncestor(expectedTargetHwnd, NativeMethods.GA_ROOT);
            }

            int seed = ++_impressionCounter * 7919 + Environment.TickCount;
            AsyncStampWorkItem snapshot = _asyncHostWorker.CreateClickSnapshot(
                clickScreenPoint,
                overlayHwnd,
                expectedTargetHwnd,
                expectedRootHwnd,
                _activeSeal,
                physicalSizeMm,
                baseRotationDeg,
                jitterDeg,
                seed);

            // Queue now so the worker does the slow read-only Word preparation during the press animation;
            // it still waits for OnStampImpactCommitted (or an abort) before inserting anything
            _pendingClickStampSnapshot = snapshot;
            _asyncHostWorker.EnqueueAwaitingImpact(snapshot);
        }

        private void OnStampImpactCommitted(Vector2D impactScreenPoint, double physicalSizeMm, double effectiveAngleDeg)
        {
            StampAudioSynthesizer.PlayStampImpactAsync();

            AsyncStampWorkItem snapshot = Interlocked.Exchange(ref _pendingClickStampSnapshot, null);
            if (snapshot == null)
            {
                if (!string.IsNullOrWhiteSpace(_lastPreImpactInvalidationReason))
                {
                    _lastPreImpactInvalidationReason = null;
                }
                return;
            }

            if (_activeSeal == null || !string.Equals(_activeSeal.Id, snapshot.Seal?.Id, StringComparison.Ordinal))
            {
                _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, "已切换印章，请重新点击盖章");
                return;
            }

            if (snapshot.CancelEpoch != _asyncHostWorker.CurrentCancelEpoch)
            {
                _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, "已取消本次盖章");
                return;
            }

            if (snapshot.ViewEpoch != _asyncHostWorker.CurrentViewEpoch)
            {
                _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, "文档页面视图已变化，已取消落章，请停稳后重试");
                return;
            }

            if (Math.Abs(_stateMachine.PhysicalSizeMm - snapshot.PhysicalSizeMm) > 0.05 ||
                Math.Abs(_stateMachine.BaseRotationDeg - snapshot.BaseAngleDeg) > 0.05)
            {
                _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, "印章尺寸或角度已变更，请重新对准后点击");
                return;
            }

            if (snapshot.ExpectedTargetHwnd != IntPtr.Zero)
            {
                IntPtr liveHwnd = _hostRouter.FindUnderlyingWindowAtPoint(snapshot.ScreenPointPx, snapshot.OverlayWindowHandle);
                if (liveHwnd != IntPtr.Zero && liveHwnd != snapshot.ExpectedTargetHwnd)
                {
                    _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, "目标窗口已切换，已取消落章以防误盖");
                    return;
                }
            }

            double currentZoom = (_latestProbe != null && _latestProbe.IsValidTarget && _latestProbe.DocumentZoomPercent > 10)
                ? _latestProbe.DocumentZoomPercent
                : 100.0;

            if (!CanCommitStampAtExtent(_cachedMonitorDpi, currentZoom, out string extentError))
            {
                // The snapshot is already queued; abort it (its completion shows extentError)
                _asyncHostWorker.AbortSnapshotBeforeImpact(snapshot, extentError);
                return;
            }

            // Paper impact: let the worker (already prepared since LEFT DOWN) insert the stamp
            _asyncHostWorker.CommitImpact(snapshot);
        }

        private void OnFrameTimerTick(object sender, EventArgs e)
        {
            TotalTimerTicks++;
            double now = _stopwatch.Elapsed.TotalSeconds;
            double dt = now - _lastTickSeconds;
            _lastTickSeconds = now;

            ConsumeAsyncHostWorkerResults();
            ApplyCoalescedWheelInput();

            if (now - _lastDpiQuerySeconds >= 0.25)
            {
                _lastDpiQuerySeconds = now;
                _cachedMonitorDpi = QueryCurrentMonitorDpi();
            }

            if (_stateMachine.CurrentState == StampState.Idle)
            {
                var cursorPt = GetCursorScreenVector();
                bool overBody = _renderer.IsPointOverIdleBody(cursorPt, _stateMachine.ParkedPosition, _cachedMonitorDpi);
                _stateMachine.SetHoverInIdle(overBody);
            }
            else if (_stateMachine.CurrentState == StampState.Aiming ||
                     (_stateMachine.CurrentState == StampState.DraggingPickup && _stateMachine.MorphProgress >= 0.55))
            {
                ScheduleNonBlockingTargetProbeIfNeeded(_stateMachine.TargetPosition, now);
            }

            if (_toastRemainingSeconds > 0.0)
            {
                _toastRemainingSeconds = Math.Max(0.0, _toastRemainingSeconds - dt);
                if (_toastRemainingSeconds <= 0.0)
                    _toastMessage = null;
            }

            StampState prevState = _stateMachine.CurrentState;
            _stateMachine.Update(dt);
            if (_stateMachine.CurrentState != prevState)
            {
                SyncPassThroughWindowStyle();
                if (_stateMachine.CurrentState == StampState.Aiming)
                {
                    _probePending = true;
                    _stationaryProbeStreak = 0;
                    ScheduleNonBlockingTargetProbeIfNeeded(_stateMachine.TargetPosition, now);
                }
            }

            UpdateAdaptiveTimerCadence();
            PresentLayeredFrame();
        }

        private void ApplyCoalescedWheelInput()
        {
            int scaleNotches = Interlocked.Exchange(ref _pendingScaleNotches, 0);
            if (scaleNotches != 0)
            {
                _asyncHostWorker.AdvanceViewEpoch();
                InvalidatePendingClickStamp("印章尺寸已调整，请重新对准后点击");
                if (_stateMachine.SizeLock != SizeLockMode.Unlocked)
                {
                    ShowToast($"尺寸已锁定为 {_stateMachine.PhysicalSizeMm:F0}mm (右键菜单可切换自由缩放)", 1.5);
                }
                else
                {
                    _stateMachine.AdjustPhysicalSizeByWheel(scaleNotches);
                }
            }

            int rotateNotches = Interlocked.Exchange(ref _pendingRotateNotches, 0);
            if (rotateNotches != 0)
            {
                _asyncHostWorker.AdvanceViewEpoch();
                InvalidatePendingClickStamp("印章角度已调整，请重新对准后点击");
                _stateMachine.RotateByWheel(rotateNotches);
            }
        }

        private void ScheduleNonBlockingTargetProbeIfNeeded(Vector2D cursorPt, double nowSeconds)
        {
            double movedPx = Vector2D.Distance(cursorPt, _lastProbedScreenPoint);
            bool cursorMoved = movedPx > 1.5;

            double requiredInterval;
            if (_probePending)
            {
                requiredInterval = 0.0;
            }
            else if (cursorMoved)
            {
                requiredInterval = 0.075; // ~13Hz coalesced background probe while moving
            }
            else
            {
                // First probe soon after the cursor rests (the full one that primes the instant-stamp page cache),
                // then progressive backoff (0.28s -> 0.55s -> 0.90s)
                requiredInterval = _stationaryProbeStreak == 0
                    ? 0.10
                    : _stationaryProbeStreak < 2
                        ? 0.28
                        : (_stationaryProbeStreak < 5 ? 0.55 : 0.90);
            }

            if (nowSeconds - _lastProbeRequestSeconds >= requiredInterval)
            {
                bool wasPending = _probePending;
                _probePending = false;
                _lastProbeRequestSeconds = nowSeconds;
                if (cursorMoved)
                {
                    _stationaryProbeStreak = 0;
                    _lastProbedScreenPoint = cursorPt;
                }
                else
                {
                    _stationaryProbeStreak++;
                }

                // Only a cheap window/zoom probe while moving, right after a scroll/zoom/pickup (the view may still be
                // animating), and on repeat probes while resting. The full page mapping (~70-200ms of Word's UI thread;
                // it primes the adapter's page cache for an instant stamp) runs once per rest, on the first regular
                // stationary probe after any of those.
                if (cursorMoved || wasPending)
                    _restingFullProbeIssued = false;
                bool lightweight = cursorMoved || wasPending || _restingFullProbeIssued;
                if (!lightweight)
                    _restingFullProbeIssued = true;

                IntPtr overlayHwnd = IsHandleCreated ? Handle : IntPtr.Zero;
                _asyncHostWorker.RequestProbe(cursorPt, overlayHwnd, lightweight);
            }
        }

        private void WakeTimerIfIdle()
        {
            if (_frameTimer != null && _frameTimer.Interval != ActiveTimerIntervalMs)
            {
                _frameTimer.Interval = ActiveTimerIntervalMs;
            }
        }

        private void UpdateAdaptiveTimerCadence()
        {
            if (_frameTimer == null)
                return;

            bool canThrottleIdle = _stateMachine.IsSettledIdle && _toastRemainingSeconds <= 0.001;
            int desiredInterval = canThrottleIdle ? SettledIdleTimerIntervalMs : ActiveTimerIntervalMs;
            if (_frameTimer.Interval != desiredInterval)
            {
                _frameTimer.Interval = desiredInterval;
            }
        }

        /// <summary>
        /// Pure-math outer ring check invoked inside WH_MOUSE_LL on WM_MOUSEWHEEL.
        /// Uses cached monitor DPI and latest probe zoom so zero Win32/COM calls occur in the hook.
        /// </summary>
        private bool IsScreenPointOnOuterRingFast(Vector2D screenPt)
        {
            if (_stateMachine.CurrentState != StampState.Aiming)
                return false;

            double zoom = (_latestProbe != null && _latestProbe.IsValidTarget && _latestProbe.DocumentZoomPercent > 10)
                ? _latestProbe.DocumentZoomPercent
                : 100.0;
            double aspect = _activeSeal != null ? _activeSeal.InkAspectRatio : 1.0;

            return _renderer.IsPointOnOuterReticleRing(
                screenPt,
                _stateMachine.CurrentPosition,
                _stateMachine.PhysicalSizeMm,
                _cachedMonitorDpi,
                zoom,
                aspect);
        }

        private void EnsureCanvasBufferCapacity(double monitorDpi, double zoomPercent)
        {
            double aspect = _activeSeal != null ? _activeSeal.InkAspectRatio : 1.0;
            int requiredSize = _renderer.ComputeRequiredCanvasSize(
                _stateMachine.PhysicalSizeMm,
                aspect,
                monitorDpi,
                zoomPercent);

            if (requiredSize != _currentCanvasSize || _dibSectionHandle == IntPtr.Zero)
            {
                AllocateCanvasAndDibResources(requiredSize);
                Size = new Size(_currentCanvasSize, _currentCanvasSize);
                _forceVisualRender = true;
            }
        }

        private void AllocateCanvasAndDibResources(int canvasSize)
        {
            ReleaseCanvasAndDibResources();

            _currentCanvasSize = Math.Max(64, canvasSize);
            _frameBuffer = new Bitmap(_currentCanvasSize, _currentCanvasSize, PixelFormat.Format32bppArgb);
            _frameGraphics = Graphics.FromImage(_frameBuffer);
            _premultiplyScratchBuffer = new int[_currentCanvasSize * _currentCanvasSize];

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                _persistentMemDc = NativeMethods.CreateCompatibleDC(screenDc);
                var bmi = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER)),
                    biWidth = _currentCanvasSize,
                    biHeight = -_currentCanvasSize, // Top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                    biSizeImage = (uint)(_currentCanvasSize * _currentCanvasSize * 4)
                };

                _dibSectionHandle = NativeMethods.CreateDIBSection(
                    _persistentMemDc,
                    ref bmi,
                    NativeMethods.DIB_RGB_COLORS,
                    out _dibBitsPtr,
                    IntPtr.Zero,
                    0);

                if (_dibSectionHandle != IntPtr.Zero)
                {
                    _oldDibBitmapHandle = NativeMethods.SelectObject(_persistentMemDc, _dibSectionHandle);
                }
            }
            finally
            {
                if (screenDc != IntPtr.Zero)
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }

            DibReallocationCount++;
        }

        private void ReleaseCanvasAndDibResources()
        {
            if (_persistentMemDc != IntPtr.Zero && _oldDibBitmapHandle != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_persistentMemDc, _oldDibBitmapHandle);
                _oldDibBitmapHandle = IntPtr.Zero;
            }

            if (_dibSectionHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_dibSectionHandle);
                _dibSectionHandle = IntPtr.Zero;
            }

            if (_persistentMemDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(_persistentMemDc);
                _persistentMemDc = IntPtr.Zero;
            }

            _dibBitsPtr = IntPtr.Zero;

            if (_frameGraphics != null)
            {
                _frameGraphics.Dispose();
                _frameGraphics = null;
            }

            if (_frameBuffer != null)
            {
                _frameBuffer.Dispose();
                _frameBuffer = null;
            }
        }

        private VisualStateSignature CaptureCurrentVisualSignature(double dpi, double zoom, double toastAlpha)
        {
            double displayAngle = _stateMachine.CurrentState == StampState.Stamping
                ? _stateMachine.EffectiveImpressionAngleDeg
                : _stateMachine.BaseRotationDeg;

            bool hasBreathEffect = _stateMachine.CurrentState == StampState.Idle &&
                                   (_stateMachine.IsHoveredInIdle || _stateMachine.HoverProgress > 0.002);

            return new VisualStateSignature
            {
                State = _stateMachine.CurrentState,
                PhysicalSizeKey = (int)Math.Round(_stateMachine.PhysicalSizeMm * 10.0),
                SizeLock = _stateMachine.SizeLock,
                HoverProgressKey = (int)Math.Round(_stateMachine.HoverProgress * 250.0),
                HoverBreathKey = hasBreathEffect ? (int)Math.Round(_stateMachine.HoverBreathPhase * 40.0) : 0,
                MorphKey = (int)Math.Round(_stateMachine.MorphProgress * 500.0),
                TiltKey = (int)Math.Round(_stateMachine.TiltAngleDeg * 20.0),
                PressScaleKey = (int)Math.Round(_stateMachine.PressScale * 500.0),
                DisplayAngleKey = (int)Math.Round(displayAngle * 20.0),
                ZCompressionKey = (int)Math.Round(_stateMachine.ZCompression * 500.0),
                RippleKey = (int)Math.Round(_stateMachine.ImpactRippleProgress * 250.0),
                DropBounceKey = (int)Math.Round(_stateMachine.DropBounceOffsetPx * 10.0),
                FlashKey = (int)Math.Round(_stateMachine.CrimsonFlashIntensity * 250.0),
                SealId = _activeSeal?.Id,
                DpiKey = (int)Math.Round(dpi * 10.0),
                ZoomKey = (int)Math.Round(zoom * 10.0),
                IsValidTarget = _latestProbe != null && _latestProbe.IsValidTarget,
                PageNumber = _latestProbe != null ? _latestProbe.PageNumber : 0,
                StatusSummary = _latestProbe?.StatusSummary,
                ToastMessage = _toastMessage,
                ToastAlphaKey = (int)Math.Round(toastAlpha * 100.0)
            };
        }

        private void CopyFrameBufferToPremultipliedDib()
        {
            if (_frameBuffer == null || _dibBitsPtr == IntPtr.Zero || _premultiplyScratchBuffer == null)
                return;

            int width = _currentCanvasSize;
            int height = _currentCanvasSize;
            int pixelCount = width * height;

            BitmapData srcData = _frameBuffer.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                Marshal.Copy(srcData.Scan0, _premultiplyScratchBuffer, 0, pixelCount);
            }
            finally
            {
                _frameBuffer.UnlockBits(srcData);
            }

            int[] buf = _premultiplyScratchBuffer;
            for (int i = 0; i < pixelCount; i++)
            {
                uint px = (uint)buf[i];
                uint a = px >> 24;
                if (a == 0)
                {
                    buf[i] = 0;
                }
                else if (a < 255)
                {
                    uint r = (((px >> 16) & 0xFF) * a + 127) / 255;
                    uint g = (((px >> 8) & 0xFF) * a + 127) / 255;
                    uint b = ((px & 0xFF) * a + 127) / 255;
                    buf[i] = (int)((a << 24) | (r << 16) | (g << 8) | b);
                }
            }

            Marshal.Copy(buf, 0, _dibBitsPtr, pixelCount);
        }

        private void OpenFrostedContextMenu(Point screenLocation)
        {
            if (_activeMenu != null && !_activeMenu.IsDisposed)
            {
                _activeMenu.Close();
            }

            _activeMenu = new FrostedContextMenuForm(
                _seals,
                _activeSeal,
                _stateMachine,
                selectedSeal =>
                {
                    _activeSeal = selectedSeal;
                    _asyncHostWorker.AdvanceViewEpoch();
                    InvalidatePendingClickStamp("已切换印章，请重新点击盖章");
                    _renderer.InvalidateCaches();
                    _forceVisualRender = true;
                    if (_stateMachine.SizeLock == SizeLockMode.Unlocked && selectedSeal != null)
                    {
                        _stateMachine.SetPhysicalSizeMm(selectedSeal.RecommendedSizeMm);
                    }
                    _stateMachine.TriggerSealLoadedFeedback();
                    WakeTimerIfIdle();
                    StampAudioSynthesizer.PlaySoftDetentAsync();
                    ShowToast($"已切换至「{selectedSeal?.Name}」", 1.8);
                },
                PromptImportSealDialog,
                lockMode =>
                {
                    _stateMachine.SetSizeLock(lockMode);
                    _asyncHostWorker.AdvanceViewEpoch();
                    InvalidatePendingClickStamp("尺寸设置已变更，请重新点击盖章");
                    _forceVisualRender = true;
                    WakeTimerIfIdle();
                    string modeDesc = lockMode == SizeLockMode.Unlocked
                        ? $"自由缩放 ({_stateMachine.PhysicalSizeMm:F0}mm)"
                        : $"已锁定 {_stateMachine.PhysicalSizeMm:F0}mm";
                    ShowToast(modeDesc, 1.8);
                },
                () =>
                {
                    _stateMachine.ResetRotation();
                    _asyncHostWorker.AdvanceViewEpoch();
                    InvalidatePendingClickStamp("角度已重置，请重新点击盖章");
                    _forceVisualRender = true;
                    WakeTimerIfIdle();
                    ShowToast("已重置旋转角度为 0.0°", 1.5);
                },
                () =>
                {
                    ShutdownAndExit();
                },
                _cachedMonitorDpi);

            _activeMenu.ShowNearPoint(screenLocation);
        }

        private void PromptImportSealDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择印章图片 (PNG透明图 / 照片 / 扫描件)";
                dlg.Filter = "印章图片 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件 (*.*)|*.*";
                dlg.Multiselect = false;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    ImportSealFromFile(dlg.FileName);
                }
            }
        }

        private void ShowToast(string message, double durationSeconds)
        {
            _toastMessage = message;
            _toastRemainingSeconds = durationSeconds;
            _forceVisualRender = true;
            WakeTimerIfIdle();
        }

        private void PresentLayeredFrame()
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            double dpi = _cachedMonitorDpi;
            double zoom = (_latestProbe != null && _latestProbe.IsValidTarget && _latestProbe.DocumentZoomPercent > 10)
                ? _latestProbe.DocumentZoomPercent
                : 100.0;

            EnsureCanvasBufferCapacity(dpi, zoom);

            double toastAlpha = _toastRemainingSeconds > 0.0
                ? Math.Min(1.0, _toastRemainingSeconds / 0.35)
                : 0.0;

            int winLeft = (int)Math.Round(_stateMachine.CurrentPosition.X - _currentCanvasSize * 0.5);
            int winTop = (int)Math.Round(_stateMachine.CurrentPosition.Y - _currentCanvasSize * 0.5);

            VisualStateSignature currentSignature = CaptureCurrentVisualSignature(dpi, zoom, toastAlpha);
            bool visualChanged = _forceVisualRender || !_hasRenderedOnce || !currentSignature.Equals(_lastRenderedSignature);
            bool positionChanged = !_hasRenderedOnce || winLeft != _lastPresentedLeft || winTop != _lastPresentedTop;

            if (!visualChanged && !positionChanged)
            {
                SkippedIdleFrameCount++;
                return;
            }

            if (visualChanged)
            {
                _renderer.RenderFrame(
                    _frameGraphics,
                    _currentCanvasSize,
                    _currentCanvasSize,
                    _stateMachine,
                    _activeSeal,
                    _latestProbe,
                    dpi,
                    _toastMessage,
                    toastAlpha);

                CopyFrameBufferToPremultipliedDib();
                _lastRenderedSignature = currentSignature;
                _forceVisualRender = false;
                _hasRenderedOnce = true;
                FullRenderFrameCount++;
            }
            else
            {
                PositionOnlyPresentCount++;
            }

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                var topPos = new NativeMethods.POINT(winLeft, winTop);
                var size = new NativeMethods.SIZE(_currentCanvasSize, _currentCanvasSize);
                var pointSource = new NativeMethods.POINT(0, 0);
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };

                NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref topPos,
                    ref size,
                    _persistentMemDc,
                    ref pointSource,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA);

                _lastPresentedLeft = winLeft;
                _lastPresentedTop = winTop;
            }
            finally
            {
                if (screenDc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private double QueryCurrentMonitorDpi()
        {
            var pt = new NativeMethods.POINT(
                (int)Math.Round(_stateMachine.CurrentPosition.X),
                (int)Math.Round(_stateMachine.CurrentPosition.Y));
            return NativeMethods.QueryDpiForPointOrWindow(pt, Handle);
        }

        private static Vector2D GetCursorScreenVector()
        {
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
                return new Vector2D(pt.X, pt.Y);
            Point wfPt = Cursor.Position;
            return new Vector2D(wfPt.X, wfPt.Y);
        }

        public void ShutdownAndExit()
        {
            _frameTimer?.Stop();
            _globalHook?.Uninstall();
            if (_activeMenu != null && !_activeMenu.IsDisposed)
                _activeMenu.Close();
            Close();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                if (_stateMachine != null)
                {
                    _stateMachine.StampTriggered -= OnStampTriggered;
                    _stateMachine.StampImpactCommitted -= OnStampImpactCommitted;
                }
                _frameTimer?.Stop();
                _frameTimer?.Dispose();
                _globalHook?.Dispose();
                _asyncHostWorker?.Dispose();
                ReleaseCanvasAndDibResources();
                _renderer?.Dispose();
                _assetRepository?.Dispose();
                foreach (SealAsset s in _seals)
                {
                    s.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
