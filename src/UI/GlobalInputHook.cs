using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopStamp.Core;

namespace DesktopStamp.UI
{
    /// <summary>
    /// Manages low-level mouse and keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL) with:
    /// - Fast no-marshal pass-through when parked in Idle
    /// - High-resolution wheel delta accumulation (120 units per notch) for precision touchpads/smooth wheels
    /// - Zero input trapping (fail-open CallNextHookEx on any exception or non-aiming state)
    /// - Single native pass-through for plain MouseWheel (underlying Word scrolls once, never twice)
    /// - Feedback loop prevention via LLMHF_INJECTED filtering
    /// - Guaranteed unhook on Dispose, ProcessExit, and UnhandledException
    /// </summary>
    public sealed class GlobalInputHook : IDisposable
    {
        public const int WheelDeltaPerNotch = 120;

        private IntPtr _mouseHookHandle = IntPtr.Zero;
        private IntPtr _keyboardHookHandle = IntPtr.Zero;

        // Keep strong references to delegates so GC never collects them while native hooks are active
        private readonly NativeMethods.LowLevelHookProc _mouseProc;
        private readonly NativeMethods.LowLevelHookProc _keyboardProc;

        private bool _swallowedLeftDown;
        private bool _swallowedRightDown;
        private int _scaleWheelRemainder;
        private int _rotateWheelRemainder;
        private bool _disposed;

        /// <summary>
        /// Delegate queried by the hook to check current state machine mode without locking.
        /// </summary>
        public Func<StampState> QueryCurrentState { get; set; }

        /// <summary>
        /// Delegate queried to check if a screen point lies on the outer rotation ring annulus in Aiming mode.
        /// Must execute in pure math without Win32/COM blocking calls.
        /// </summary>
        public Func<Vector2D, bool> IsPointOnOuterReticleRing { get; set; }

        /// <summary>
        /// Delegate queried to check if our own context menu is currently visible and contains the point.
        /// </summary>
        public Func<Vector2D, bool> IsPointInsideOwnMenu { get; set; }

        /// <summary>
        /// Delegate queried to check virtual key state (VK_CONTROL, VK_MENU). Defaults to NativeMethods.IsKeyDown.
        /// </summary>
        public Func<int, bool> QueryKeyState { get; set; } = NativeMethods.IsKeyDown;

        public event Action<Vector2D> PointerMoved;
        public event Action<Vector2D> PickupDragReleased;
        public event Action<Vector2D> AimingLeftClickCommitted;
        public event Action<Vector2D> AimingRightClickCancelled;
        public event Action<int> CtrlWheelScaled;
        public event Action<int> AltOrRingWheelRotated;
        public event Action PlainWheelScrolled;
        public event Action EscapePressed;
        public event Action<int> NavigationKeyPressed;

        public bool IsInstalled => _mouseHookHandle != IntPtr.Zero && _keyboardHookHandle != IntPtr.Zero;
        public int LastInstallErrorCode { get; private set; }

        public GlobalInputHook()
        {
            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;
        }

        /// <summary>
        /// Accumulates high-resolution wheel deltas (e.g. ±15/±30 from precision touchpads or ±120 from notched wheels)
        /// into whole 120-unit notches, resetting remainder immediately when direction reverses.
        /// </summary>
        public static int AccumulateWheelDeltaToNotches(int wheelDelta, ref int remainder)
        {
            if (wheelDelta == 0)
            {
                return 0;
            }

            if ((wheelDelta > 0 && remainder < 0) || (wheelDelta < 0 && remainder > 0))
            {
                remainder = 0;
            }

            remainder += wheelDelta;
            int notches = remainder / WheelDeltaPerNotch;
            remainder %= WheelDeltaPerNotch;
            return notches;
        }

        public void ResetWheelAccumulators()
        {
            _scaleWheelRemainder = 0;
            _rotateWheelRemainder = 0;
        }

        /// <summary>
        /// Drives the real MouseHookCallback with an unmanaged MSLLHOOKSTRUCT in memory for controlled
        /// hook-routing tests (since OS-level SendInput sets LLMHF_INJECTED which MouseHookCallback filters out).
        /// Returns true if the hook swallowed the event (non-zero return), or false if passed through to CallNextHookEx.
        /// </summary>
        public bool SimulateLowLevelMouseMessage(int msg, Vector2D screenPoint, short wheelDelta = 0, uint flags = 0)
        {
            var hookStruct = new NativeMethods.MSLLHOOKSTRUCT
            {
                pt = new NativeMethods.POINT((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)),
                mouseData = (uint)((ushort)wheelDelta << 16),
                flags = flags,
                time = (uint)(Environment.TickCount & int.MaxValue),
                dwExtraInfo = IntPtr.Zero
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.MSLLHOOKSTRUCT)));
            try
            {
                Marshal.StructureToPtr(hookStruct, ptr, false);
                IntPtr res = MouseHookCallback(0, new IntPtr(msg), ptr);
                return res != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Drives the real KeyboardHookCallback with an unmanaged KBDLLHOOKSTRUCT in memory for controlled
        /// hook-routing tests. Returns true if the hook swallowed the key (non-zero return), or false if passed through.
        /// </summary>
        public bool SimulateLowLevelKeyboardMessage(int msg, int vkCode, uint flags = 0)
        {
            var hookStruct = new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = (uint)vkCode,
                scanCode = 0,
                flags = flags,
                time = (uint)(Environment.TickCount & int.MaxValue),
                dwExtraInfo = IntPtr.Zero
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.KBDLLHOOKSTRUCT)));
            try
            {
                Marshal.StructureToPtr(hookStruct, ptr, false);
                IntPtr res = KeyboardHookCallback(0, new IntPtr(msg), ptr);
                return res != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Returns true if the virtual key code can scroll, navigate, or change zoom in the underlying document viewport.
        /// </summary>
        public static bool IsViewportOrDocumentNavigationKey(int vkCode, bool ctrlDown = false)
        {
            switch (vkCode)
            {
                case NativeMethods.VK_PRIOR: // PageUp
                case NativeMethods.VK_NEXT:  // PageDown
                case NativeMethods.VK_HOME:
                case NativeMethods.VK_END:
                case NativeMethods.VK_UP:
                case NativeMethods.VK_DOWN:
                case NativeMethods.VK_LEFT:
                case NativeMethods.VK_RIGHT:
                case NativeMethods.VK_SPACE:
                case NativeMethods.VK_RETURN:
                case NativeMethods.VK_TAB:
                    return true;
                case NativeMethods.VK_ADD:
                case NativeMethods.VK_SUBTRACT:
                case NativeMethods.VK_OEM_PLUS:
                case NativeMethods.VK_OEM_MINUS:
                case 0x30: // '0' (Ctrl+0 zoom reset)
                    return ctrlDown;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Installs both low-level mouse and keyboard hooks. If either hook fails to install,
        /// rolls back and returns false so the caller can detect and report the failure.
        /// </summary>
        public bool Install()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GlobalInputHook));

            if (IsInstalled)
                return true;

            LastInstallErrorCode = 0;
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hMod = NativeMethods.GetModuleHandle(curModule?.ModuleName);
                if (_mouseHookHandle == IntPtr.Zero)
                {
                    _mouseHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
                    if (_mouseHookHandle == IntPtr.Zero)
                    {
                        LastInstallErrorCode = Marshal.GetLastWin32Error();
                        Uninstall();
                        return false;
                    }
                }
                if (_keyboardHookHandle == IntPtr.Zero)
                {
                    _keyboardHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
                    if (_keyboardHookHandle == IntPtr.Zero)
                    {
                        LastInstallErrorCode = Marshal.GetLastWin32Error();
                        Uninstall();
                        return false;
                    }
                }
            }

            return IsInstalled;
        }

        public void Uninstall()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }
            _swallowedLeftDown = false;
            _swallowedRightDown = false;
            _scaleWheelRemainder = 0;
            _rotateWheelRemainder = 0;
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    StampState state = QueryCurrentState != null ? QueryCurrentState() : StampState.Idle;

                    // Fast zero-marshal exit when parked in Idle with no pending swallowed button-up
                    if (state == StampState.Idle && !_swallowedLeftDown && !_swallowedRightDown)
                    {
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                    // Ignore synthetic/injected events to prevent any feedback loops
                    if ((info.flags & NativeMethods.LLMHF_INJECTED) != 0)
                    {
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    int msg = wParam.ToInt32();
                    var pt = new Vector2D(info.pt.X, info.pt.Y);

                    if (msg == NativeMethods.WM_MOUSEMOVE)
                    {
                        if (state != StampState.Idle)
                        {
                            PointerMoved?.Invoke(pt);
                        }
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    // If clicking inside our own context menu, always allow normal WinForms processing
                    if (IsPointInsideOwnMenu != null && IsPointInsideOwnMenu(pt))
                    {
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    if (state == StampState.DraggingPickup)
                    {
                        if (msg == NativeMethods.WM_LBUTTONUP)
                        {
                            PickupDragReleased?.Invoke(pt);
                        }
                        else if (msg == NativeMethods.WM_RBUTTONDOWN)
                        {
                            _swallowedRightDown = true;
                            AimingRightClickCancelled?.Invoke(pt);
                            return new IntPtr(1);
                        }
                        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    if (state == StampState.Aiming || state == StampState.Stamping)
                    {
                        if (msg == NativeMethods.WM_LBUTTONDOWN)
                        {
                            _swallowedLeftDown = true;
                            if (state == StampState.Aiming)
                            {
                                AimingLeftClickCommitted?.Invoke(pt);
                            }
                            return new IntPtr(1); // Swallow click so underlying Word selection/focus is untouched
                        }

                        if (msg == NativeMethods.WM_LBUTTONUP && _swallowedLeftDown)
                        {
                            _swallowedLeftDown = false;
                            return new IntPtr(1);
                        }

                        if (msg == NativeMethods.WM_RBUTTONDOWN)
                        {
                            _swallowedRightDown = true;
                            AimingRightClickCancelled?.Invoke(pt);
                            return new IntPtr(1);
                        }

                        if (msg == NativeMethods.WM_RBUTTONUP && _swallowedRightDown)
                        {
                            _swallowedRightDown = false;
                            return new IntPtr(1);
                        }

                        if (msg == NativeMethods.WM_MOUSEWHEEL)
                        {
                            short wheelDelta = (short)((info.mouseData >> 16) & 0xFFFF);

                            Func<int, bool> keyQuery = QueryKeyState ?? NativeMethods.IsKeyDown;
                            bool ctrlDown = keyQuery(NativeMethods.VK_CONTROL);
                            bool altDown = keyQuery(NativeMethods.VK_MENU);
                            bool onOuterRing = IsPointOnOuterReticleRing != null && IsPointOnOuterReticleRing(pt);

                            if (ctrlDown)
                            {
                                _rotateWheelRemainder = 0;
                                int notches = AccumulateWheelDeltaToNotches(wheelDelta, ref _scaleWheelRemainder);
                                if (notches != 0)
                                {
                                    CtrlWheelScaled?.Invoke(notches);
                                }
                                return new IntPtr(1); // Swallow so Word doesn't zoom
                            }

                            if (altDown || onOuterRing)
                            {
                                _scaleWheelRemainder = 0;
                                int notches = AccumulateWheelDeltaToNotches(wheelDelta, ref _rotateWheelRemainder);
                                if (notches != 0)
                                {
                                    AltOrRingWheelRotated?.Invoke(notches);
                                }
                                return new IntPtr(1); // Swallow so Word doesn't scroll while rotating
                            }

                            _scaleWheelRemainder = 0;
                            _rotateWheelRemainder = 0;
                            PlainWheelScrolled?.Invoke();

                            // Plain wheel: pass straight through to OS once (with WS_EX_TRANSPARENT on the layered overlay,
                            // Windows delivers this single wheel event directly to the underlying Word window).
                            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                        }
                    }
                    else
                    {
                        // Clean up any pending button-up swallow flags if state returned to Idle
                        if (msg == NativeMethods.WM_LBUTTONUP && _swallowedLeftDown)
                        {
                            _swallowedLeftDown = false;
                            return new IntPtr(1);
                        }
                        if (msg == NativeMethods.WM_RBUTTONUP && _swallowedRightDown)
                        {
                            _swallowedRightDown = false;
                            return new IntPtr(1);
                        }
                    }
                }
                catch
                {
                    // Always fail open so desktop input is never trapped
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    StampState state = QueryCurrentState != null ? QueryCurrentState() : StampState.Idle;
                    if (state == StampState.Idle)
                    {
                        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
                    }

                    int msg = wParam.ToInt32();
                    if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                    {
                        var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                        if ((kb.flags & NativeMethods.LLKHF_INJECTED) != 0)
                        {
                            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
                        }

                        int vk = (int)kb.vkCode;
                        if (vk == NativeMethods.VK_ESCAPE)
                        {
                            EscapePressed?.Invoke();
                            return new IntPtr(1);
                        }

                        if (state == StampState.Aiming || state == StampState.Stamping)
                        {
                            Func<int, bool> keyQuery = QueryKeyState ?? NativeMethods.IsKeyDown;
                            bool ctrlDown = keyQuery(NativeMethods.VK_CONTROL);
                            if (IsViewportOrDocumentNavigationKey(vk, ctrlDown))
                            {
                                // Notify non-blockingly so ViewEpoch advances and pending click snapshots invalidate,
                                // while passing the key straight through to the underlying document window!
                                NavigationKeyPressed?.Invoke(vk);
                            }
                        }
                    }
                }
                catch
                {
                    // Fail open
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Uninstall();
                GC.SuppressFinalize(this);
            }
        }

        ~GlobalInputHook()
        {
            Uninstall();
        }
    }
}
