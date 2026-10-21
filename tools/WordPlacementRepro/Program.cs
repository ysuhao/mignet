using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DesktopStamp.Core;
using DesktopStamp.HostAdapters;
using DesktopStamp.Imaging;

namespace DesktopStamp.WordPlacementRepro
{
    /// <summary>
    /// Live placement accuracy check against a disposable Word document.
    /// For every monitor and target page point, draws a green marker shape at a known page position,
    /// finds its real on-screen pixel by screen capture (ground truth), removes it, then asks the adapter
    /// to probe and stamp at that pixel and reports the page / point error.
    /// Never touches any existing user document: it starts its own Word instance and closes without saving.
    /// </summary>
    internal static class Program
    {
        private sealed class Target
        {
            public int Page;
            public double LeftPt;
            public double TopPt;
            public string Label;
        }

        private static readonly Target[] Targets =
        {
            new Target { Page = 1, LeftPt = 300, TopPt = 130, Label = "p1 near text" },
            new Target { Page = 1, LeftPt = 300, TopPt = 520, Label = "p1 blank middle" },
            new Target { Page = 1, LeftPt = 430, TopPt = 740, Label = "p1 blank bottom-right" },
            new Target { Page = 2, LeftPt = 200, TopPt = 160, Label = "p2 near text" },
            new Target { Page = 2, LeftPt = 330, TopPt = 600, Label = "p2 blank lower" },
        };

        private const int MarkerRgb = 0x00FF00;
        private const double MarkerSizePt = 6.0;

        private static int _failures;
        private static int _skipped;

        [STAThread]
        private static int Main(string[] args)
        {
            bool stamp = Array.IndexOf(args, "--no-stamp") < 0;
            double[] zooms = { 100.0, 140.0 };

            Console.OutputEncoding = Encoding.UTF8;
            DumpMonitors();

            // Quit(0) at the end must never be able to close a user's own Word session
            if (Process.GetProcessesByName("WINWORD").Length > 0)
            {
                Console.WriteLine("Word is already running. Close it first; this tool only runs against its own Word instance.");
                return 3;
            }

            Type wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                Console.WriteLine("Word ProgID not registered.");
                return 2;
            }

            dynamic app = null;
            dynamic doc = null;
            try
            {
                app = Activator.CreateInstance(wordType);
                app.Visible = true;
                app.DisplayAlerts = 0;
                doc = app.Documents.Add();
                BuildDocument(app, doc);

                dynamic win = doc.ActiveWindow;
                win.View.Type = 3;
                IntPtr root = new IntPtr(Convert.ToInt32(win.Hwnd));
                IntPtr wwg = FindWwG(root);
                Console.WriteLine($"Word root=0x{root.ToInt64():X} _WwG=0x{wwg.ToInt64():X}");
                DumpWindowDpi("Word root", root);

                var adapter = new WordStampHostAdapter();
                List<SealAsset> seals = SealImageProcessor.CreateBuiltInSampleSeals();
                SealAsset seal = seals[0];

                Screen[] screens = Screen.AllScreens;
                for (int s = 0; s < screens.Length; s++)
                {
                    MoveToScreen(root, screens[s]);
                    wwg = FindWwG(root);
                    DumpWindowDpi($"After move to screen {s} _WwG", wwg);

                    foreach (double zoom in zooms)
                    {
                        win.View.Zoom.Percentage = (int)zoom;
                        Pump(400);

                        // 100%: stamp through the page cache primed by the probe; other zooms: uncached full mapping
                        bool usePageCache = zoom == 100.0;
                        Console.WriteLine($"--- screen {s} ({screens[s].DeviceName}) zoom {zoom}% {(usePageCache ? "cached" : "uncached")} mapping ---");
                        foreach (Target t in Targets)
                        {
                            RunTarget(app, doc, win, root, wwg, adapter, seal, t, stamp, usePageCache);
                        }

                        if (stamp && usePageCache)
                        {
                            RunEndToEnd(doc, win, root, wwg, seal, Targets[2]);
                            RunEndToEnd(doc, win, root, wwg, seal, Targets[3]);
                        }
                    }
                }

                foreach (SealAsset sa in seals)
                    sa.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                _failures++;
            }
            finally
            {
                WordStampHostAdapter.ClearThreadLocalCache();
                if (doc != null)
                {
                    try { doc.Close(0); } catch { }
                    try { Marshal.FinalReleaseComObject(doc); } catch { }
                }
                if (app != null)
                {
                    try { app.Quit(0); } catch { }
                    try { Marshal.FinalReleaseComObject(app); } catch { }
                }
            }

            // Skipped targets were never measured, so they must not count as a pass
            Console.WriteLine(_failures == 0 && _skipped == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({_failures} failed, {_skipped} skipped)");
            return _failures == 0 && _skipped == 0 ? 0 : 1;
        }

        private static void BuildDocument(dynamic app, dynamic doc)
        {
            dynamic sel = app.Selection;
            sel.TypeText("WordPlacementRepro disposable document - page 1\r");
            sel.TypeText("Short text at the top of page 1. The rest of the page is intentionally blank, like a contract signature page.\r");
            sel.InsertBreak(7);
            sel.TypeText("Page 2 heading\r");
            for (int i = 0; i < 6; i++)
                sel.TypeText("Page 2 body paragraph " + i + ". Lorem ipsum dolor sit amet, consectetur adipiscing elit.\r");
            sel.InsertBreak(7);
            sel.TypeText("Page 3 heading\r");
        }

        private static void RunTarget(dynamic app, dynamic doc, dynamic win, IntPtr root, IntPtr wwg,
            WordStampHostAdapter adapter, SealAsset seal, Target t, bool stamp, bool usePageCache)
        {
            Point? truth = FindTruth(doc, win, wwg, t);
            if (!truth.HasValue)
                return;

            var screenPt = new Vector2D(truth.Value.X, truth.Value.Y);
            double expLeft = t.LeftPt + MarkerSizePt / 2;
            double expTop = t.TopPt + MarkerSizePt / 2;

            var sw = Stopwatch.StartNew();
            StampTargetProbe probe = adapter.ProbeTarget(screenPt, wwg, false);
            double probeMs = sw.Elapsed.TotalMilliseconds;

            string probeText;
            if (!probe.IsValidTarget)
            {
                probeText = $"probe INVALID ({probe.StatusSummary}) hit: {DescribeHit(win, truth.Value)}";
                _failures++;
            }
            else
            {
                double errMm = DistMm(probe.PageLeftPt - expLeft, probe.PageTopPt - expTop);
                bool ok = probe.PageNumber == t.Page && errMm <= 1.0;
                if (!ok) _failures++;
                probeText = $"probe p{probe.PageNumber} ({probe.PageLeftPt:F1},{probe.PageTopPt:F1}) err {errMm:F2}mm {(ok ? "OK" : "BAD")} {probeMs:F0}ms";
            }

            string stampText = string.Empty;
            if (stamp)
            {
                // The full probe above primed the page cache; dropping it exercises the uncached mapping path
                if (!usePageCache)
                    WordStampHostAdapter.ClearThreadLocalCache();

                using (Bitmap imp = SealImageProcessor.CreateImpressionBitmap(seal, 7))
                {
                    var req = new StampRequest
                    {
                        ScreenPointPx = screenPt,
                        ExpectedTargetHwnd = wwg,
                        ExpectedRootHwnd = root,
                        Seal = seal,
                        ImpressionBitmap = imp,
                        PhysicalSizeMm = 40.0,
                        BaseAngleDeg = 0,
                        JitterAngleDeg = 0,
                        ImpressionSeed = 7
                    };
                    sw.Restart();
                    StampPlacementResult res = adapter.TryApplyStamp(req, wwg);
                    double stampMs = sw.Elapsed.TotalMilliseconds;
                    if (!res.Success)
                    {
                        _failures++;
                        stampText = $" | stamp FAILED ({res.Message}) {stampMs:F0}ms [{res.Diagnostics}]";
                    }
                    else
                    {
                        stampText = " | " + VerifyLastShape(doc, t, expLeft, expTop) + $" {stampMs:F0}ms [{res.Diagnostics}]";
                    }
                }
            }

            Console.WriteLine($"  {t.Label,-24} truth px ({truth.Value.X},{truth.Value.Y}) {probeText}{stampText}");
        }

        /// <summary>
        /// Real app flow through AsyncStampHostWorker: cursor rests (full probe primes the page cache), LEFT DOWN queues
        /// the snapshot, paper impact commits ~63ms later. Reports click-to-inserted time against the 210ms press animation.
        /// </summary>
        private static void RunEndToEnd(dynamic doc, dynamic win, IntPtr root, IntPtr wwg, SealAsset seal, Target t)
        {
            Point? truth = FindTruth(doc, win, wwg, t);
            if (!truth.HasValue)
                return;

            var screenPt = new Vector2D(truth.Value.X, truth.Value.Y);
            using (var worker = new AsyncStampHostWorker(new CompositeStampHostRouter()))
            {
                // Light probes while "moving", then the resting full probe
                for (int i = 0; i < 3; i++)
                {
                    worker.RequestProbe(new Vector2D(screenPt.X - 30 + i * 10, screenPt.Y), IntPtr.Zero, lightweight: true);
                    Pump(75);
                }
                long before = worker.GetLatestProbeSnapshot()?.CompletedTimestamp ?? 0;
                var probeWatch = Stopwatch.StartNew();
                worker.RequestProbe(screenPt, IntPtr.Zero, lightweight: false);
                while ((worker.GetLatestProbeSnapshot()?.CompletedTimestamp ?? 0) == before && probeWatch.ElapsedMilliseconds < 3000)
                    Thread.Sleep(5);
                double restingProbeMs = probeWatch.Elapsed.TotalMilliseconds;

                var click = Stopwatch.StartNew();
                AsyncStampWorkItem item = worker.CreateClickSnapshot(screenPt, IntPtr.Zero, wwg, root, seal, 40.0, 0.0, 1.5, 11);
                worker.EnqueueAwaitingImpact(item);
                Thread.Sleep(63); // press animation reaches paper (0.30 * 210ms)
                worker.CommitImpact(item);

                AsyncStampCompletion completion = null;
                while (!worker.TryConsumeStampCompletion(out completion) && click.ElapsedMilliseconds < 5000)
                    Thread.Sleep(2);
                double clickToDoneMs = click.Elapsed.TotalMilliseconds;

                // Visible when ScreenUpdating is restored (end of "format"), slightly before the worker reports completion
                bool ok = completion != null && completion.Result != null && completion.Result.Success && clickToDoneMs <= 300;
                if (!ok) _failures++;
                Console.WriteLine($"  e2e {t.Label,-20} resting probe {restingProbeMs:F0}ms, click->inserted {clickToDoneMs:F0}ms (animation 210ms) " +
                                  $"{(ok ? "OK" : "BAD")} {completion?.Result?.Message} [{completion?.Result?.Diagnostics}]");
            }

            if (Convert.ToInt32(doc.Shapes.Count) > 0)
                doc.Undo(1);
        }

        /// <summary>Draws a marker at the target, finds its real screen pixel, removes it. Null if not measurable.</summary>
        private static Point? FindTruth(dynamic doc, dynamic win, IntPtr wwg, Target t)
        {
            dynamic marker = AddMarker(doc, t);
            EnsureVisible(win, wwg, marker);

            // Word scrolls smoothly; wait until two consecutive captures put the green pixels in the same place
            byte[] withMarker = CaptureGreenMask(wwg, out RECT capRect);
            for (int attempt = 0; attempt < 15; attempt++)
            {
                Pump(150);
                byte[] again = CaptureGreenMask(wwg, out RECT againRect);
                bool stable = withMarker != null && again != null && againRect.Equals(capRect) &&
                              Equals(GreenCentroid(withMarker, capRect), GreenCentroid(again, againRect));
                withMarker = again;
                capRect = againRect;
                if (stable)
                    break;
            }
            try { marker.Delete(); } catch { }
            Marshal.ReleaseComObject(marker);
            Pump(250);

            // Ground truth = pixels that are green WITH the marker and not green after deleting it,
            // so green pixels from any other window can never be mistaken for the marker
            byte[] withoutMarker = CaptureGreenMask(wwg, out RECT capRect2);
            Point? truth = null;
            long markerPixels = 0;
            if (withMarker != null && withoutMarker != null && capRect.Equals(capRect2))
                truth = DiffCentroid(withMarker, withoutMarker, capRect, out markerPixels);

            // The changed green blob must be about the marker's rendered size, otherwise something else changed
            double sidePx = MarkerSizePt * GetDpiForWindow(wwg) / 72.0 * Convert.ToDouble(win.View.Zoom.Percentage) / 100.0;
            if (truth.HasValue && (markerPixels < 0.5 * sidePx * sidePx || markerPixels > 2.0 * sidePx * sidePx))
            {
                Console.WriteLine($"  {t.Label,-24} changed green area {markerPixels}px is not the marker (~{sidePx * sidePx:F0}px) (skipped)");
                _skipped++;
                return null;
            }

            if (!truth.HasValue)
            {
                Console.WriteLine($"  {t.Label,-24} marker not visible on screen (skipped)");
                _skipped++;
                return null;
            }
            if (WindowFromPoint(new POINT { X = truth.Value.X, Y = truth.Value.Y }) != wwg)
            {
                Console.WriteLine($"  {t.Label,-24} marker pixel is covered by another window (skipped)");
                _skipped++;
                return null;
            }
            return truth;
        }

        /// <summary>Scrolls the document until the marker shape is inside the middle band of the _WwG viewport.</summary>
        private static void EnsureVisible(dynamic win, IntPtr wwg, dynamic marker)
        {
            try { win.ScrollIntoView(marker.Anchor, true); } catch { }
            Pump(200);
            GetWindowRect(wwg, out RECT r);
            for (int i = 0; i < 60; i++)
            {
                int l, t, w, h;
                try
                {
                    win.GetPoint(out l, out t, out w, out h, marker);
                }
                catch
                {
                    break;
                }
                int mid = t + h / 2;
                if (mid > r.Top + (r.Bottom - r.Top) / 4 && mid < r.Bottom - (r.Bottom - r.Top) / 4)
                    break;
                if (mid >= r.Bottom - (r.Bottom - r.Top) / 4)
                    win.SmallScroll(3);
                else
                    win.SmallScroll(0, 3);
                Pump(60);
            }
            Pump(300);
        }

        private static string DescribePages(dynamic win, int pageNumber)
        {
            var sb = new StringBuilder();
            try
            {
                sb.Append($"P2SX(0)={win.PointsToScreenPixelsX(0)} P2SX(500)={win.PointsToScreenPixelsX(500)} P2SY(0)={win.PointsToScreenPixelsY(0)} P2SY(500)={win.PointsToScreenPixelsY(500)}");
                dynamic rects = win.ActivePane.Pages[pageNumber].Rectangles;
                int n = Convert.ToInt32(rects.Count);
                for (int i = 1; i <= Math.Min(n, 3); i++)
                {
                    dynamic rc = rects[i];
                    sb.Append($" R{i}[type {rc.RectangleType}] L{rc.Left} T{rc.Top} W{rc.Width} H{rc.Height}");
                }
            }
            catch (Exception ex)
            {
                sb.Append("Pages error " + ex.Message);
            }
            try
            {
                dynamic doc = win.Document;
                dynamic start = doc.GoTo(1, 1, pageNumber);
                win.GetPoint(out int sl, out int st, out int sw, out int sh, start);
                double hx = Convert.ToDouble(start.Information[5]);
                double hy = Convert.ToDouble(start.Information[6]);
                sb.Append($" | pageStart screen ({sl},{st},{sw}x{sh}) pagePos ({hx:F1},{hy:F1})");
            }
            catch (Exception ex)
            {
                sb.Append(" | pageStart error " + ex.Message);
            }
            return sb.ToString();
        }

        /// <summary>What Word's RangeFromPoint returns at a screen pixel, for diagnosing failed probes.</summary>
        private static string DescribeHit(dynamic win, Point pt)
        {
            try
            {
                dynamic hit = win.RangeFromPoint(pt.X, pt.Y);
                if (hit == null)
                    return "null";
                dynamic r = hit.Duplicate;
                r.Collapse(1);
                int page = Convert.ToInt32(r.Information[3]);
                double hx = Convert.ToDouble(r.Information[5]);
                double hy = Convert.ToDouble(r.Information[6]);
                win.GetPoint(out int sl, out int st, out int sw, out int sh, r);
                string text = Convert.ToString(hit.Text) ?? string.Empty;
                text = text.Replace("\r", "\\r").Replace("\f", "\\f");
                if (text.Length > 20) text = text.Substring(0, 20);
                return $"start {hit.Start} page {page} pagePos ({hx:F1},{hy:F1}) screen ({sl},{st}) text '{text}'";
            }
            catch (Exception ex)
            {
                return "error " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string VerifyLastShape(dynamic doc, Target t, double expLeft, double expTop)
        {
            int count = Convert.ToInt32(doc.Shapes.Count);
            dynamic shape = doc.Shapes[count];
            try
            {
                // Re-read actual position from the document after layout; page comes from where Word draws it
                int page = Convert.ToInt32(shape.Anchor.Information[3]);
                int relH = Convert.ToInt32(shape.RelativeHorizontalPosition);
                int relV = Convert.ToInt32(shape.RelativeVerticalPosition);
                double cx = Convert.ToDouble(shape.Left) + Convert.ToDouble(shape.Width) / 2;
                double cy = Convert.ToDouble(shape.Top) + Convert.ToDouble(shape.Height) / 2;
                int wrap = Convert.ToInt32(shape.WrapFormat.Type);
                int allowOverlap = Convert.ToInt32(shape.WrapFormat.AllowOverlap);
                if (allowOverlap == 0)
                {
                    Console.WriteLine("    AllowOverlap is false on the inserted stamp");
                    _failures++;
                }
                double errMm = DistMm(cx - expLeft, cy - expTop);
                Marshal.ReleaseComObject(shape);
                shape = null;

                // A single Undo must remove the whole stamp (insert + wrap + position + rotation)
                doc.Undo(1);
                bool undoOk = Convert.ToInt32(doc.Shapes.Count) == count - 1;
                if (!undoOk)
                {
                    while (Convert.ToInt32(doc.Shapes.Count) > count - 1)
                        doc.Shapes[Convert.ToInt32(doc.Shapes.Count)].Delete();
                }

                bool ok = page == t.Page && relH == 1 && relV == 1 && wrap == 5 && errMm <= 1.0 && undoOk;
                if (!ok) _failures++;
                return $"shape p{page} ({cx:F1},{cy:F1}) rel {relH}/{relV} wrap {wrap} undo1 {(undoOk ? "yes" : "NO")} err {errMm:F2}mm {(ok ? "OK" : "BAD")}";
            }
            finally
            {
                if (shape != null)
                    Marshal.ReleaseComObject(shape);
            }
        }

        private static dynamic AddMarker(dynamic doc, Target t)
        {
            dynamic pageStart = doc.GoTo(1, 1, t.Page);
            dynamic shape = doc.Shapes.AddShape(1, (float)t.LeftPt, (float)t.TopPt, (float)MarkerSizePt, (float)MarkerSizePt, pageStart);
            shape.WrapFormat.Type = 3;
            shape.RelativeHorizontalPosition = 1;
            shape.RelativeVerticalPosition = 1;
            shape.Left = (float)t.LeftPt;
            shape.Top = (float)t.TopPt;
            shape.Line.Visible = 0;
            shape.Fill.ForeColor.RGB = MarkerRgb;
            shape.Fill.Solid();
            Marshal.ReleaseComObject(pageStart);
            return shape;
        }

        /// <summary>Screen-captures the _WwG area and returns a per-pixel mask (1 = pure marker green).</summary>
        private static byte[] CaptureGreenMask(IntPtr wwg, out RECT r)
        {
            GetWindowRect(wwg, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0)
                return null;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));

                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] buf = new byte[stride * h];
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                    var mask = new byte[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int i = row + x * 4;
                            byte b = buf[i], gg = buf[i + 1], rr = buf[i + 2];
                            if (gg > 200 && rr < 80 && b < 80)
                                mask[y * w + x] = 1;
                        }
                    }
                    return mask;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
        }

        private static Point? GreenCentroid(byte[] mask, RECT r)
        {
            return DiffCentroid(mask, new byte[mask.Length], r, out _);
        }

        private static Point? DiffCentroid(byte[] withMarker, byte[] withoutMarker, RECT r, out long n)
        {
            int w = r.Right - r.Left;
            long sx = 0, sy = 0;
            n = 0;
            for (int i = 0; i < withMarker.Length; i++)
            {
                if (withMarker[i] == 1 && withoutMarker[i] == 0)
                {
                    sx += i % w; sy += i / w; n++;
                }
            }
            if (n < 4)
                return null;
            return new Point(r.Left + (int)Math.Round((double)sx / n), r.Top + (int)Math.Round((double)sy / n));
        }

        private static void MoveToScreen(IntPtr root, Screen screen)
        {
            ShowWindow(root, 9 /* SW_RESTORE */);
            Rectangle wa = screen.WorkingArea;
            SetWindowPos(root, IntPtr.Zero, wa.Left + 40, wa.Top + 40, Math.Max(800, wa.Width - 80), Math.Max(600, wa.Height - 80), 0x0040 /* SWP_SHOWWINDOW */);
            Pump(300);
            ShowWindow(root, 3 /* SW_MAXIMIZE */);
            // Keep the disposable Word window above everything while measuring so screen captures see it
            SetWindowPos(root, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
            SetForegroundWindow(root);
            Pump(1200);
        }

        private static IntPtr FindWwG(IntPtr root)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(root, (h, l) =>
            {
                var sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "_WwG" && IsWindowVisible(h))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static void DumpMonitors()
        {
            foreach (Screen s in Screen.AllScreens)
            {
                IntPtr mon = MonitorFromPoint(new POINT { X = s.Bounds.Left + 1, Y = s.Bounds.Top + 1 }, 2);
                GetDpiForMonitor(mon, 0, out uint dx, out uint dy);
                Console.WriteLine($"Monitor {s.DeviceName} bounds {s.Bounds} dpi {dx} ({dx * 100 / 96}%) primary={s.Primary}");
            }
        }

        private static void DumpWindowDpi(string label, IntPtr hwnd)
        {
            try
            {
                IntPtr ctx = GetWindowDpiAwarenessContext(hwnd);
                int awareness = GetAwarenessFromDpiAwarenessContext(ctx);
                uint dpi = GetDpiForWindow(hwnd);
                string name = awareness == 0 ? "Unaware" : awareness == 1 ? "SystemAware" : awareness == 2 ? "PerMonitor" : awareness.ToString();
                Console.WriteLine($"{label}: awareness={name} GetDpiForWindow={dpi}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: dpi query failed {ex.Message}");
            }
        }

        private static double DistMm(double dxPt, double dyPt) => Math.Sqrt(dxPt * dxPt + dyPt * dyPt) / CoordinateTransformer.PointsPerMillimeter;

        private static void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    }
}
