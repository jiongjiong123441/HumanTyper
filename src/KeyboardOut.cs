using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    internal enum InputMode
    {
        Unicode = 0,    // 标准：直接发送 Unicode 字符（本机程序，中文直接上屏）
        ScanCode = 1,   // 键盘扫描码：和实体键盘相同的按键信号（远程桌面 / 虚拟机）
        Driver = 2,     // 驱动级：通过 Interception 驱动从键盘驱动层发出按键
    }

    /// <summary>
    /// Sends keystrokes either as Unicode characters (local apps) or as real key presses —
    /// scan codes via SendInput or via the Interception keyboard driver — so they pass through
    /// remote-desktop / remote-control windows the same way a physical keyboard does.
    /// </summary>
    internal sealed class KeyboardOut
    {
        public const ushort SC_LSHIFT = 0x2A, SC_LCTRL = 0x1D, SC_SPACE = 0x39, SC_ENTER = 0x1C,
                            SC_BACK = 0x0E, SC_TAB = 0x0F, SC_LEFT = 0x4B, SC_RIGHT = 0x4D,
                            SC_HOME = 0x47, SC_C = 0x2E;

        private readonly Random _rnd = new Random();
        public InputMode Mode { get; }

        /// <summary>
        /// Tick count until which keyboard events seen by our hooks are taken to be our own echo.
        /// Driver-level keys carry no marker at all, and raw input may not pass the marker on, so
        /// every send opens a short window in which keyboard events aren't blamed on the user.
        /// </summary>
        private static long _selfUntil;
        public static bool IsSelfEcho => Environment.TickCount - Interlocked.Read(ref _selfUntil) < 0;
        private static void MarkSelf() => Interlocked.Exchange(ref _selfUntil, Environment.TickCount + 120);

        public KeyboardOut(InputMode mode)
        {
            Mode = mode;
            if (mode == InputMode.Driver && !Interception.Open())
                throw new InvalidOperationException(Interception.LastError);
        }

        public bool IsKeyMode => Mode != InputMode.Unicode;

        // ------------------------------------------------------------------ high level

        /// <summary>Types one visible character (may be a surrogate pair).</summary>
        public void Char(string unit)
        {
            if (Mode == InputMode.Unicode || !TryCharToKey(unit, out ushort sc, out bool shift))
            {
                UnicodeChar(unit);
                return;
            }
            KeyTap(sc, false, shift);
        }

        /// <summary>True if the character can be produced by a key on the current keyboard layout.</summary>
        public static bool CanType(string unit) => TryCharToKey(unit, out _, out _);

        public void Enter(bool shift) => Special(VK_RETURN, SC_ENTER, false, shift);
        public void Backspace() => Special(VK_BACK, SC_BACK, false, false);
        public void Tab() => Special(VK_TAB, SC_TAB, false, false);
        public void Left() => Special(VK_LEFT, SC_LEFT, true, false);
        public void Right() => Special(VK_RIGHT, SC_RIGHT, true, false);
        public void Space() => KeyTap(SC_SPACE, false, false);

        /// <summary>Taps Shift on its own (toggles Chinese/English in most Chinese IMEs).</summary>
        public void LoneShift()
        {
            Key(SC_LSHIFT, false, false, VK_SHIFT);
            Thread.Sleep(_rnd.Next(40, 90));
            Key(SC_LSHIFT, false, true, VK_SHIFT);
        }

        /// <summary>Ctrl+Space (alternative IME toggle).</summary>
        public void CtrlSpace()
        {
            Key(SC_LCTRL, false, false, 0x11);
            Thread.Sleep(_rnd.Next(30, 70));
            KeyTap(SC_SPACE, false, false);
            Thread.Sleep(_rnd.Next(20, 50));
            Key(SC_LCTRL, false, true, 0x11);
        }

        /// <summary>Ctrl+Shift+Home: selects everything from the caret back to the start of the text.</summary>
        public void SelectToStart() => Chord(0x24, SC_HOME, true, true, true);

        /// <summary>Ctrl+C.</summary>
        public void Copy() => Chord(0x43, SC_C, false, true, false);

        /// <summary>A key with Ctrl and/or Shift held (modifiers pressed first, released last).</summary>
        private void Chord(ushort vk, ushort sc, bool extended, bool ctrl, bool shift)
        {
            if (ctrl) { Key(SC_LCTRL, false, false, 0x11); Thread.Sleep(_rnd.Next(30, 60)); }
            if (shift) { Key(SC_LSHIFT, false, false, VK_SHIFT); Thread.Sleep(_rnd.Next(30, 60)); }
            Key(sc, extended, false, vk);
            Thread.Sleep(_rnd.Next(40, 80));
            Key(sc, extended, true, vk);
            Thread.Sleep(_rnd.Next(20, 50));
            if (shift) { Key(SC_LSHIFT, false, true, VK_SHIFT); Thread.Sleep(_rnd.Next(15, 40)); }
            if (ctrl) Key(SC_LCTRL, false, true, 0x11);
        }

        /// <summary>Presses a key by its scan code, optionally with Shift held.</summary>
        public void KeyTap(ushort sc, bool extended, bool shift)
        {
            if (shift)
            {
                Key(SC_LSHIFT, false, false, VK_SHIFT);
                Thread.Sleep(_rnd.Next(25, 70));
            }
            Key(sc, extended, false, 0);
            Thread.Sleep(_rnd.Next(25, 80));          // key hold time
            Key(sc, extended, true, 0);
            if (shift)
            {
                Thread.Sleep(_rnd.Next(15, 50));
                Key(SC_LSHIFT, false, true, VK_SHIFT);
            }
        }

        private void Special(ushort vk, ushort sc, bool extended, bool shift)
        {
            if (Mode == InputMode.Unicode)
            {
                if (shift) SendVk(VK_SHIFT, false);
                SendVk(vk, false);
                Thread.Sleep(_rnd.Next(8, 28));
                SendVk(vk, true);
                if (shift) SendVk(VK_SHIFT, true);
                return;
            }
            KeyTap(sc, extended, shift);
        }

        // ------------------------------------------------------------------ low level

        private void Key(ushort sc, bool extended, bool up, ushort vkForUnicodeMode)
        {
            MarkSelf();
            if (Mode == InputMode.Driver)
            {
                Interception.Send(sc, extended, up);
                return;
            }
            if (Mode == InputMode.Unicode && vkForUnicodeMode != 0)
            {
                SendVk(vkForUnicodeMode, up);
                return;
            }
            var inp = new INPUT { type = INPUT_KEYBOARD };
            uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            inp.U.ki = new KEYBDINPUT { wVk = 0, wScan = sc, dwFlags = flags, dwExtraInfo = InjectMarker };
            SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        private void UnicodeChar(string unit)
        {
            MarkSelf();
            var downs = new List<INPUT>();
            var ups = new List<INPUT>();
            foreach (char c in unit)
            {
                downs.Add(UnicodeInput(c, false));
                ups.Add(UnicodeInput(c, true));
            }
            SendInput((uint)downs.Count, downs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(_rnd.Next(8, 28));
            SendInput((uint)ups.Count, ups.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT UnicodeInput(char c, bool up)
        {
            var inp = new INPUT { type = INPUT_KEYBOARD };
            inp.U.ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = InjectMarker };
            return inp;
        }

        private static void SendVk(ushort vk, bool up)
        {
            MarkSelf();
            var inp = new INPUT { type = INPUT_KEYBOARD };
            bool ext = (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;
            inp.U.ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (ext ? KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = InjectMarker
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        // ------------------------------------------------------------------ character → key

        /// <summary>
        /// Maps a character to the physical key (scan code + Shift) that produces it on a
        /// US-style layout. Remote machines almost always use this layout for typing pinyin.
        /// </summary>
        private static bool TryCharToKey(string unit, out ushort sc, out bool shift)
        {
            sc = 0; shift = false;
            if (unit == null || unit.Length != 1) return false;
            char c = unit[0];
            if (c >= 0x80) return false;
            int idx = UsUnshifted.IndexOf(c);
            if (idx >= 0) { sc = UsScan[idx]; shift = false; }
            else
            {
                idx = UsShifted.IndexOf(c);
                if (idx < 0) return false;
                sc = UsScan[idx]; shift = true;
            }
            // Caps Lock flips letters.
            if (char.IsLetter(c) && (GetKeyState(0x14) & 1) != 0) shift = !shift;
            return true;
        }

        // Standard US keyboard: the character each key gives without / with Shift, and its scan code.
        private const string UsUnshifted = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./ ";
        private const string UsShifted   = "~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>? ";
        private static readonly ushort[] UsScan =
        {
            0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x2B,
            0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35,
            0x39,
        };

        // ------------------------------------------------------------------ Interception driver

        /// <summary>
        /// Minimal binding to the Interception keyboard driver (https://github.com/oblitum/Interception).
        /// interception.dll must sit next to HumanTyper.exe and the driver must be installed.
        /// </summary>
        internal static class Interception
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct KeyStroke { public ushort code; public ushort state; public uint information; }

            private const ushort KEY_DOWN = 0x00, KEY_UP = 0x01, KEY_E0 = 0x02;

            [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr interception_create_context();
            [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int interception_send(IntPtr context, int device, ref KeyStroke stroke, uint nstroke);
            [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern int interception_is_keyboard(int device);
            [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern uint interception_get_hardware_id(IntPtr context, int device, IntPtr buffer, uint size);

            private static IntPtr _ctx;
            private static int _device = 1;
            public static string LastError { get; private set; } = "";

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibrary(string path);

            private static string ExeDirDll =>
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "interception.dll");

            /// <summary>The dll is either next to the exe or where the one-click installer put it.</summary>
            public static bool DllPresent => System.IO.File.Exists(ExeDirDll) || System.IO.File.Exists(DriverSetup.DllPath);

            private static bool _loaded;

            /// <summary>True when the dll loads and at least one keyboard is attached through the driver.</summary>
            public static bool Open()
            {
                if (_ctx != IntPtr.Zero) return true;
                try
                {
                    if (!DllPresent)
                    {
                        LastError = DriverSetup.DriverInstalled ? "驱动已安装，但缺少 interception.dll，请点“一键安装驱动”修复"
                                                                : "尚未安装键盘驱动，点“一键安装驱动”即可";
                        return false;
                    }
                    if (!_loaded)
                    {
                        // Load it by full path so the DllImports below bind to it.
                        string path = System.IO.File.Exists(ExeDirDll) ? ExeDirDll : DriverSetup.DllPath;
                        if (LoadLibrary(path) == IntPtr.Zero) { LastError = "无法加载 interception.dll"; return false; }
                        _loaded = true;
                    }
                    _ctx = interception_create_context();
                    if (_ctx == IntPtr.Zero)
                    {
                        LastError = DriverSetup.DriverInstalled ? "驱动已安装，重启电脑后生效" : "尚未安装键盘驱动，点“一键安装驱动”即可";
                        return false;
                    }
                    // pick the first keyboard device that the driver actually sees
                    IntPtr buf = Marshal.AllocHGlobal(512);
                    try
                    {
                        for (int d = 1; d <= 10; d++)
                        {
                            if (interception_is_keyboard(d) == 0) continue;
                            if (interception_get_hardware_id(_ctx, d, buf, 512) > 0) { _device = d; return true; }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    _device = 1;
                    return true;
                }
                catch (DllNotFoundException) { LastError = "无法加载 interception.dll（请确认是 x64 版本）"; }
                catch (BadImageFormatException) { LastError = "interception.dll 位数不匹配（64 位系统请用 x64 版本）"; }
                catch (EntryPointNotFoundException) { LastError = "interception.dll 版本不正确"; }
                _ctx = IntPtr.Zero;
                return false;
            }

            public static bool IsAvailable() => Open();

            public static void Send(ushort sc, bool extended, bool up)
            {
                var s = new KeyStroke { code = sc, state = (ushort)((up ? KEY_UP : KEY_DOWN) | (extended ? KEY_E0 : 0)) };
                interception_send(_ctx, _device, ref s, 1);
            }
        }
    }
}
