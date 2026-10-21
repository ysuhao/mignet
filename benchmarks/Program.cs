using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DesktopStamp.Core;
using DesktopStamp.HostAdapters;
using DesktopStamp.Imaging;
using DesktopStamp.UI;

namespace DesktopStamp.Benchmarks
{
    public sealed class ScenarioMetrics
    {
        public string ScenarioName { get; set; }
        public double DurationSeconds { get; set; }
        public int TickSampleCount { get; set; }
        public long LayeredPresentCount { get; set; }
        public long FullBitmapRedrawCount { get; set; }
        public long PositionOnlyPresentCount { get; set; }
        public long SkippedIdleFrameCount { get; set; }
        public int CanvasSizePx { get; set; }
        public double EffectiveFps => DurationSeconds > 0 ? TickSampleCount / DurationSeconds : 0.0;
        public double EffectivePresentFps => DurationSeconds > 0 ? LayeredPresentCount / DurationSeconds : 0.0;
        public double EffectiveRedrawFps => DurationSeconds > 0 ? FullBitmapRedrawCount / DurationSeconds : 0.0;

        public double CpuTimeMs { get; set; }
        public double CpuPercentOneCore { get; set; }
        public double CpuPercentWholeMachine { get; set; }

        public long WorkingSetBytes { get; set; }
        public long PrivateMemoryBytes { get; set; }
        public long ManagedMemoryDeltaBytes { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }

        public uint GdiHandlesStart { get; set; }
        public uint GdiHandlesEnd { get; set; }
        public uint GdiHandlesPeak { get; set; }
        public uint UserHandlesStart { get; set; }
        public uint UserHandlesEnd { get; set; }
        public uint UserHandlesPeak { get; set; }

        public double TickDurationP50Ms { get; set; }
        public double TickDurationP95Ms { get; set; }
        public double TickDurationP99Ms { get; set; }
        public double TickDurationMaxMs { get; set; }

        public double FrameIntervalP50Ms { get; set; }
        public double FrameIntervalP95Ms { get; set; }
        public double FrameIntervalP99Ms { get; set; }
        public double FrameIntervalMaxMs { get; set; }

        public int PointerQueueSampleCount { get; set; }
        public double PointerQueueDelayP50Ms { get; set; }
        public double PointerQueueDelayP95Ms { get; set; }
        public double PointerQueueDelayP99Ms { get; set; }
        public double PointerQueueDelayMaxMs { get; set; }

        public string Notes { get; set; }
    }

    public static class Program
    {
        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;
        private const uint PM_REMOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public NativeMethods.POINT pt;
        }

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        private static void ParseCommandLineArgs(string[] args, out string label, out string outJsonPath)
        {
            label = "unknown";
            outJsonPath = null;
            if (args == null || args.Length == 0)
                return;

            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--label", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-l", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        label = args[++i];
                }
                else if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        outJsonPath = args[++i];
                }
                else if (a != null && a.StartsWith("--label=", StringComparison.OrdinalIgnoreCase))
                {
                    label = a.Substring("--label=".Length);
                }
                else if (a != null && a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                {
                    outJsonPath = a.Substring("--output=".Length);
                }
                else
                {
                    positional.Add(a);
                }
            }

            if (label == "unknown" && positional.Count > 0)
                label = positional[0];
            if (string.IsNullOrWhiteSpace(outJsonPath) && positional.Count > 1)
                outJsonPath = positional[1];
        }

        private static void ResetStateMachineToIdle(StampStateMachine sm, Vector2D parkedPos)
        {
            if (sm.CurrentState != StampState.Idle)
            {
                if (sm.CurrentState != StampState.Returning)
                {
                    sm.BeginReturnToParked();
                }
                for (int i = 0; i < 20 && sm.CurrentState != StampState.Idle; i++)
                {
                    sm.Update(0.08);
                }
            }
            sm.SetParkedPosition(parkedPos, snapIfIdle: true);
            if (sm.CurrentState != StampState.Idle)
            {
                throw new InvalidOperationException($"Failed to reset state machine to Idle (CurrentState={sm.CurrentState}).");
            }
        }

        private static int RunChildNeutralHostWindow(string[] args)
        {
            int left = int.Parse(args[1], CultureInfo.InvariantCulture);
            int top = int.Parse(args[2], CultureInfo.InvariantCulture);
            int width = int.Parse(args[3], CultureInfo.InvariantCulture);
            int height = int.Parse(args[4], CultureInfo.InvariantCulture);
            int parentPid = int.Parse(args[5], CultureInfo.InvariantCulture);

            using (var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(left, top, width, height),
                BackColor = Color.FromArgb(245, 245, 248),
                Text = "NeutralBoundaryBenchmarkHost"
            })
            using (var parentWatchTimer = new System.Windows.Forms.Timer { Interval = 150 })
            {
                parentWatchTimer.Tick += (s, e) =>
                {
                    try
                    {
                        using (Process p = Process.GetProcessById(parentPid))
                        {
                            if (p.HasExited)
                                form.Close();
                        }
                    }
                    catch
                    {
                        form.Close();
                    }
                };
                form.Shown += (s, e) =>
                {
                    NativeMethods.SetWindowPos(
                        form.Handle,
                        NativeMethods.HWND_TOPMOST,
                        left,
                        top,
                        width,
                        height,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    parentWatchTimer.Start();
                };
                Application.Run(form);
            }
            return 0;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null &&
                args.Length >= 6 &&
                string.Equals(args[0], "--neutral-host-window", StringComparison.OrdinalIgnoreCase))
            {
                return RunChildNeutralHostWindow(args);
            }

            ParseCommandLineArgs(args, out string label, out string outJsonPath);

            int logicalCores = Math.Max(1, Environment.ProcessorCount);
            Assembly stampAsm = typeof(StampOverlayForm).Assembly;
            string asmPath = stampAsm.Location;

            Console.WriteLine("========================================================================");
            Console.WriteLine($" DesktopStamp Benchmark Harness — Build Label: {label}");
            Console.WriteLine($" Assembly: {asmPath}");
            Console.WriteLine($" Logical Processors: {logicalCores}");
            Console.WriteLine("========================================================================");

            var allResults = new List<ScenarioMetrics>();

            // 1. Warmup Renderer & StateMachine offscreen
            Console.WriteLine("==> Warming up JIT, GDI+, and AssetRepository...");
            WarmupOffscreen();

            // 2. Offscreen Renderer Throughput & High-DPI/High-Zoom Benchmark
            allResults.Add(RunOffscreenRenderBenchmark("00_OffscreenRender_460px_96Dpi", 460, 96.0, 100.0, false, 1.8, logicalCores));
            allResults.Add(RunOffscreenRenderBenchmark("08_OffscreenRender_HighDpi144_Zoom200_WideSeal", 864, 144.0, 200.0, true, 1.8, logicalCores));

            // 3. Live Native Overlay + Message Loop Scenarios (Idle, Hover, Drag)
            using (var overlay = new StampOverlayForm())
            {
                overlay.Show();
                PumpNativeMessagesBounded(0.15);

                // Ensure low-level hook doesn't trap desktop input during benchmark
                overlay.InputHook?.Uninstall();

                allResults.Add(RunOverlayScenario(
                    overlay,
                    "01_SettledIdle_NativeOverlay",
                    durationSec: 2.2,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        var awayPos = new Vector2D(cursor.X + 320, cursor.Y + 220);
                        form.StateMachine.SetParkedPosition(awayPos, snapIfIdle: true);
                        form.StateMachine.SetHoverInIdle(false);
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.45);
                    },
                    perStep: null));

                allResults.Add(RunOverlayScenario(
                    overlay,
                    "02_IdleHoverBreathing_NativeOverlay",
                    durationSec: 2.2,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        // Position ParkedPosition right at real cursor so unmodified v1 OnFrameTimerTick sees overBody == true
                        form.StateMachine.SetParkedPosition(cursor, snapIfIdle: true);
                        form.StateMachine.SetHoverInIdle(true);
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.30);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        form.StateMachine.SetHoverInIdle(true);
                    }));

                allResults.Add(RunOverlayScenario(
                    overlay,
                    "03_MovingDragPickup_NativeOverlay",
                    durationSec: 2.2,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        var startPos = new Vector2D(cursor.X + 240, cursor.Y + 160);
                        form.StateMachine.SetParkedPosition(startPos, snapIfIdle: true);
                        form.StateMachine.BeginPickupDrag(cursor);
                        form.SyncPassThroughWindowStyle();
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        double angle = elapsedSec * 5.0;
                        var pt = new Vector2D(
                            cursor.X + Math.Cos(angle) * 140.0,
                            cursor.Y + Math.Sin(angle * 1.5) * 95.0);
                        form.StateMachine.OnPointerMove(pt);
                    }));

                // 4. Isolated Disposable Live Word Scenarios (P0 Stutter & Responsiveness Verification)
                RunLiveWordBenchmarks(overlay, allResults, logicalCores);

                overlay.Close();
            }

            PrintSummaryTable(allResults);

            if (!string.IsNullOrWhiteSpace(outJsonPath))
            {
                SaveResultsJson(outJsonPath, label, asmPath, logicalCores, allResults);
                Console.WriteLine($"==> Saved machine-readable benchmark results to: {outJsonPath}");
            }

            return 0;
        }

        private static void WarmupOffscreen()
        {
            using (var repo = new AssetRepository())
            {
                var renderer = new StampRenderer(repo);
                var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), 42);
                var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                using (var bmp = new Bitmap(460, 460, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    for (int i = 0; i < 12; i++)
                    {
                        sm.Update(0.016);
                        renderer.RenderFrame(g, 460, 460, sm, seals[0], null, 96.0);
                    }
                }
                foreach (var s in seals) s.Dispose();
            }
        }

        private static ScenarioMetrics RunOffscreenRenderBenchmark(
            string name,
            int canvasSize,
            double dpi,
            double zoomPercent,
            bool useWideSeal,
            double durationSec,
            int logicalCores)
        {
            using (var repo = new AssetRepository())
            {
                var renderer = new StampRenderer(repo);
                var sm = new StampStateMachine(new StampConfiguration(), new Vector2D(500, 500), 42);
                sm.BeginPickupDrag(new Vector2D(400, 400));
                sm.CompletePickupToAiming(new Vector2D(400, 400));
                sm.Update(0.2);

                var seals = SealImageProcessor.CreateBuiltInSampleSeals();
                SealAsset activeSeal = useWideSeal
                    ? SealImageProcessor.CreateWideSignatureSampleSeal(40.0)
                    : seals[0];

                int actualCanvas = renderer.ComputeRequiredCanvasSize(40.0, activeSeal.InkAspectRatio, dpi, zoomPercent);
                if (actualCanvas > 0) canvasSize = actualCanvas;

                var probe = new StampTargetProbe
                {
                    IsValidTarget = true,
                    AdapterName = "WordStampHostAdapter",
                    MonitorDpi = dpi,
                    DocumentZoomPercent = zoomPercent,
                    PageNumber = 1,
                    StatusSummary = "第 1 页"
                };

                using (var bmp = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        renderer.RenderFrame(g, canvasSize, canvasSize, sm, activeSeal, probe, dpi);
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    Process proc = Process.GetCurrentProcess();
                    IntPtr hProc = proc.Handle;
                    uint gdiStart = GetGuiResources(hProc, GR_GDIOBJECTS);
                    uint userStart = GetGuiResources(hProc, GR_USEROBJECTS);
                    uint gdiPeak = gdiStart;
                    uint userPeak = userStart;

                    int gen0Start = GC.CollectionCount(0);
                    int gen1Start = GC.CollectionCount(1);
                    int gen2Start = GC.CollectionCount(2);
                    long memStart = GC.GetTotalMemory(false);
                    TimeSpan cpuStart = proc.TotalProcessorTime;

                    var frameDurations = new List<double>(2048);
                    var swTotal = Stopwatch.StartNew();
                    var swFrame = new Stopwatch();

                    int frameCount = 0;
                    while (swTotal.Elapsed.TotalSeconds < durationSec)
                    {
                        double t = swTotal.Elapsed.TotalSeconds;
                        sm.OnPointerMove(new Vector2D(400 + Math.Cos(t * 6.0) * 50.0, 400 + Math.Sin(t * 6.0) * 50.0));
                        sm.RotateByWheel((frameCount % 2 == 0) ? 1 : -1);
                        sm.Update(1.0 / 120.0);

                        swFrame.Restart();
                        renderer.RenderFrame(g, canvasSize, canvasSize, sm, activeSeal, probe, dpi);
                        swFrame.Stop();

                        frameDurations.Add(swFrame.Elapsed.TotalMilliseconds);
                        frameCount++;

                        if ((frameCount & 31) == 0)
                        {
                            uint gdiNow = GetGuiResources(hProc, GR_GDIOBJECTS);
                            uint userNow = GetGuiResources(hProc, GR_USEROBJECTS);
                            if (gdiNow > gdiPeak) gdiPeak = gdiNow;
                            if (userNow > userPeak) userPeak = userNow;
                        }
                    }

                    swTotal.Stop();
                    proc.Refresh();
                    TimeSpan cpuDelta = proc.TotalProcessorTime - cpuStart;
                    long memEnd = GC.GetTotalMemory(false);
                    uint gdiEnd = GetGuiResources(hProc, GR_GDIOBJECTS);
                    uint userEnd = GetGuiResources(hProc, GR_USEROBJECTS);

                    if (useWideSeal) activeSeal.Dispose();
                    foreach (var s in seals) s.Dispose();

                    double wallSec = swTotal.Elapsed.TotalSeconds;
                    double cpuCorePct = (cpuDelta.TotalSeconds / wallSec) * 100.0;

                    var m = new ScenarioMetrics
                    {
                        ScenarioName = name,
                        DurationSeconds = wallSec,
                        TickSampleCount = frameCount,
                        LayeredPresentCount = frameCount,
                        FullBitmapRedrawCount = frameCount,
                        PositionOnlyPresentCount = 0,
                        SkippedIdleFrameCount = 0,
                        CanvasSizePx = canvasSize,
                        CpuTimeMs = cpuDelta.TotalMilliseconds,
                        CpuPercentOneCore = cpuCorePct,
                        CpuPercentWholeMachine = cpuCorePct / logicalCores,
                        WorkingSetBytes = proc.WorkingSet64,
                        PrivateMemoryBytes = proc.PrivateMemorySize64,
                        ManagedMemoryDeltaBytes = Math.Max(0, memEnd - memStart),
                        Gen0Collections = GC.CollectionCount(0) - gen0Start,
                        Gen1Collections = GC.CollectionCount(1) - gen1Start,
                        Gen2Collections = GC.CollectionCount(2) - gen2Start,
                        GdiHandlesStart = gdiStart,
                        GdiHandlesEnd = gdiEnd,
                        GdiHandlesPeak = Math.Max(gdiPeak, gdiEnd),
                        UserHandlesStart = userStart,
                        UserHandlesEnd = userEnd,
                        UserHandlesPeak = Math.Max(userPeak, userEnd),
                        TickDurationP50Ms = Percentile(frameDurations, 0.50),
                        TickDurationP95Ms = Percentile(frameDurations, 0.95),
                        TickDurationP99Ms = Percentile(frameDurations, 0.99),
                        TickDurationMaxMs = Percentile(frameDurations, 1.00),
                        FrameIntervalP50Ms = Percentile(frameDurations, 0.50),
                        FrameIntervalP95Ms = Percentile(frameDurations, 0.95),
                        FrameIntervalP99Ms = Percentile(frameDurations, 0.99),
                        FrameIntervalMaxMs = Percentile(frameDurations, 1.00),
                        Notes = $"Offscreen RenderFrame ({canvasSize}x{canvasSize}px, DPI={dpi}, Zoom={zoomPercent}%)"
                    };

                    Console.WriteLine(
                        $"  [{name}] frames={m.TickSampleCount} ({m.EffectiveFps:F1} FPS), " +
                        $"frameP50={m.TickDurationP50Ms:F2}ms, frameP95={m.TickDurationP95Ms:F2}ms, frameMax={m.TickDurationMaxMs:F2}ms");

                    return m;
                }
            }
        }

        private static void RunLiveWordBenchmarks(
            StampOverlayForm overlay,
            List<ScenarioMetrics> results,
            int logicalCores)
        {
            Type wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                Console.WriteLine("WARN: Word.Application not registered; skipping live Word benchmark scenarios.");
                return;
            }

            dynamic wordApp = null;
            dynamic doc = null;
            Process neutralHostProcess = null;

            try
            {
                Console.WriteLine("==> Launching isolated disposable Word instance for live P0 stutter measurement...");
                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = true;
                wordApp.DisplayAlerts = 0;

                NativeMethods.GetCursorPos(out NativeMethods.POINT realCursor);
                Screen scr = Screen.FromPoint(new Point(realCursor.X, realCursor.Y));
                Rectangle work = scr.WorkingArea;

                // Keep Word window compact (740x680) so there is guaranteed disjoint screen space outside Word's root OpusApp HWND
                int winW = Math.Min(740, Math.Max(560, work.Width - 420));
                int winH = Math.Min(680, Math.Max(520, work.Height - 80));
                int winLeft = Math.Max(work.Left + 300, Math.Min(work.Right - winW - 20, realCursor.X - 180));
                int winTop = Math.Max(work.Top + 20, Math.Min(work.Bottom - winH - 20, realCursor.Y - (int)(winH * 0.55)));

                wordApp.WindowState = 0; // wdWindowStateNormal
                wordApp.Left = (int)Math.Round(winLeft * 72.0 / 96.0);
                wordApp.Top = (int)Math.Round(winTop * 72.0 / 96.0);
                wordApp.Width = (int)Math.Round(winW * 72.0 / 96.0);
                wordApp.Height = (int)Math.Round(winH * 72.0 / 96.0);

                doc = wordApp.Documents.Add();
                dynamic win = doc.ActiveWindow;
                win.View.Type = 3; // wdPrintView
                win.View.Zoom.Percentage = 100;

                dynamic sel = wordApp.Selection;
                var paragraphBuilder = new StringBuilder();
                for (int line = 1; line <= 28; line++)
                {
                    paragraphBuilder.AppendFormat("第 1 页合同正文测试段落 Line {0:D2} — 桌面物理印章 Word COM 实时瞄准与抗卡顿基准测试文档。\r\n", line);
                }
                sel.TypeText(paragraphBuilder.ToString());
                sel.InsertBreak(7); // wdPageBreak
                paragraphBuilder.Clear();
                for (int line = 1; line <= 28; line++)
                {
                    paragraphBuilder.AppendFormat("第 2 页补充协议正文测试段落 Line {0:D2} — 验证滚动、缩放与多页连续瞄准响应延迟。\r\n", line);
                }
                sel.TypeText(paragraphBuilder.ToString());
                sel.InsertBreak(7);
                paragraphBuilder.Clear();
                for (int line = 1; line <= 24; line++)
                {
                    paragraphBuilder.AppendFormat("第 3 页签署页测试段落 Line {0:D2} — 盖章位置与非阻塞坐标映射测试。\r\n", line);
                }
                sel.TypeText(paragraphBuilder.ToString());

                dynamic page1Range = doc.Paragraphs[6].Range;
                page1Range.Collapse(1);
                win.ScrollIntoView(page1Range, true);
                PumpNativeMessagesBounded(0.20);

                int anchorX = 0, anchorY = 0, anchorW = 0, anchorH = 0;
                win.GetPoint(out anchorX, out anchorY, out anchorW, out anchorH, page1Range);
                var validWordPagePoint = new Vector2D(anchorX + 100, anchorY + 70);

                // Position the isolated Word window so that its valid page point sits directly under realCursor
                // (without ever moving the user's real mouse cursor!)
                IntPtr wordRootHwnd = new IntPtr(Convert.ToInt32(win.Hwnd));
                Rectangle wordRootRect = Rectangle.Empty;
                int dxCursor = realCursor.X - (int)validWordPagePoint.X;
                int dyCursor = realCursor.Y - (int)validWordPagePoint.Y;
                if (NativeMethods.GetWindowRect(wordRootHwnd, out NativeMethods.RECT wr))
                {
                    NativeMethods.SetWindowPos(
                        wordRootHwnd,
                        IntPtr.Zero,
                        wr.Left + dxCursor,
                        wr.Top + dyCursor,
                        wr.Right - wr.Left,
                        wr.Bottom - wr.Top,
                        NativeMethods.SWP_NOACTIVATE);
                    PumpNativeMessagesBounded(0.18);
                    win.GetPoint(out anchorX, out anchorY, out anchorW, out anchorH, page1Range);
                    validWordPagePoint = new Vector2D(anchorX + 100, anchorY + 70);
                    if (NativeMethods.GetWindowRect(wordRootHwnd, out NativeMethods.RECT wrAfter))
                    {
                        wordRootRect = Rectangle.FromLTRB(wrAfter.Left, wrAfter.Top, wrAfter.Right, wrAfter.Bottom);
                    }
                }

                // Place a separate-process controlled neutral non-Word window COMPLETELY OUTSIDE wordRootRect
                // so CompositeStampHostRouter (which skips only _currentProcessId) resolves neutralWindowPoint
                // to a genuine foreign-PID non-Word HWND and never falls through to Word behind it.
                int neutralW = 220;
                int neutralH = 240;
                int neutralLeft = (wordRootRect.Left - neutralW - 45 >= work.Left + 8)
                    ? (wordRootRect.Left - neutralW - 45)
                    : (wordRootRect.Right + 45);
                int neutralTop = Math.Max(work.Top + 20, Math.Min(work.Bottom - neutralH - 20, (int)Math.Round(validWordPagePoint.Y) - neutralH / 2));
                var neutralRect = new Rectangle(neutralLeft, neutralTop, neutralW, neutralH);
                if (wordRootRect.IntersectsWith(neutralRect))
                {
                    throw new InvalidOperationException(
                        $"Neutral host rect {neutralRect} must not intersect Word root rect {wordRootRect}.");
                }

                string selfExe = Assembly.GetExecutingAssembly().Location;
                var startInfo = new ProcessStartInfo(
                    selfExe,
                    $"--neutral-host-window {neutralLeft} {neutralTop} {neutralW} {neutralH} {Process.GetCurrentProcess().Id}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                neutralHostProcess = Process.Start(startInfo);
                var neutralWindowPoint = new Vector2D(neutralLeft + neutralW * 0.5, neutralTop + neutralH * 0.5);

                var router = new CompositeStampHostRouter();
                IntPtr startTargetHwnd = IntPtr.Zero;
                IntPtr startRootHwnd = IntPtr.Zero;
                string startTargetClass = string.Empty;
                string startRootClass = string.Empty;
                StampTargetProbe startProbe = null;

                for (int attempt = 0; attempt < 25; attempt++)
                {
                    PumpNativeMessagesBounded(0.04);
                    startTargetHwnd = router.FindUnderlyingWindowAtPoint(neutralWindowPoint, overlay.Handle);
                    startRootHwnd = startTargetHwnd != IntPtr.Zero
                        ? NativeMethods.GetAncestor(startTargetHwnd, NativeMethods.GA_ROOT)
                        : IntPtr.Zero;
                    startTargetClass = startTargetHwnd != IntPtr.Zero ? NativeMethods.GetWindowClassName(startTargetHwnd) : string.Empty;
                    startRootClass = startRootHwnd != IntPtr.Zero ? NativeMethods.GetWindowClassName(startRootHwnd) : string.Empty;
                    startProbe = router.ProbeAtScreenPoint(neutralWindowPoint, overlay.Handle);

                    if (startTargetHwnd != IntPtr.Zero &&
                        neutralHostProcess != null &&
                        !string.Equals(startTargetClass, "_WwG", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(startRootClass, "OpusApp", StringComparison.OrdinalIgnoreCase) &&
                        startRootHwnd != wordRootHwnd &&
                        startProbe != null &&
                        !startProbe.IsValidTarget)
                    {
                        NativeMethods.GetWindowThreadProcessId(startTargetHwnd, out uint hitPid);
                        if (hitPid == (uint)neutralHostProcess.Id)
                            break;
                    }
                }

                IntPtr endTargetHwnd = router.FindUnderlyingWindowAtPoint(validWordPagePoint, overlay.Handle);
                IntPtr endRootHwnd = endTargetHwnd != IntPtr.Zero
                    ? NativeMethods.GetAncestor(endTargetHwnd, NativeMethods.GA_ROOT)
                    : IntPtr.Zero;
                string endTargetClass = endTargetHwnd != IntPtr.Zero ? NativeMethods.GetWindowClassName(endTargetHwnd) : string.Empty;
                string endRootClass = endRootHwnd != IntPtr.Zero ? NativeMethods.GetWindowClassName(endRootHwnd) : string.Empty;
                StampTargetProbe endProbe = router.ProbeAtScreenPoint(validWordPagePoint, overlay.Handle);

                if (startTargetHwnd == IntPtr.Zero ||
                    startRootHwnd == wordRootHwnd ||
                    string.Equals(startTargetClass, "_WwG", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(startRootClass, "OpusApp", StringComparison.OrdinalIgnoreCase) ||
                    startProbe == null ||
                    startProbe.IsValidTarget)
                {
                    throw new InvalidOperationException(
                        $"Boundary preflight failed at neutral start point {neutralWindowPoint}: " +
                        $"TargetHwnd=0x{startTargetHwnd.ToInt64():X} ({startTargetClass}), RootHwnd=0x{startRootHwnd.ToInt64():X} ({startRootClass}), Valid={startProbe?.IsValidTarget}.");
                }

                if (endTargetHwnd == IntPtr.Zero ||
                    endRootHwnd != wordRootHwnd ||
                    !string.Equals(endTargetClass, "_WwG", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(endRootClass, "OpusApp", StringComparison.OrdinalIgnoreCase) ||
                    endProbe == null ||
                    !endProbe.IsValidTarget)
                {
                    throw new InvalidOperationException(
                        $"Boundary preflight failed at Word end point {validWordPagePoint}: " +
                        $"TargetHwnd=0x{endTargetHwnd.ToInt64():X} ({endTargetClass}), RootHwnd=0x{endRootHwnd.ToInt64():X} ({endRootClass}), Valid={endProbe?.IsValidTarget}.");
                }

                string boundaryEvidenceSummary =
                    $"Start=0x{startTargetHwnd.ToInt64():X}/{startTargetClass}(Root=0x{startRootHwnd.ToInt64():X}/{startRootClass},Valid=False) -> " +
                    $"End=0x{endTargetHwnd.ToInt64():X}/{endTargetClass}(Root=0x{endRootHwnd.ToInt64():X}/{endRootClass},Valid=True)";
                Console.WriteLine($"  [BoundaryPreflight] {boundaryEvidenceSummary}");

                // 3B. Pure Held-Button Pickup Drag Boundary Crossing: Neutral Window <-> Live Word _WwG (2.8s, NO release throughout)
                int heldDragStepAssertions = 0;
                ScenarioMetrics m03B = RunOverlayScenario(
                    overlay,
                    "03B_HeldDrag_BoundaryEntry_NeutralToLiveWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        heldDragStepAssertions = 0;
                        ResetStateMachineToIdle(form.StateMachine, neutralWindowPoint);
                        bool began = form.StateMachine.BeginPickupDrag(neutralWindowPoint);
                        if (!began || form.StateMachine.CurrentState != StampState.DraggingPickup)
                        {
                            throw new InvalidOperationException(
                                $"03B setup failed: BeginPickupDrag returned {began}, CurrentState={form.StateMachine.CurrentState}.");
                        }
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.08);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        if (form.StateMachine.CurrentState != StampState.DraggingPickup)
                        {
                            throw new InvalidOperationException(
                                $"03B state violation: expected DraggingPickup throughout pure held-drag scenario, got {form.StateMachine.CurrentState}.");
                        }
                        heldDragStepAssertions++;
                        double wave = 0.5 * (1.0 - Math.Cos(elapsedSec * 7.5));
                        var pt = Vector2D.Lerp(neutralWindowPoint, validWordPagePoint, wave);
                        form.StateMachine.OnPointerMove(pt);
                    });
                m03B.Notes = $"Pure Held DraggingPickup ({heldDragStepAssertions} steps asserted, 0 releases) | {boundaryEvidenceSummary}";
                results.Add(m03B);

                // 4A. Measure direct synchronous ProbeAtScreenPoint cost (25 samples)
                var probeDurations = new List<double>(30);
                var swProbe = new Stopwatch();
                int validProbes = 0;
                for (int i = 0; i < 25; i++)
                {
                    var testPt = new Vector2D(
                        validWordPagePoint.X + (i % 5) * 14,
                        validWordPagePoint.Y + (i / 5) * 14);
                    swProbe.Restart();
                    StampTargetProbe p = router.ProbeAtScreenPoint(testPt, overlay.Handle);
                    swProbe.Stop();
                    probeDurations.Add(swProbe.Elapsed.TotalMilliseconds);
                    if (p != null && p.IsValidTarget) validProbes++;
                }
                Console.WriteLine(
                    $"  [04A_DirectSyncWordProbe_25Calls] valid={validProbes}/25, " +
                    $"p50={Percentile(probeDurations, 0.50):F2}ms, p95={Percentile(probeDurations, 0.95):F2}ms, " +
                    $"max={Percentile(probeDurations, 1.00):F2}ms");

                results.Add(new ScenarioMetrics
                {
                    ScenarioName = "04A_DirectSyncWordProbe_25Calls",
                    DurationSeconds = probeDurations.Sum() / 1000.0,
                    TickSampleCount = probeDurations.Count,
                    LayeredPresentCount = 0,
                    FullBitmapRedrawCount = 0,
                    PositionOnlyPresentCount = 0,
                    SkippedIdleFrameCount = 0,
                    CanvasSizePx = 0,
                    TickDurationP50Ms = Percentile(probeDurations, 0.50),
                    TickDurationP95Ms = Percentile(probeDurations, 0.95),
                    TickDurationP99Ms = Percentile(probeDurations, 0.99),
                    TickDurationMaxMs = Percentile(probeDurations, 1.00),
                    FrameIntervalP50Ms = Percentile(probeDurations, 0.50),
                    FrameIntervalP95Ms = Percentile(probeDurations, 0.95),
                    FrameIntervalP99Ms = Percentile(probeDurations, 0.99),
                    FrameIntervalMaxMs = Percentile(probeDurations, 1.00),
                    Notes = $"Direct CompositeStampHostRouter.ProbeAtScreenPoint over live Word ({validProbes}/25 valid)"
                });

                // 4B. Stationary Aiming over Live Word (2.8s)
                results.Add(RunOverlayScenario(
                    overlay,
                    "04B_AimingStationary_LiveWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        ResetStateMachineToIdle(form.StateMachine, validWordPagePoint);
                        if (!form.StateMachine.BeginPickupDrag(validWordPagePoint) ||
                            !form.StateMachine.CompletePickupToAiming(validWordPagePoint) ||
                            form.StateMachine.CurrentState != StampState.Aiming)
                        {
                            throw new InvalidOperationException("04B setup failed to enter Aiming.");
                        }
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.20);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        form.StateMachine.OnPointerMove(validWordPagePoint);
                    }));

                // 4C. Released Aiming Boundary Entry: Neutral Window <-> Live Word _WwG Page (2.8s)
                int aimingBoundaryAssertions = 0;
                ScenarioMetrics m04C = RunOverlayScenario(
                    overlay,
                    "04C_Aiming_BoundaryEntry_NeutralToLiveWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        aimingBoundaryAssertions = 0;
                        ResetStateMachineToIdle(form.StateMachine, neutralWindowPoint);
                        bool began = form.StateMachine.BeginPickupDrag(neutralWindowPoint);
                        bool completed = form.StateMachine.CompletePickupToAiming(neutralWindowPoint);
                        if (!began || !completed || form.StateMachine.CurrentState != StampState.Aiming)
                        {
                            throw new InvalidOperationException(
                                $"04C setup failed: BeginPickupDrag={began}, CompletePickupToAiming={completed}, CurrentState={form.StateMachine.CurrentState}.");
                        }
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.15);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        if (form.StateMachine.CurrentState != StampState.Aiming)
                        {
                            throw new InvalidOperationException(
                                $"04C state violation: expected Aiming throughout released boundary scenario, got {form.StateMachine.CurrentState}.");
                        }
                        aimingBoundaryAssertions++;
                        double wave = 0.5 * (1.0 - Math.Cos(elapsedSec * 7.5));
                        var pt = Vector2D.Lerp(neutralWindowPoint, validWordPagePoint, wave);
                        form.StateMachine.OnPointerMove(pt);
                    });
                m04C.Notes = $"Released Aiming ({aimingBoundaryAssertions} steps asserted) | {boundaryEvidenceSummary}";
                results.Add(m04C);

                // 5. Continuous Moving Aim over Live Word (2.8s)
                results.Add(RunOverlayScenario(
                    overlay,
                    "05_MovingAim_LiveWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        ResetStateMachineToIdle(form.StateMachine, validWordPagePoint);
                        if (!form.StateMachine.BeginPickupDrag(validWordPagePoint) ||
                            !form.StateMachine.CompletePickupToAiming(validWordPagePoint))
                        {
                            throw new InvalidOperationException("05 setup failed to enter Aiming.");
                        }
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.20);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        double angle = elapsedSec * 6.5;
                        var pt = new Vector2D(
                            validWordPagePoint.X + Math.Cos(angle) * 65.0,
                            validWordPagePoint.Y + Math.Sin(angle * 2.0) * 55.0);
                        form.StateMachine.OnPointerMove(pt);
                    }));

                // 6. Moving Aim while Word is actively scrolling & zooming (2.8s)
                int lastScrollStep = -1;
                results.Add(RunOverlayScenario(
                    overlay,
                    "06_MovingAim_BusyScrollingZoomingWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        lastScrollStep = -1;
                        ResetStateMachineToIdle(form.StateMachine, validWordPagePoint);
                        if (!form.StateMachine.BeginPickupDrag(validWordPagePoint) ||
                            !form.StateMachine.CompletePickupToAiming(validWordPagePoint))
                        {
                            throw new InvalidOperationException("06 setup failed to enter Aiming.");
                        }
                        form.SyncPassThroughWindowStyle();
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        double angle = elapsedSec * 7.0;
                        var pt = new Vector2D(
                            validWordPagePoint.X + Math.Cos(angle) * 60.0,
                            validWordPagePoint.Y + Math.Sin(angle * 1.5) * 50.0);
                        form.StateMachine.OnPointerMove(pt);

                        int step = (int)(elapsedSec / 0.35);
                        if (step != lastScrollStep)
                        {
                            lastScrollStep = step;
                            try
                            {
                                int[] zooms = { 100, 120, 140, 110, 100 };
                                win.View.Zoom.Percentage = zooms[step % zooms.Length];
                                win.SmallScroll(Down: (step % 2 == 0) ? 2 : 0, Up: (step % 2 != 0) ? 2 : 0);
                            }
                            catch
                            {
                            }
                        }
                    }));

                try
                {
                    win.View.Zoom.Percentage = 100;
                    win.ScrollIntoView(page1Range, true);
                    PumpNativeMessagesBounded(0.15);
                }
                catch
                {
                }

                // 7. Repeated Stamping Impact & Rebound + Return Arc over Live Word (2.8s)
                int triggerStep = -1;
                results.Add(RunOverlayScenario(
                    overlay,
                    "07_ImpactAndReturn_LiveWord",
                    durationSec: 2.8,
                    logicalCores: logicalCores,
                    setup: (form, cursor) =>
                    {
                        triggerStep = -1;
                        ResetStateMachineToIdle(form.StateMachine, validWordPagePoint);
                        if (!form.StateMachine.BeginPickupDrag(validWordPagePoint) ||
                            !form.StateMachine.CompletePickupToAiming(validWordPagePoint))
                        {
                            throw new InvalidOperationException("07 setup failed to enter Aiming.");
                        }
                        form.SyncPassThroughWindowStyle();
                        PumpNativeMessagesBounded(0.20);
                    },
                    perStep: (form, cursor, elapsedSec) =>
                    {
                        int phase = (int)(elapsedSec / 0.70);
                        if (phase != triggerStep)
                        {
                            triggerStep = phase;
                            var targetPt = new Vector2D(validWordPagePoint.X + (phase % 3) * 24, validWordPagePoint.Y + (phase % 2) * 24);
                            if (phase == 0 || phase == 1)
                            {
                                form.StateMachine.TryTriggerStamp(targetPt);
                                form.SyncPassThroughWindowStyle();
                            }
                            else if (phase == 2)
                            {
                                form.StateMachine.BeginReturnToParked();
                                form.SyncPassThroughWindowStyle();
                            }
                            else if (phase == 3)
                            {
                                form.StateMachine.BeginPickupDrag(targetPt);
                                form.StateMachine.CompletePickupToAiming(targetPt);
                                form.SyncPassThroughWindowStyle();
                            }
                        }
                    }));
            }
            finally
            {
                if (neutralHostProcess != null)
                {
                    try
                    {
                        if (!neutralHostProcess.HasExited)
                            neutralHostProcess.Kill();
                    }
                    catch { }
                    try { neutralHostProcess.Dispose(); } catch { }
                }

                // Clear any thread-local Word RCW cache on the current thread before closing Word
                try
                {
                    MethodInfo clearCacheMethod = typeof(WordStampHostAdapter).GetMethod(
                        "ClearThreadLocalCache",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    clearCacheMethod?.Invoke(null, null);
                }
                catch
                {
                }

                if (doc != null)
                {
                    try { doc.Close(0); } catch { }
                    try { Marshal.FinalReleaseComObject(doc); } catch { }
                }
                if (wordApp != null)
                {
                    try { wordApp.Quit(0); } catch { }
                    try { Marshal.FinalReleaseComObject(wordApp); } catch { }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static long ReadOptionalLongProperty(object obj, string propName)
        {
            if (obj == null)
                return -1L;
            PropertyInfo p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null)
                return -1L;
            try
            {
                return Convert.ToInt64(p.GetValue(obj));
            }
            catch
            {
                return -1L;
            }
        }

        private static int ReadCanvasSizePx(StampOverlayForm overlay)
        {
            if (overlay == null)
                return 0;
            PropertyInfo p = overlay.GetType().GetProperty("CurrentCanvasSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                try { return Convert.ToInt32(p.GetValue(overlay)); } catch { }
            }
            FieldInfo f = overlay.GetType().GetField("_currentCanvasSize", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                try { return Convert.ToInt32(f.GetValue(overlay)); } catch { }
            }
            return overlay.Width;
        }

        private static ScenarioMetrics RunOverlayScenario(
            StampOverlayForm overlay,
            string scenarioName,
            double durationSec,
            int logicalCores,
            Action<StampOverlayForm, Vector2D> setup,
            Action<StampOverlayForm, Vector2D, double> perStep)
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT curPt);
            var cursorVec = new Vector2D(curPt.X, curPt.Y);

            setup?.Invoke(overlay, cursorVec);

            FieldInfo timerField = typeof(StampOverlayForm).GetField("_frameTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo tickMethod = typeof(StampOverlayForm).GetMethod("OnFrameTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);

            System.Windows.Forms.Timer frameTimer = (System.Windows.Forms.Timer)timerField.GetValue(overlay);
            EventHandler originalHandler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), overlay, tickMethod);

            var tickDurationsMs = new List<double>(512);
            var frameIntervalsMs = new List<double>(512);
            var pointerQueueDelaysMs = new List<double>(1024);
            object delayLock = new object();

            var swScenario = new Stopwatch();
            var swSingleTick = new Stopwatch();
            double lastTickTimestampMs = -1.0;

            EventHandler instrumentedHandler = (s, e) =>
            {
                if (!swScenario.IsRunning || swScenario.Elapsed.TotalSeconds >= durationSec)
                    return;

                double nowMs = swScenario.Elapsed.TotalMilliseconds;
                if (lastTickTimestampMs >= 0.0)
                {
                    frameIntervalsMs.Add(nowMs - lastTickTimestampMs);
                }
                lastTickTimestampMs = nowMs;

                swSingleTick.Restart();
                originalHandler(s, e);
                swSingleTick.Stop();
                tickDurationsMs.Add(swSingleTick.Elapsed.TotalMilliseconds);
            };

            frameTimer.Tick -= originalHandler;
            frameTimer.Tick += instrumentedHandler;

            int running = 1;
            int probeInFlight = 0;

            // Dedicated 250Hz background input-latency sampler thread:
            // Posts lightweight callbacks onto the UI message loop every 4ms and measures how long the UI thread
            // takes to service them (directly capturing stalls caused by synchronous COM or heavy GDI+ frames).
            var inputLatencyThread = new Thread(() =>
            {
                while (Volatile.Read(ref running) == 1)
                {
                    if (swScenario.IsRunning && swScenario.Elapsed.TotalSeconds < durationSec)
                    {
                        if (Interlocked.CompareExchange(ref probeInFlight, 1, 0) == 0)
                        {
                            double postedAtMs = swScenario.Elapsed.TotalMilliseconds;
                            try
                            {
                                overlay.BeginInvoke(new Action(() =>
                                {
                                    double servicedAtMs = swScenario.Elapsed.TotalMilliseconds;
                                    lock (delayLock)
                                    {
                                        pointerQueueDelaysMs.Add(Math.Max(0.0, servicedAtMs - postedAtMs));
                                    }
                                    Interlocked.Exchange(ref probeInFlight, 0);
                                }));
                            }
                            catch
                            {
                                Interlocked.Exchange(ref probeInFlight, 0);
                            }
                        }
                    }
                    Thread.Sleep(4);
                }
            })
            {
                IsBackground = true,
                Name = "BenchmarkInputLatencySampler"
            };

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Process proc = Process.GetCurrentProcess();
                IntPtr hProc = proc.Handle;
                uint gdiStart = GetGuiResources(hProc, GR_GDIOBJECTS);
                uint userStart = GetGuiResources(hProc, GR_USEROBJECTS);
                uint gdiPeak = gdiStart;
                uint userPeak = userStart;

                int gen0Start = GC.CollectionCount(0);
                int gen1Start = GC.CollectionCount(1);
                int gen2Start = GC.CollectionCount(2);
                long memStart = GC.GetTotalMemory(false);

                long presentStart = ReadOptionalLongProperty(overlay, "LayeredFramesPresentedCount");
                long fullRedrawStart = ReadOptionalLongProperty(overlay, "FullRenderFrameCount");
                long posOnlyStart = ReadOptionalLongProperty(overlay, "PositionOnlyPresentCount");
                long skippedStart = ReadOptionalLongProperty(overlay, "SkippedIdleFrameCount");

                TimeSpan cpuStart = proc.TotalProcessorTime;

                swScenario.Start();
                inputLatencyThread.Start();

                while (swScenario.Elapsed.TotalSeconds < durationSec)
                {
                    double elapsedSec = swScenario.Elapsed.TotalSeconds;
                    perStep?.Invoke(overlay, cursorVec, elapsedSec);

                    // Pump up to 32 native messages per outer iteration while checking durationSec after EVERY message
                    int pumped = 0;
                    while (swScenario.Elapsed.TotalSeconds < durationSec &&
                           pumped < 32 &&
                           PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                        pumped++;
                    }

                    uint gdiNow = GetGuiResources(hProc, GR_GDIOBJECTS);
                    uint userNow = GetGuiResources(hProc, GR_USEROBJECTS);
                    if (gdiNow > gdiPeak) gdiPeak = gdiNow;
                    if (userNow > userPeak) userPeak = userNow;

                    if (pumped == 0)
                    {
                        Thread.Sleep(1);
                    }
                }

                Interlocked.Exchange(ref running, 0);
                swScenario.Stop();
                inputLatencyThread.Join(200);

                proc.Refresh();
                TimeSpan cpuDelta = proc.TotalProcessorTime - cpuStart;
                long memEnd = GC.GetTotalMemory(false);
                uint gdiEnd = GetGuiResources(hProc, GR_GDIOBJECTS);
                uint userEnd = GetGuiResources(hProc, GR_USEROBJECTS);

                long presentEnd = ReadOptionalLongProperty(overlay, "LayeredFramesPresentedCount");
                long fullRedrawEnd = ReadOptionalLongProperty(overlay, "FullRenderFrameCount");
                long posOnlyEnd = ReadOptionalLongProperty(overlay, "PositionOnlyPresentCount");
                long skippedEnd = ReadOptionalLongProperty(overlay, "SkippedIdleFrameCount");

                long layeredPresents = (presentStart >= 0 && presentEnd >= presentStart)
                    ? (presentEnd - presentStart)
                    : tickDurationsMs.Count;
                long fullRedraws = (fullRedrawStart >= 0 && fullRedrawEnd >= fullRedrawStart)
                    ? (fullRedrawEnd - fullRedrawStart)
                    : layeredPresents; // in v1 every present is a full bitmap redraw
                long posOnlyPresents = (posOnlyStart >= 0 && posOnlyEnd >= posOnlyStart)
                    ? (posOnlyEnd - posOnlyStart)
                    : 0L;
                long skippedIdleFrames = (skippedStart >= 0 && skippedEnd >= skippedStart)
                    ? (skippedEnd - skippedStart)
                    : 0L;

                double wallSec = swScenario.Elapsed.TotalSeconds;
                double cpuCorePct = (cpuDelta.TotalSeconds / wallSec) * 100.0;

                List<double> delaysSnapshot;
                lock (delayLock)
                {
                    delaysSnapshot = new List<double>(pointerQueueDelaysMs);
                }

                var m = new ScenarioMetrics
                {
                    ScenarioName = scenarioName,
                    DurationSeconds = wallSec,
                    TickSampleCount = tickDurationsMs.Count,
                    LayeredPresentCount = layeredPresents,
                    FullBitmapRedrawCount = fullRedraws,
                    PositionOnlyPresentCount = posOnlyPresents,
                    SkippedIdleFrameCount = skippedIdleFrames,
                    CanvasSizePx = ReadCanvasSizePx(overlay),
                    CpuTimeMs = cpuDelta.TotalMilliseconds,
                    CpuPercentOneCore = cpuCorePct,
                    CpuPercentWholeMachine = cpuCorePct / logicalCores,
                    WorkingSetBytes = proc.WorkingSet64,
                    PrivateMemoryBytes = proc.PrivateMemorySize64,
                    ManagedMemoryDeltaBytes = Math.Max(0, memEnd - memStart),
                    Gen0Collections = GC.CollectionCount(0) - gen0Start,
                    Gen1Collections = GC.CollectionCount(1) - gen1Start,
                    Gen2Collections = GC.CollectionCount(2) - gen2Start,
                    GdiHandlesStart = gdiStart,
                    GdiHandlesEnd = gdiEnd,
                    GdiHandlesPeak = Math.Max(gdiPeak, gdiEnd),
                    UserHandlesStart = userStart,
                    UserHandlesEnd = userEnd,
                    UserHandlesPeak = Math.Max(userPeak, userEnd),
                    TickDurationP50Ms = Percentile(tickDurationsMs, 0.50),
                    TickDurationP95Ms = Percentile(tickDurationsMs, 0.95),
                    TickDurationP99Ms = Percentile(tickDurationsMs, 0.99),
                    TickDurationMaxMs = Percentile(tickDurationsMs, 1.00),
                    FrameIntervalP50Ms = Percentile(frameIntervalsMs, 0.50),
                    FrameIntervalP95Ms = Percentile(frameIntervalsMs, 0.95),
                    FrameIntervalP99Ms = Percentile(frameIntervalsMs, 0.99),
                    FrameIntervalMaxMs = Percentile(frameIntervalsMs, 1.00),
                    PointerQueueSampleCount = delaysSnapshot.Count,
                    PointerQueueDelayP50Ms = Percentile(delaysSnapshot, 0.50),
                    PointerQueueDelayP95Ms = Percentile(delaysSnapshot, 0.95),
                    PointerQueueDelayP99Ms = Percentile(delaysSnapshot, 0.99),
                    PointerQueueDelayMaxMs = Percentile(delaysSnapshot, 1.00),
                    Notes = "Live native StampOverlayForm message loop"
                };

                Console.WriteLine(
                    $"  [{scenarioName}] ticks={m.TickSampleCount}, presents={m.LayeredPresentCount} (redraw={m.FullBitmapRedrawCount}, posOnly={m.PositionOnlyPresentCount}, skip={m.SkippedIdleFrameCount}), " +
                    $"CPU(1core)={m.CpuPercentOneCore:F1}%, tickP95={m.TickDurationP95Ms:F2}ms, tickMax={m.TickDurationMaxMs:F2}ms, " +
                    $"intervalP95={m.FrameIntervalP95Ms:F2}ms, intervalMax={m.FrameIntervalMaxMs:F2}ms, " +
                    $"uiQueueP95={m.PointerQueueDelayP95Ms:F2}ms, uiQueueMax={m.PointerQueueDelayMaxMs:F2}ms");

                return m;
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
                frameTimer.Tick -= instrumentedHandler;
                frameTimer.Tick += originalHandler;
            }
        }

        private static void PumpNativeMessagesBounded(double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                int count = 0;
                while (sw.Elapsed.TotalSeconds < seconds &&
                       count < 24 &&
                       PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                    count++;
                }
                Thread.Sleep(2);
            }
        }

        private static double Percentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0)
                return 0.0;

            var sorted = new List<double>(values);
            sorted.Sort();
            if (percentile >= 1.0)
                return sorted[sorted.Count - 1];
            if (percentile <= 0.0)
                return sorted[0];

            double index = percentile * (sorted.Count - 1);
            int low = (int)Math.Floor(index);
            int high = (int)Math.Ceiling(index);
            if (low == high)
                return sorted[low];
            double frac = index - low;
            return sorted[low] + (sorted[high] - sorted[low]) * frac;
        }

        private static void PrintSummaryTable(List<ScenarioMetrics> metrics)
        {
            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------------------------------------------------------------------------------------");
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-44} | {1,5} | {2,5} | {3,5} | {4,5} | {5,8} | {6,8} | {7,8} | {8,8} | {9,8}",
                "Scenario", "Ticks", "Redrw", "PosOn", "Pres", "CPU(1C%)", "TickP50", "TickP95", "TickMax", "UIQMax"));
            Console.WriteLine("------------------------------------------------------------------------------------------------------------------------------------------");
            foreach (ScenarioMetrics m in metrics)
            {
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-44} | {1,5} | {2,5} | {3,5} | {4,5} | {5,7:F1}% | {6,6:F2}ms | {7,6:F2}ms | {8,6:F2}ms | {9,6:F2}ms",
                    m.ScenarioName,
                    m.TickSampleCount,
                    m.FullBitmapRedrawCount,
                    m.PositionOnlyPresentCount,
                    m.LayeredPresentCount,
                    m.CpuPercentOneCore,
                    m.TickDurationP50Ms,
                    m.TickDurationP95Ms,
                    m.TickDurationMaxMs,
                    m.PointerQueueDelayMaxMs));
            }
            Console.WriteLine("------------------------------------------------------------------------------------------------------------------------------------------");
        }

        private static void SaveResultsJson(
            string path,
            string label,
            string assemblyPath,
            int logicalCores,
            List<ScenarioMetrics> metrics)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"label\": \"{EscapeJson(label)}\",");
            sb.AppendLine($"  \"capturedAt\": \"{DateTimeOffset.Now:O}\",");
            sb.AppendLine($"  \"assemblyPath\": \"{EscapeJson(assemblyPath)}\",");
            sb.AppendLine($"  \"logicalCores\": {logicalCores},");
            sb.AppendLine("  \"scenarios\": [");

            for (int i = 0; i < metrics.Count; i++)
            {
                ScenarioMetrics m = metrics[i];
                sb.AppendLine("    {");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"scenarioName\": \"{0}\",", EscapeJson(m.ScenarioName)));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"durationSeconds\": {0:F4},", m.DurationSeconds));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"tickSampleCount\": {0},", m.TickSampleCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"layeredPresentCount\": {0},", m.LayeredPresentCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"fullBitmapRedrawCount\": {0},", m.FullBitmapRedrawCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"positionOnlyPresentCount\": {0},", m.PositionOnlyPresentCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"skippedIdleFrameCount\": {0},", m.SkippedIdleFrameCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"canvasSizePx\": {0},", m.CanvasSizePx));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"effectiveFps\": {0:F2},", m.EffectiveFps));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"effectivePresentFps\": {0:F2},", m.EffectivePresentFps));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"effectiveRedrawFps\": {0:F2},", m.EffectiveRedrawFps));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"cpuTimeMs\": {0:F2},", m.CpuTimeMs));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"cpuPercentOneCore\": {0:F2},", m.CpuPercentOneCore));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"cpuPercentWholeMachine\": {0:F2},", m.CpuPercentWholeMachine));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"workingSetBytes\": {0},", m.WorkingSetBytes));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"privateMemoryBytes\": {0},", m.PrivateMemoryBytes));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"managedMemoryDeltaBytes\": {0},", m.ManagedMemoryDeltaBytes));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gen0Collections\": {0},", m.Gen0Collections));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gen1Collections\": {0},", m.Gen1Collections));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gen2Collections\": {0},", m.Gen2Collections));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gdiHandlesStart\": {0},", m.GdiHandlesStart));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gdiHandlesEnd\": {0},", m.GdiHandlesEnd));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"gdiHandlesPeak\": {0},", m.GdiHandlesPeak));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"userHandlesStart\": {0},", m.UserHandlesStart));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"userHandlesEnd\": {0},", m.UserHandlesEnd));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"userHandlesPeak\": {0},", m.UserHandlesPeak));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"tickDurationP50Ms\": {0:F3},", m.TickDurationP50Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"tickDurationP95Ms\": {0:F3},", m.TickDurationP95Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"tickDurationP99Ms\": {0:F3},", m.TickDurationP99Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"tickDurationMaxMs\": {0:F3},", m.TickDurationMaxMs));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"frameIntervalP50Ms\": {0:F3},", m.FrameIntervalP50Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"frameIntervalP95Ms\": {0:F3},", m.FrameIntervalP95Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"frameIntervalP99Ms\": {0:F3},", m.FrameIntervalP99Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"frameIntervalMaxMs\": {0:F3},", m.FrameIntervalMaxMs));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"pointerQueueSampleCount\": {0},", m.PointerQueueSampleCount));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"pointerQueueDelayP50Ms\": {0:F3},", m.PointerQueueDelayP50Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"pointerQueueDelayP95Ms\": {0:F3},", m.PointerQueueDelayP95Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"pointerQueueDelayP99Ms\": {0:F3},", m.PointerQueueDelayP99Ms));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"pointerQueueDelayMaxMs\": {0:F3},", m.PointerQueueDelayMaxMs));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      \"notes\": \"{0}\"", EscapeJson(m.Notes ?? "")));
                sb.AppendLine(i == metrics.Count - 1 ? "    }" : "    },");
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}
