using System;
using System.Runtime.InteropServices;

namespace HumanTyper
{
    /// <summary>Progress shown on the program's taskbar button (green, yellow when paused).</summary>
    internal static class TaskbarProgress
    {
        public enum State { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, State state);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarInstance { }

        private static ITaskbarList3 _tb;
        private static bool _failed;

        private static ITaskbarList3 Tb
        {
            get
            {
                if (_tb != null || _failed) return _tb;
                try { _tb = (ITaskbarList3)new TaskbarInstance(); _tb.HrInit(); }
                catch { _failed = true; _tb = null; }
                return _tb;
            }
        }

        public static void Set(IntPtr hwnd, State state, int done = 0, int total = 0)
        {
            try
            {
                var tb = Tb;
                if (tb == null || hwnd == IntPtr.Zero) return;
                tb.SetProgressState(hwnd, state);
                if (state == State.Normal || state == State.Paused || state == State.Error)
                    tb.SetProgressValue(hwnd, (ulong)Math.Max(0, done), (ulong)Math.Max(1, total));
            }
            catch { }
        }
    }
}
