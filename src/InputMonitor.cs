using System;
using System.Runtime.InteropServices;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    /// <summary>
    /// Global low-level keyboard/mouse hooks that report physical (non-injected) user input.
    /// Must be installed on a thread with a message loop (the UI thread).
    /// </summary>
    internal sealed class InputMonitor : IDisposable
    {
        /// <summary>(isMouse, vkCode, rootWindowUnderMouse)</summary>
        public event Action<bool, uint, IntPtr> UserInput;

        private readonly LowLevelProc _kbProc;
        private readonly LowLevelProc _msProc;
        private IntPtr _kbHook, _msHook;

        public InputMonitor()
        {
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
        }

        public void Install()
        {
            IntPtr mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
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
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
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
            return CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        private static bool IsModifier(uint vk) =>
            vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14;

        public void Dispose()
        {
            if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
        }
    }
}
