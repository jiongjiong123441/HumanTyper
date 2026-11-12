using System;
using System.Runtime.InteropServices;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    /// <summary>Reads the focused control and caret position of a foreground window.</summary>
    internal static class CaretProbe
    {
        private static Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        public struct Snapshot
        {
            public IntPtr Focus;
            public bool CaretOk;
            public int X, Y, H;

            public bool Near(Snapshot other, int tol = 3)
            {
                return CaretOk && other.CaretOk &&
                       Math.Abs(X - other.X) <= tol && Math.Abs(Y - other.Y) <= tol;
            }

            public override string ToString() => CaretOk ? $"({X},{Y})" : "(未知)";
        }

        public static Snapshot Read(IntPtr fg)
        {
            var s = new Snapshot();
            if (fg == IntPtr.Zero) return s;
            uint tid = GetWindowThreadProcessId(fg, out _);
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
            if (!GetGUIThreadInfo(tid, ref gti)) return s;
            s.Focus = gti.hwndFocus;

            // 1) Classic Win32 caret
            if (gti.hwndCaret != IntPtr.Zero &&
                (gti.rcCaret.Bottom - gti.rcCaret.Top > 0 || gti.rcCaret.Left != 0 || gti.rcCaret.Top != 0))
            {
                var p = new POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Top };
                if (ClientToScreen(gti.hwndCaret, ref p))
                {
                    s.CaretOk = true; s.X = p.X; s.Y = p.Y; s.H = gti.rcCaret.Bottom - gti.rcCaret.Top;
                    return s;
                }
            }

            // 2) MSAA caret object (Chrome / Edge / Firefox / many modern apps)
            IntPtr target = s.Focus != IntPtr.Zero ? s.Focus : fg;
            object obj = null;
            try
            {
                if (AccessibleObjectFromWindow(target, OBJID_CARET, ref IID_IAccessible, out obj) == 0 &&
                    obj is Accessibility.IAccessible acc)
                {
                    acc.accLocation(out int l, out int t, out int w, out int h, 0);
                    if (w > 0 || h > 0 || l != 0 || t != 0)
                    {
                        s.CaretOk = true; s.X = l; s.Y = t; s.H = h;
                    }
                }
            }
            catch
            {
                // caret not exposed
            }
            finally
            {
                if (obj != null && Marshal.IsComObject(obj))
                {
                    try { Marshal.ReleaseComObject(obj); } catch { }
                }
            }
            return s;
        }
    }
}
