using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HumanTyper
{
    /// <summary>
    /// Clipboard access from the (MTA) typing thread: every call runs on a short-lived STA thread.
    /// Used by the proofreading fallback, which copies the text before the caret to read it back.
    /// </summary>
    internal static class ClipboardText
    {
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        public static uint Sequence => GetClipboardSequenceNumber();

        /// <summary>A copy of everything on the clipboard that can be put back later.</summary>
        public static DataObject Save()
        {
            return Sta(() =>
            {
                var src = Retry(() => Clipboard.GetDataObject());
                if (src == null) return null;
                var copy = new DataObject();
                int n = 0;
                foreach (var f in src.GetFormats(false))
                {
                    try
                    {
                        object d = src.GetData(f, false);
                        if (d != null) { copy.SetData(f, false, d); n++; }
                    }
                    catch { }
                }
                return n > 0 ? copy : null;
            });
        }

        public static void Restore(DataObject saved)
        {
            Sta(() =>
            {
                Retry(() =>
                {
                    if (saved != null) Clipboard.SetDataObject(saved, true);
                    else Clipboard.Clear();
                    return true;
                });
                return true;
            });
        }

        public static string GetText()
        {
            return Sta(() => Retry(() => Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText) : null));
        }

        private static T Retry<T>(Func<T> f)
        {
            for (int i = 0; ; i++)
            {
                try { return f(); }
                catch (ExternalException) when (i < 10) { Thread.Sleep(30); }   // clipboard busy
                catch { return default(T); }
            }
        }

        private static T Sta<T>(Func<T> f)
        {
            T result = default(T);
            var t = new Thread(() => { try { result = f(); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(3000);
            return result;
        }
    }
}
