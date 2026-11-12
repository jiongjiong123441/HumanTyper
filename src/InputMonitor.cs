using System;
using System.Runtime.InteropServices;
using System.Threading;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    /// <summary>
    /// Global low-level keyboard/mouse hooks that report physical (non-injected) user input.
    /// Runs on its own thread with its own message loop so a click is noticed immediately,
    /// even while the UI thread is busy — this keeps our next keystroke from landing at the
    /// place the user just clicked.
    /// </summary>
    internal sealed class InputMonitor : IDisposable
    {
        /// <summary>(isMouse, vkCode, rootWindowUnderMouse) — raised on the hook thread.</summary>
        public event Action<bool, uint, IntPtr> UserInput;

        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private readonly LowLevelProc _kbProc;
        private readonly LowLevelProc _msProc;
        private IntPtr _kbHook, _msHook;
        private Thread _thread;
        private uint _threadId;

        public InputMonitor()
        {
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
        }

        public void Install()
        {
            if (_thread != null) return;
            var ready = new ManualResetEvent(false);
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                IntPtr mod = GetModuleHandle(null);
                _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
                _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
                ready.Set();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { }
                if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
                if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
                _kbHook = _msHook = IntPtr.Zero;
            })
            { IsBackground = true, Name = "HumanTyper.Hooks", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            ready.WaitOne(2000);
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & LLKHF_INJECTED) != 0 || k.dwExtraInfo == InjectMarker;
                    if (!injected && !IsModifier(k.vkCode) && (k.flags & LLKHF_ALTDOWN) == 0)
                    {
                        try { UserInput?.Invoke(false, k.vkCode, IntPtr.Zero); } catch { }
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
                {
                    var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if ((m.flags & LLMHF_INJECTED) == 0)
                    {
                        IntPtr root = GetAncestor(WindowFromPoint(m.pt), GA_ROOT);
                        try { UserInput?.Invoke(true, 0, root); } catch { }
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static bool IsModifier(uint vk) =>
            vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14;

        public void Dispose()
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }
    }
}
