using System;
using System.Collections.Generic;
using System.Diagnostics;
using DesktopStamp.Core;
using DesktopStamp.UI;

namespace DesktopStamp.HostAdapters
{
    /// <summary>
    /// Routes target probes and stamp placement requests to registered IStampHostAdapter implementations.
    /// Fails closed whenever the underlying desktop window is not a verified, supported document target.
    /// </summary>
    public sealed class CompositeStampHostRouter
    {
        private readonly List<IStampHostAdapter> _adapters = new List<IStampHostAdapter>();
        private readonly uint _currentProcessId;
        private readonly Dictionary<uint, string> _processNameCache = new Dictionary<uint, string>();

        public IReadOnlyList<IStampHostAdapter> Adapters => _adapters;

        public CompositeStampHostRouter()
        {
            _currentProcessId = (uint)Process.GetCurrentProcess().Id;
            _adapters.Add(new WordStampHostAdapter());
            _adapters.Add(new ExcelStampHostAdapter());
            _adapters.Add(new PdfStampHostAdapter());
            _adapters.Add(new ScreenshotStampHostAdapter());
        }

        public CompositeStampHostRouter(IEnumerable<IStampHostAdapter> customAdapters)
        {
            _currentProcessId = (uint)Process.GetCurrentProcess().Id;
            if (customAdapters != null)
            {
                _adapters.AddRange(customAdapters);
            }
        }

        public StampTargetProbe ProbeAtScreenPoint(Vector2D screenPointPx, IntPtr excludedOverlayHwnd, bool lightweight = false)
        {
            IntPtr targetHwnd = FindUnderlyingWindowAtPoint(screenPointPx, excludedOverlayHwnd);
            IntPtr rootHwnd = targetHwnd != IntPtr.Zero
                ? NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT)
                : IntPtr.Zero;

            string className = string.Empty;
            string rootClassName = string.Empty;
            string processName = string.Empty;
            if (targetHwnd != IntPtr.Zero)
            {
                GetWindowIdentity(targetHwnd, out className, out rootClassName, out processName);
            }

            for (int i = 0; i < _adapters.Count; i++)
            {
                IStampHostAdapter adapter = _adapters[i];
                if (adapter.CanHandleWindow(targetHwnd, className, rootClassName, processName))
                {
                    StampTargetProbe probe = adapter.ProbeTarget(screenPointPx, targetHwnd, lightweight);
                    if (probe != null && probe.RootHwnd == IntPtr.Zero)
                    {
                        probe.RootHwnd = rootHwnd;
                    }
                    return probe;
                }
            }

            return new StampTargetProbe
            {
                IsValidTarget = false,
                AdapterName = targetHwnd == IntPtr.Zero ? "None" : "UnsupportedWindow",
                TargetHwnd = targetHwnd,
                RootHwnd = rootHwnd,
                StatusSummary = "请移到 Word 文档页面"
            };
        }

        public StampPlacementResult TryApplyStamp(StampRequest request, IntPtr excludedOverlayHwnd)
        {
            if (request == null)
                return StampPlacementResult.Rejected("CompositeStampHostRouter", "请移到 Word 文档页面");

            if (request.TryGetInvalidationReason(out string invalidReason))
            {
                return StampPlacementResult.Rejected("CompositeStampHostRouter", invalidReason, abortedByEpoch: true);
            }

            IntPtr targetHwnd = FindUnderlyingWindowAtPoint(request.ScreenPointPx, excludedOverlayHwnd);
            IntPtr actualRootHwnd = targetHwnd != IntPtr.Zero
                ? NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT)
                : IntPtr.Zero;

            // Enforce click-time target/root HWND binding so a stamp never inserts into an unrelated newly hovered Word window
            if (request.ExpectedTargetHwnd != IntPtr.Zero && targetHwnd != request.ExpectedTargetHwnd)
            {
                return StampPlacementResult.Rejected(
                    "CompositeStampHostRouter",
                    "目标文档窗口已变化，已取消本次盖章",
                    abortedByEpoch: true);
            }

            if (request.ExpectedRootHwnd != IntPtr.Zero && actualRootHwnd != request.ExpectedRootHwnd)
            {
                return StampPlacementResult.Rejected(
                    "CompositeStampHostRouter",
                    "目标文档窗口已变化，已取消本次盖章",
                    abortedByEpoch: true);
            }

            string className = string.Empty;
            string rootClassName = string.Empty;
            string processName = string.Empty;
            if (targetHwnd != IntPtr.Zero)
            {
                GetWindowIdentity(targetHwnd, out className, out rootClassName, out processName);
            }

            for (int i = 0; i < _adapters.Count; i++)
            {
                IStampHostAdapter adapter = _adapters[i];
                if (adapter.CanHandleWindow(targetHwnd, className, rootClassName, processName))
                {
                    return adapter.TryApplyStamp(request, targetHwnd);
                }
            }

            return StampPlacementResult.Rejected(
                "CompositeStampHostRouter",
                "请移到 Word 文档页面");
        }

        /// <summary>
        /// Finds the deepest visible child window at <paramref name="screenPointPx"/> belonging to the highest
        /// Z-order top-level window other than our own overlay process.
        /// </summary>
        public IntPtr FindUnderlyingWindowAtPoint(Vector2D screenPointPx, IntPtr excludedOverlayHwnd)
        {
            int px = (int)Math.Round(screenPointPx.X);
            int py = (int)Math.Round(screenPointPx.Y);
            var pt = new NativeMethods.POINT(px, py);

            // Fast path: when our overlay is click-through (WS_EX_TRANSPARENT), WindowFromPoint directly returns the underlying window
            IntPtr directHwnd = NativeMethods.WindowFromPoint(pt);
            if (directHwnd != IntPtr.Zero && directHwnd != excludedOverlayHwnd)
            {
                NativeMethods.GetWindowThreadProcessId(directHwnd, out uint pid);
                if (pid != _currentProcessId)
                {
                    string directCls = NativeMethods.GetWindowClassName(directHwnd);
                    if (string.Equals(directCls, "_WwG", StringComparison.OrdinalIgnoreCase))
                    {
                        return directHwnd;
                    }

                    IntPtr resolvedFromDirect = DrillIntoChildWindowAtPoint(directHwnd, px, py);
                    return resolvedFromDirect != IntPtr.Zero ? resolvedFromDirect : directHwnd;
                }
            }

            // Fallback path: enumerate top-level windows in Z-order skipping our own process
            IntPtr foundTopWindow = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == excludedOverlayHwnd || !NativeMethods.IsWindowVisible(hWnd))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == _currentProcessId)
                    return true;

                if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT r))
                {
                    if (px >= r.Left && px < r.Right && py >= r.Top && py < r.Bottom)
                    {
                        string cls = NativeMethods.GetWindowClassName(hWnd);
                        if (!string.Equals(cls, "Ghost", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(cls, "ShadowWindow", StringComparison.OrdinalIgnoreCase))
                        {
                            foundTopWindow = hWnd;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (foundTopWindow == IntPtr.Zero)
                return IntPtr.Zero;

            return DrillIntoChildWindowAtPoint(foundTopWindow, px, py);
        }

        private static IntPtr DrillIntoChildWindowAtPoint(IntPtr parentWindow, int px, int py)
        {
            IntPtr bestChild = parentWindow;
            int smallestArea = int.MaxValue;

            NativeMethods.EnumChildWindows(parentWindow, (childHwnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(childHwnd))
                    return true;

                if (NativeMethods.GetWindowRect(childHwnd, out NativeMethods.RECT r))
                {
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w > 4 && h > 4 && px >= r.Left && px < r.Right && py >= r.Top && py < r.Bottom)
                    {
                        string childCls = NativeMethods.GetWindowClassName(childHwnd);
                        if (string.Equals(childCls, "_WwG", StringComparison.OrdinalIgnoreCase))
                        {
                            bestChild = childHwnd;
                            return false;
                        }

                        int area = w * h;
                        if (area <= smallestArea)
                        {
                            smallestArea = area;
                            bestChild = childHwnd;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            return bestChild;
        }

        private void GetWindowIdentity(IntPtr hWnd, out string className, out string rootClassName, out string processName)
        {
            className = NativeMethods.GetWindowClassName(hWnd);
            IntPtr rootHwnd = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
            rootClassName = rootHwnd != IntPtr.Zero ? NativeMethods.GetWindowClassName(rootHwnd) : className;

            // Fast short-circuit for Word windows without needing any process query
            if (string.Equals(className, "_WwG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootClassName, "OpusApp", StringComparison.OrdinalIgnoreCase))
            {
                processName = "WINWORD";
                return;
            }

            processName = string.Empty;
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
                return;

            lock (_processNameCache)
            {
                if (_processNameCache.TryGetValue(pid, out string cached) && !string.IsNullOrEmpty(cached))
                {
                    processName = cached;
                    return;
                }
            }

            string queried = NativeMethods.QueryProcessNameFast(pid);
            if (!string.IsNullOrEmpty(queried))
            {
                lock (_processNameCache)
                {
                    if (_processNameCache.Count > 128)
                        _processNameCache.Clear();
                    _processNameCache[pid] = queried;
                }
                processName = queried;
            }
        }
    }
}
