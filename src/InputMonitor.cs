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
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);

        private RawInputSink _raw;

        /// <summary>True once raw input is registered (diagnostics).</summary>
        public bool RawInputActive => _raw != null && _raw.Registered;

        private void Raise(bool isMouse, uint vk, IntPtr root)
        {
            try { UserInput?.Invoke(isMouse, vk, root); } catch { }
        }

        /// <summary>
        /// Every real key press (raw input) with the modifiers held at that moment, raised on the
        /// hook thread — used to recognise our hotkeys even when another app swallows them.
        /// </summary>
        public event Action<System.Windows.Forms.Keys> KeyDown;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        private void RaiseKeyDown(ushort vk)
        {
            var k = (System.Windows.Forms.Keys)vk;
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0) k |= System.Windows.Forms.Keys.Control;
            if ((GetAsyncKeyState(0x12) & 0x8000) != 0) k |= System.Windows.Forms.Keys.Alt;
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0) k |= System.Windows.Forms.Keys.Shift;
            try { KeyDown?.Invoke(k); } catch { }
        }

        /// <summary>
        /// Message-only window that receives WM_INPUT for every mouse button press and key press
        /// in the system (RIDEV_INPUTSINK). Input we inject with SendInput arrives without a device
        /// handle and is ignored, so only real hardware counts.
        /// </summary>
        private sealed class RawInputSink : System.Windows.Forms.NativeWindow
        {
            private const int WM_INPUT = 0x00FF;
            private const uint RID_INPUT = 0x10000003;
            private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
            private const uint RIDEV_INPUTSINK = 0x00000100;
            private const ushort RI_MOUSE_BUTTON_DOWNS = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
            private const ushort RI_KEY_BREAK = 0x0001;

            [StructLayout(LayoutKind.Sequential)]
            private struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

            [StructLayout(LayoutKind.Sequential)]
            private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

            [DllImport("user32.dll")]
            private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

            [DllImport("user32.dll")]
            private static extern bool GetCursorPos(out POINT pt);

            private readonly InputMonitor _owner;
            private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            public bool Registered { get; }

            public RawInputSink(InputMonitor owner)
            {
                _owner = owner;
                CreateHandle(new System.Windows.Forms.CreateParams { Parent = new IntPtr(-3) });   // HWND_MESSAGE
                var devs = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle }, // mouse
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = Handle }, // keyboard
                };
                Registered = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_INPUT) OnRawInput(m.LParam);
                base.WndProc(ref m);
            }

            private void OnRawInput(IntPtr hRaw)
            {
                uint size = 0;
                GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, HeaderSize);
                if (size == 0 || size > 1024) return;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, HeaderSize) != size) return;
                    var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                    IntPtr data = buf + (int)HeaderSize;
                    if (hdr.dwType == RIM_TYPEMOUSE)
                    {
                        // every click is a person (we never click)
                        // RAWMOUSE: usFlags (2) + pad (2), then usButtonFlags
                        ushort buttons = (ushort)Marshal.ReadInt16(data, 4);
                        if ((buttons & RI_MOUSE_BUTTON_DOWNS) == 0) return;
                        GetCursorPos(out POINT pt);
                        _owner.Raise(true, 0, GetAncestor(WindowFromPoint(pt), GA_ROOT));
                    }
                    else if (hdr.dwType == RIM_TYPEKEYBOARD)
                    {
                        // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey, Message, ExtraInformation
                        ushort flags = (ushort)Marshal.ReadInt16(data, 2);
                        ushort vk = (ushort)Marshal.ReadInt16(data, 6);
                        uint extra = (uint)Marshal.ReadInt32(data, 12);
                        if (extra == (uint)InjectMarker.ToInt64()) return;   // our own keystroke
                        if ((flags & RI_KEY_BREAK) != 0 || IsModifier(vk) || vk == 0xFF) return;
                        // Hotkeys: remote-control apps grab the keyboard while their window has focus,
                        // so RegisterHotKey never fires there — raw input still sees the key.
                        _owner.RaiseKeyDown(vk);
                        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) return;   // Alt held (Alt+Tab etc.)
                        _owner.Raise(false, vk, IntPtr.Zero);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vk);
        }

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
                // Raw input as a second channel: remote-control apps (ToDesk, Sunlogin…) install
                // their own low-level hooks and may swallow clicks/keys before ours sees them,
                // but they can't keep raw input from reaching us.
                try { _raw = new RawInputSink(this); } catch { _raw = null; }
                ready.Set();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                _raw?.DestroyHandle();
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
                    // Only our own keystrokes carry InjectMarker. Keys injected by other software —
                    // e.g. ToDesk/Sunlogin replaying the remote operator's keyboard — are a person too.
                    bool ours = k.dwExtraInfo == InjectMarker;
                    if (!ours && !IsModifier(k.vkCode) && (k.flags & LLKHF_ALTDOWN) == 0)
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
                    // HumanTyper never clicks, so every click is a person — local, or a remote
                    // operator whose clicks the remote-control software injects.
                    var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    IntPtr root = GetAncestor(WindowFromPoint(m.pt), GA_ROOT);
                    try { UserInput?.Invoke(true, 0, root); } catch { }
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
