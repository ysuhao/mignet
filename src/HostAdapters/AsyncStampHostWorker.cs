using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using DesktopStamp.Core;
using DesktopStamp.Imaging;
using DesktopStamp.UI;

namespace DesktopStamp.HostAdapters
{
    public sealed class AsyncHostProbeSnapshot
    {
        public AsyncHostProbeSnapshot()
        {
        }

        public AsyncHostProbeSnapshot(
            Vector2D screenPointPx,
            StampTargetProbe probe,
            long viewEpoch,
            long cancelEpoch,
            long requestedTimestamp,
            long completedTimestamp,
            bool isStaleAtCompletion = false)
        {
            ScreenPointPx = screenPointPx;
            Probe = probe;
            ViewEpoch = (int)viewEpoch;
            CancelEpoch = (int)cancelEpoch;
            RequestedTimestamp = requestedTimestamp;
            CompletedTimestamp = completedTimestamp;
            IsStaleAtCompletion = isStaleAtCompletion;
        }

        public StampTargetProbe Probe { get; set; }
        public Vector2D ScreenPointPx { get; set; }
        public int ViewEpoch { get; set; }
        public int CancelEpoch { get; set; }
        public long RequestedTimestamp { get; set; }
        public long CompletedTimestamp { get; set; }
        public double DurationMs { get; set; }
        public bool IsStaleAtCompletion { get; set; }
    }

    /// <summary>
    /// Immutable stamp request snapshot captured at accepted LEFT MOUSE DOWN and retained through
    /// the physical plunge impact (~60ms later) and background STA COM execution.
    /// </summary>
    public sealed class AsyncStampWorkItem
    {
        public int RequestId { get; set; }
        public Vector2D ScreenPointPx { get; set; }
        public IntPtr OverlayWindowHandle { get; set; }
        public IntPtr ExpectedTargetHwnd { get; set; }
        public IntPtr ExpectedRootHwnd { get; set; }
        public SealAsset Seal { get; set; }
        public double PhysicalSizeMm { get; set; }
        public double BaseAngleDeg { get; set; }
        public double JitterAngleDeg { get; set; }
        public int ImpressionSeed { get; set; }
        public int ViewEpoch { get; set; }
        public int CancelEpoch { get; set; }
        public long CreatedTimestampTicks { get; set; }
        public double MaxAllowedAgeMs { get; set; } = 2500.0;
        public Action OnBeforeMutationHook { get; set; }
        public Action OnDuringConfigurationHook { get; set; }

        // Two-phase click stamping (see AsyncStampHostWorker.EnqueueAwaitingImpact): closed while the press
        // animation has not yet reached paper impact; the document may only be mutated once it opens.
        internal readonly ManualResetEventSlim ImpactGate = new ManualResetEventSlim(true);
        internal volatile string PreImpactAbortReason;
        private int _completionClaimed;

        /// <summary>Exactly one of the worker or a pre-impact abort reports the completion for this item.</summary>
        internal bool TryClaimCompletion() => Interlocked.Exchange(ref _completionClaimed, 1) == 0;
    }

    public sealed class AsyncStampCompletion
    {
        public int RequestId { get; set; }
        public Vector2D ScreenPointPx { get; set; }
        public StampPlacementResult Result { get; set; }
        public bool WasAbortedByEpoch { get; set; }
        public bool WasRejectedBusy { get; set; }
        public double PhysicalSizeMm { get; set; }
        public double EffectiveAngleDeg { get; set; }
        public double DurationMs { get; set; }
        public int ViewEpoch { get; set; }
        public int CancelEpoch { get; set; }
        public long CompletedTimestampTicks { get; set; }
    }

    /// <summary>
    /// Dedicated background STA worker thread for cross-process host target probing (Word COM)
    /// and physical stamp insertion. Keeps the UI and animation thread 100% non-blocking while
    /// coalescing intermediate probe requests to the latest cursor position and queuing rapid
    /// stamp requests in a bounded FIFO queue without silent drops.
    /// </summary>
    public sealed class AsyncStampHostWorker : IDisposable
    {
        public const int MaxPendingStampQueueDepth = 8;

        // Impact normally follows LEFT DOWN by ~63ms; this only bounds a lost commit signal
        private const int MaxImpactWaitMs = 1500;

        private sealed class PendingProbeSlot
        {
            public Vector2D ScreenPointPx;
            public IntPtr OverlayWindowHandle;
            public int ViewEpoch;
            public int CancelEpoch;
            public long RequestedTimestamp;
            public bool Lightweight;
        }

        private readonly CompositeStampHostRouter _hostRouter;
        private readonly object _sync = new object();
        private readonly AutoResetEvent _workSignal = new AutoResetEvent(false);
        private readonly Thread _staThread;
        private readonly Action _onResultReady;

        private PendingProbeSlot _pendingProbe;
        private readonly Queue<AsyncStampWorkItem> _pendingStamps = new Queue<AsyncStampWorkItem>();
        private readonly Queue<AsyncStampCompletion> _completedStamps = new Queue<AsyncStampCompletion>();
        private AsyncHostProbeSnapshot _latestSnapshot;

        private int _viewEpoch = 1;
        private int _cancelEpoch = 1;
        private int _nextStampRequestId = 0;
        private volatile bool _disposed;
        private volatile bool _isBusy;

        private long _totalProbeRequests;
        private long _executedProbes;
        private long _executedStamps;
        private long _abortedStamps;
        private long _rejectedBusyStamps;
        private double _lastProbeDurationMs;
        private double _lastStampDurationMs;
        private string _lastDiagnosticError;

        public AsyncStampHostWorker(CompositeStampHostRouter hostRouter, Action onResultReady = null)
        {
            _hostRouter = hostRouter ?? throw new ArgumentNullException(nameof(hostRouter));
            _onResultReady = onResultReady;

            long nowTicks = Stopwatch.GetTimestamp();
            _latestSnapshot = new AsyncHostProbeSnapshot
            {
                Probe = new StampTargetProbe
                {
                    IsValidTarget = false,
                    AdapterName = "None",
                    StatusSummary = "请移到 Word 文档页面"
                },
                ScreenPointPx = new Vector2D(0, 0),
                ViewEpoch = _viewEpoch,
                CancelEpoch = _cancelEpoch,
                RequestedTimestamp = nowTicks,
                CompletedTimestamp = nowTicks,
                DurationMs = 0.0,
                IsStaleAtCompletion = true
            };

            _staThread = new Thread(WorkerThreadMain)
            {
                Name = "DesktopStamp.AsyncHostStaWorker",
                IsBackground = true
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        public int CurrentViewEpoch => Volatile.Read(ref _viewEpoch);
        public int CurrentCancelEpoch => Volatile.Read(ref _cancelEpoch);
        public bool IsBusy => _isBusy;
        public int PendingStampCount
        {
            get
            {
                lock (_sync)
                {
                    return _pendingStamps.Count;
                }
            }
        }
        public long TotalProbeRequests => Interlocked.Read(ref _totalProbeRequests);
        public long ExecutedProbes => Interlocked.Read(ref _executedProbes);
        public long CoalescedProbeRequests => Math.Max(0L, TotalProbeRequests - ExecutedProbes);
        public long ExecutedStamps => Interlocked.Read(ref _executedStamps);
        public long AbortedStamps => Interlocked.Read(ref _abortedStamps);
        public long AbortedByEpochStamps => AbortedStamps;
        public long RejectedBusyStamps => Interlocked.Read(ref _rejectedBusyStamps);
        public double LastProbeDurationMs => Volatile.Read(ref _lastProbeDurationMs);
        public double LastStampDurationMs => Volatile.Read(ref _lastStampDurationMs);
        public string LastDiagnosticError
        {
            get
            {
                lock (_sync)
                {
                    return _lastDiagnosticError;
                }
            }
        }

        public int AdvanceViewEpoch()
        {
            return Interlocked.Increment(ref _viewEpoch);
        }

        public int AdvanceCancelEpoch()
        {
            int epoch = Interlocked.Increment(ref _cancelEpoch);
            lock (_sync)
            {
                _pendingProbe = null;
            }
            return epoch;
        }

        public void RequestProbe(Vector2D screenPointPx, IntPtr overlayWindowHandle, bool lightweight = false)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _totalProbeRequests);
            int viewEpoch = Volatile.Read(ref _viewEpoch);
            int cancelEpoch = Volatile.Read(ref _cancelEpoch);
            long nowTicks = Stopwatch.GetTimestamp();

            lock (_sync)
            {
                _pendingProbe = new PendingProbeSlot
                {
                    ScreenPointPx = screenPointPx,
                    OverlayWindowHandle = overlayWindowHandle,
                    ViewEpoch = viewEpoch,
                    CancelEpoch = cancelEpoch,
                    RequestedTimestamp = nowTicks,
                    Lightweight = lightweight
                };
            }

            _workSignal.Set();
        }

        /// <summary>
        /// Captures an immutable stamp request snapshot at accepted LEFT MOUSE DOWN, locking in the
        /// click coordinate, target/root HWND, ViewEpoch, CancelEpoch, physical size, base angle,
        /// jitter angle, and seal reference before the ~60ms downward plunge animation.
        /// </summary>
        public AsyncStampWorkItem CreateClickSnapshot(
            Vector2D screenPointPx,
            IntPtr overlayWindowHandle,
            IntPtr expectedTargetHwnd,
            IntPtr expectedRootHwnd,
            SealAsset seal,
            double physicalSizeMm,
            double baseAngleDeg,
            double jitterAngleDeg,
            int impressionSeed,
            double maxAllowedAgeMs = 2500.0)
        {
            int reqId = Interlocked.Increment(ref _nextStampRequestId);
            return new AsyncStampWorkItem
            {
                RequestId = reqId,
                ScreenPointPx = screenPointPx,
                OverlayWindowHandle = overlayWindowHandle,
                ExpectedTargetHwnd = expectedTargetHwnd,
                ExpectedRootHwnd = expectedRootHwnd,
                Seal = seal,
                PhysicalSizeMm = physicalSizeMm,
                BaseAngleDeg = baseAngleDeg,
                JitterAngleDeg = jitterAngleDeg,
                ImpressionSeed = impressionSeed,
                ViewEpoch = Volatile.Read(ref _viewEpoch),
                CancelEpoch = Volatile.Read(ref _cancelEpoch),
                CreatedTimestampTicks = Stopwatch.GetTimestamp(),
                MaxAllowedAgeMs = maxAllowedAgeMs
            };
        }

        /// <summary>
        /// Enqueues a previously captured immutable click snapshot into the bounded FIFO stamp queue.
        /// Never silently overwrites an earlier pending stamp; if the queue is at capacity, records
        /// an explicit busy rejection completion.
        /// </summary>
        public int EnqueueStamp(AsyncStampWorkItem workItem)
        {
            if (_disposed || workItem == null || workItem.Seal == null)
            {
                return 0;
            }

            if (workItem.RequestId <= 0)
            {
                workItem.RequestId = Interlocked.Increment(ref _nextStampRequestId);
            }
            if (workItem.CreatedTimestampTicks <= 0)
            {
                workItem.CreatedTimestampTicks = Stopwatch.GetTimestamp();
            }

            bool rejectedBusy = false;
            lock (_sync)
            {
                if (_pendingStamps.Count >= MaxPendingStampQueueDepth && workItem.TryClaimCompletion())
                {
                    rejectedBusy = true;
                    _completedStamps.Enqueue(new AsyncStampCompletion
                    {
                        RequestId = workItem.RequestId,
                        ScreenPointPx = workItem.ScreenPointPx,
                        Result = StampPlacementResult.Rejected("AsyncWorker", "正在处理上一枚印章，请稍候再试"),
                        WasAbortedByEpoch = false,
                        WasRejectedBusy = true,
                        PhysicalSizeMm = workItem.PhysicalSizeMm,
                        EffectiveAngleDeg = StampStateMachine.NormalizeAngle(workItem.BaseAngleDeg + workItem.JitterAngleDeg),
                        DurationMs = 0.0,
                        ViewEpoch = workItem.ViewEpoch,
                        CancelEpoch = workItem.CancelEpoch,
                        CompletedTimestampTicks = Stopwatch.GetTimestamp()
                    });
                }
                else
                {
                    _pendingStamps.Enqueue(workItem);
                }
            }

            if (rejectedBusy)
            {
                Interlocked.Increment(ref _rejectedBusyStamps);
                NotifyResultReady();
                return workItem.RequestId;
            }

            _workSignal.Set();
            return workItem.RequestId;
        }

        /// <summary>
        /// Records an explicit epoch/cancel abort for a click snapshot that was invalidated before impact.
        /// </summary>
        /// <summary>
        /// Queues a click-time snapshot immediately at LEFT DOWN so the worker can do all read-only preparation
        /// (window lookup, page mapping, image encoding, view verification) while the press animation plays.
        /// The worker blocks right before Shapes.AddPicture until <see cref="CommitImpact"/> (paper impact) or
        /// <see cref="AbortSnapshotBeforeImpact"/>, so nothing is written to the document before impact.
        /// </summary>
        public int EnqueueAwaitingImpact(AsyncStampWorkItem workItem)
        {
            if (workItem == null)
            {
                return 0;
            }
            workItem.ImpactGate.Reset();
            return EnqueueStamp(workItem);
        }

        public void CommitImpact(AsyncStampWorkItem workItem)
        {
            workItem?.ImpactGate.Set();
        }

        /// <summary>Waits (off the UI thread) for paper impact before the adapter mutates the document.</summary>
        private string AwaitImpactPermit(AsyncStampWorkItem req)
        {
            var waited = Stopwatch.StartNew();
            while (!req.ImpactGate.Wait(50))
            {
                if (_disposed)
                {
                    return "已取消本次盖章";
                }
                if (waited.ElapsedMilliseconds > MaxImpactWaitMs)
                {
                    return "盖章请求已超时，请重新点击盖章";
                }
            }
            return EvaluateRequestInvalidation(req);
        }

        public void AbortSnapshotBeforeImpact(AsyncStampWorkItem workItem, string conciseChineseReason)
        {
            if (_disposed || workItem == null)
            {
                return;
            }

            string msg = string.IsNullOrWhiteSpace(conciseChineseReason)
                ? "已取消本次盖章"
                : conciseChineseReason;

            // If the item is already queued (two-phase), make the worker drop it before any mutation
            workItem.PreImpactAbortReason = msg;
            workItem.ImpactGate.Set();
            if (!workItem.TryClaimCompletion())
            {
                return;
            }

            Interlocked.Increment(ref _abortedStamps);
            lock (_sync)
            {
                _completedStamps.Enqueue(new AsyncStampCompletion
                {
                    RequestId = workItem.RequestId,
                    ScreenPointPx = workItem.ScreenPointPx,
                    Result = StampPlacementResult.Rejected("AsyncWorker", msg, abortedByEpoch: true),
                    WasAbortedByEpoch = true,
                    WasRejectedBusy = false,
                    PhysicalSizeMm = workItem.PhysicalSizeMm,
                    EffectiveAngleDeg = StampStateMachine.NormalizeAngle(workItem.BaseAngleDeg + workItem.JitterAngleDeg),
                    DurationMs = 0.0,
                    ViewEpoch = workItem.ViewEpoch,
                    CancelEpoch = workItem.CancelEpoch,
                    CompletedTimestampTicks = Stopwatch.GetTimestamp()
                });
            }

            NotifyResultReady();
        }

        public int RequestStamp(
            Vector2D screenPointPx,
            IntPtr overlayWindowHandle,
            SealAsset seal,
            double physicalSizeMm,
            double baseAngleDeg,
            double jitterAngleDeg,
            int impressionSeed,
            IntPtr expectedTargetHwnd = default,
            IntPtr expectedRootHwnd = default,
            Action onBeforeMutationHook = null,
            Action onDuringConfigurationHook = null)
        {
            if (_disposed || seal == null)
            {
                return 0;
            }

            AsyncStampWorkItem item = CreateClickSnapshot(
                screenPointPx,
                overlayWindowHandle,
                expectedTargetHwnd,
                expectedRootHwnd,
                seal,
                physicalSizeMm,
                baseAngleDeg,
                jitterAngleDeg,
                impressionSeed);

            item.OnBeforeMutationHook = onBeforeMutationHook;
            item.OnDuringConfigurationHook = onDuringConfigurationHook;
            return EnqueueStamp(item);
        }

        public AsyncHostProbeSnapshot GetLatestProbeSnapshot()
        {
            lock (_sync)
            {
                return _latestSnapshot;
            }
        }

        public bool TryConsumeStampCompletion(out AsyncStampCompletion completion)
        {
            lock (_sync)
            {
                if (_completedStamps.Count > 0)
                {
                    completion = _completedStamps.Dequeue();
                    return true;
                }
            }

            completion = null;
            return false;
        }

        private void WorkerThreadMain()
        {
            // Note: IOleMessageFilter bounds retries when Word rejects an incoming call with SERVERCALL_RETRYLATER
            // (e.g. during modal dialogs); once Word accepts a synchronous COM call into its STA, pre-mutation and
            // post-configuration guards in WordStampHostAdapter protect document state upon return.
            NativeMethods.RegisterStandardOleMessageFilter(retryDelayMs: 50, timeoutMs: 2200);

            try
            {
                while (!_disposed)
                {
                    _workSignal.WaitOne(250);
                    if (_disposed)
                    {
                        break;
                    }

                    ProcessPendingWorkLoop();
                }
            }
            finally
            {
                WordStampHostAdapter.ClearThreadLocalCache();
                NativeMethods.RevokeOleMessageFilter();
            }
        }

        private void ProcessPendingWorkLoop()
        {
            while (!_disposed)
            {
                AsyncStampWorkItem stampReq = null;
                PendingProbeSlot probeReq = null;

                lock (_sync)
                {
                    if (_pendingStamps.Count > 0)
                    {
                        stampReq = _pendingStamps.Dequeue();
                    }
                    else if (_pendingProbe != null)
                    {
                        probeReq = _pendingProbe;
                        _pendingProbe = null;
                    }
                }

                if (stampReq == null && probeReq == null)
                {
                    _isBusy = false;
                    break;
                }

                _isBusy = true;

                if (stampReq != null)
                {
                    ExecuteStampRequest(stampReq);
                }
                else if (probeReq != null)
                {
                    ExecuteProbeRequest(probeReq);
                }
            }
        }

        private string EvaluateRequestInvalidation(AsyncStampWorkItem req)
        {
            string preImpactAbortReason = req.PreImpactAbortReason;
            if (preImpactAbortReason != null)
            {
                return preImpactAbortReason;
            }

            if (_disposed || req.CancelEpoch != Volatile.Read(ref _cancelEpoch))
            {
                return "已取消本次盖章";
            }

            if (req.ViewEpoch != Volatile.Read(ref _viewEpoch))
            {
                return "文档视图已变化，已取消本次盖章";
            }

            if (req.CreatedTimestampTicks > 0 && req.MaxAllowedAgeMs > 0.0)
            {
                double ageMs = (Stopwatch.GetTimestamp() - req.CreatedTimestampTicks) * 1000.0 / Stopwatch.Frequency;
                if (ageMs > req.MaxAllowedAgeMs)
                {
                    return "盖章请求已超时，请重新点击盖章";
                }
            }

            return null;
        }

        private void ExecuteStampRequest(AsyncStampWorkItem req)
        {
            long startTicks = Stopwatch.GetTimestamp();
            StampPlacementResult result;
            bool aborted = false;
            bool reachedHost = false;

            try
            {
                string initialInvalidReason = EvaluateRequestInvalidation(req);
                if (!string.IsNullOrEmpty(initialInvalidReason))
                {
                    aborted = true;
                    result = StampPlacementResult.Rejected("AsyncWorker", initialInvalidReason, abortedByEpoch: true);
                }
                else
                {
                    using (Bitmap impressionBmp = SealImageProcessor.CreateImpressionBitmap(req.Seal, req.ImpressionSeed))
                    {
                        string postImageInvalidReason = EvaluateRequestInvalidation(req);
                        if (!string.IsNullOrEmpty(postImageInvalidReason))
                        {
                            aborted = true;
                            result = StampPlacementResult.Rejected("AsyncWorker", postImageInvalidReason, abortedByEpoch: true);
                        }
                        else
                        {
                            var hostRequest = new StampRequest
                            {
                                RequestId = req.RequestId,
                                ScreenPointPx = req.ScreenPointPx,
                                ExpectedTargetHwnd = req.ExpectedTargetHwnd,
                                ExpectedRootHwnd = req.ExpectedRootHwnd,
                                Seal = req.Seal,
                                ImpressionBitmap = impressionBmp,
                                PhysicalSizeMm = req.PhysicalSizeMm,
                                BaseAngleDeg = req.BaseAngleDeg,
                                JitterAngleDeg = req.JitterAngleDeg,
                                ImpressionSeed = req.ImpressionSeed,
                                ClickViewEpoch = req.ViewEpoch,
                                ClickCancelEpoch = req.CancelEpoch,
                                CreatedTimestampTicks = req.CreatedTimestampTicks,
                                MaxAllowedAgeMs = req.MaxAllowedAgeMs,
                                CheckInvalidationReason = () => EvaluateRequestInvalidation(req),
                                AwaitMutationPermit = () => AwaitImpactPermit(req),
                                OnBeforeMutationHook = req.OnBeforeMutationHook,
                                OnDuringConfigurationHook = req.OnDuringConfigurationHook
                            };

                            result = _hostRouter.TryApplyStamp(hostRequest, req.OverlayWindowHandle);
                            reachedHost = true;
                            if (result != null && result.WasAbortedByEpoch)
                            {
                                aborted = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RecordDiagnostic("ExecuteStampRequest", ex);
                result = StampPlacementResult.Rejected("AsyncWorker", "当前无法完成盖章，请重试");
            }

            double durationMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastStampDurationMs, durationMs);

            double queuedMs = req.CreatedTimestampTicks > 0
                ? (startTicks - req.CreatedTimestampTicks) * 1000.0 / Stopwatch.Frequency
                : 0.0;
            StampDiagnosticsLog.Append(
                $"stamp #{req.RequestId} {(result != null && result.Success ? "OK" : "FAIL")} " +
                $"msg=\"{result?.Message}\" queued={queuedMs:F0}ms run={durationMs:F0}ms {result?.Diagnostics}");

            if (!req.TryClaimCompletion())
            {
                // A pre-impact abort already reported (and counted) this item
                return;
            }

            if (aborted)
            {
                Interlocked.Increment(ref _abortedStamps);
            }
            else if (reachedHost)
            {
                Interlocked.Increment(ref _executedStamps);
            }

            lock (_sync)
            {
                _completedStamps.Enqueue(new AsyncStampCompletion
                {
                    RequestId = req.RequestId,
                    ScreenPointPx = req.ScreenPointPx,
                    Result = result,
                    WasAbortedByEpoch = aborted || (result != null && result.WasAbortedByEpoch),
                    WasRejectedBusy = false,
                    PhysicalSizeMm = req.PhysicalSizeMm,
                    EffectiveAngleDeg = StampStateMachine.NormalizeAngle(req.BaseAngleDeg + req.JitterAngleDeg),
                    DurationMs = durationMs,
                    ViewEpoch = req.ViewEpoch,
                    CancelEpoch = req.CancelEpoch,
                    CompletedTimestampTicks = Stopwatch.GetTimestamp()
                });
            }

            NotifyResultReady();
        }

        private void ExecuteProbeRequest(PendingProbeSlot req)
        {
            long startTicks = Stopwatch.GetTimestamp();
            StampTargetProbe probe;
            try
            {
                probe = _hostRouter.ProbeAtScreenPoint(req.ScreenPointPx, req.OverlayWindowHandle, req.Lightweight);
            }
            catch (Exception ex)
            {
                RecordDiagnostic("ExecuteProbeRequest", ex);
                probe = new StampTargetProbe
                {
                    IsValidTarget = false,
                    AdapterName = "Error",
                    StatusSummary = "请移到 Word 文档页面"
                };
            }

            Interlocked.Increment(ref _executedProbes);
            long completedTicks = Stopwatch.GetTimestamp();
            double durationMs = (completedTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastProbeDurationMs, durationMs);

            bool staleAtCompletion =
                req.CancelEpoch != Volatile.Read(ref _cancelEpoch) ||
                req.ViewEpoch != Volatile.Read(ref _viewEpoch);

            var snapshot = new AsyncHostProbeSnapshot
            {
                Probe = probe,
                ScreenPointPx = req.ScreenPointPx,
                ViewEpoch = req.ViewEpoch,
                CancelEpoch = req.CancelEpoch,
                RequestedTimestamp = req.RequestedTimestamp,
                CompletedTimestamp = completedTicks,
                DurationMs = durationMs,
                IsStaleAtCompletion = staleAtCompletion
            };

            lock (_sync)
            {
                // Do not overwrite an already-published snapshot from a newer epoch with an obsolete completion
                if (!staleAtCompletion ||
                    _latestSnapshot == null ||
                    snapshot.ViewEpoch >= _latestSnapshot.ViewEpoch)
                {
                    _latestSnapshot = snapshot;
                }
            }

            NotifyResultReady();
        }

        private void RecordDiagnostic(string context, Exception ex)
        {
            string entry = $"[{DateTimeOffset.Now:O}] {context}: {ex?.GetType().Name}: {ex?.Message}";
            lock (_sync)
            {
                _lastDiagnosticError = entry;
            }
            try
            {
                Trace.WriteLine(entry);
            }
            catch
            {
            }
        }

        private void NotifyResultReady()
        {
            try
            {
                _onResultReady?.Invoke();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _workSignal.Set();

            try
            {
                if (_staThread.IsAlive)
                {
                    _staThread.Join(350);
                }
            }
            catch
            {
            }

            try
            {
                _workSignal.Dispose();
            }
            catch
            {
            }
        }
    }
}
