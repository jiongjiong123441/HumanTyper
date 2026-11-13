using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HumanTyper
{
    /// <summary>
    /// A small always-on-top panel that shows how far typing has got: state, a big percentage,
    /// a thick progress bar and a "teleprompter" line with the character being typed right now
    /// highlighted between what's done and what's next. It never takes focus (clicking or
    /// dragging it doesn't pull the keyboard away from the target window).
    /// </summary>
    internal sealed class ProgressOverlay : Form
    {
        private List<string> _units = new List<string>();
        private int _done, _total;
        private EngineState _state = EngineState.Running;
        private string _msg = "";
        private string _stats = "";
        private readonly float _k;                       // DPI scale
        private readonly Font _fState, _fPct, _fStrip, _fCur, _fSmall;
        private Rectangle _closeRect;

        /// <summary>Raised when the user closes the panel with its × button.</summary>
        public event Action ClosedByUser;
        /// <summary>Raised after the user dragged the panel somewhere else.</summary>
        public event Action<Point> Moved;

        public ProgressOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 30, 36);
            Opacity = 0.94;
            Text = "HumanTyper 进度";

            using (var g = CreateGraphics()) _k = g.DpiX / 96f;
            ClientSize = new Size((int)(620 * _k), (int)(118 * _k));
            _fState = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _fPct = new Font("Segoe UI", 20f, FontStyle.Bold);
            _fStrip = new Font("Microsoft YaHei UI", 14f);
            _fCur = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            _fSmall = new Font("Microsoft YaHei UI", 8.5f);

            var rgn = RoundRect(new Rectangle(Point.Empty, ClientSize), (int)(10 * _k));
            Region = new Region(rgn);
        }

        // never activate: the target window must keep the keyboard
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Keep the panel out of screenshots (Windows 10 2004+), so the screenshot proofreading
            // never reads our copy of the text instead of the target's.
            try { NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------------ data

        public void SetText(List<string> units)
        {
            _units = units ?? new List<string>();
            _total = _units.Count;
            _done = 0;
            Invalidate();
        }

        public void SetProgress(int done, int total, string stats)
        {
            _done = done; _total = total; _stats = stats ?? "";
            Invalidate();
        }

        public void SetState(EngineState st, string msg)
        {
            _state = st; _msg = msg ?? "";
            Invalidate();
        }

        // ------------------------------------------------------------------ dragging / closing

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (_closeRect.Contains(e.Location)) { Hide(); ClosedByUser?.Invoke(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            Moved?.Invoke(Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = _closeRect.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
        }

        // ------------------------------------------------------------------ painting

        private Color Accent =>
            _state == EngineState.Paused ? Color.FromArgb(255, 183, 77) :
            _state == EngineState.Finished ? Color.FromArgb(100, 181, 246) :
            _state == EngineState.Stopped ? Color.FromArgb(239, 83, 80) :
            Color.FromArgb(102, 210, 120);

        private string StateText =>
            _state == EngineState.Paused ? "已暂停" :
            _state == EngineState.Thinking ? (_done >= _total && _total > 0 ? "核对中" : "思考中") :
            _state == EngineState.Finished ? "完成" :
            _state == EngineState.Stopped ? "已停止" : "正在输入";

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int W = ClientSize.Width, pad = (int)(14 * _k);
            Color dim = Color.FromArgb(120, 126, 138), text = Color.FromArgb(235, 238, 243);
            double frac = _total > 0 ? Math.Min(1.0, (double)_done / _total) : 0;

            // accent stripe on the left
            using (var b = new SolidBrush(Accent)) g.FillRectangle(b, 0, 0, (int)(5 * _k), ClientSize.Height);

            // row 1: state + message, percentage on the right
            string pct = $"{frac * 100:0}%";
            var pctSize = TextRenderer.MeasureText(g, pct, _fPct, Size.Empty, TextFormatFlags.NoPadding);
            int closeW = (int)(22 * _k);
            _closeRect = new Rectangle(W - closeW - (int)(4 * _k), (int)(4 * _k), closeW, closeW);
            int pctX = _closeRect.Left - (int)(4 * _k) - pctSize.Width;
            TextRenderer.DrawText(g, pct, _fPct, new Point(pctX, (int)(6 * _k)), Accent, TextFormatFlags.NoPadding);

            int y1 = (int)(10 * _k);
            int dot = (int)(10 * _k);
            using (var b = new SolidBrush(Accent)) g.FillEllipse(b, pad, y1 + (int)(4 * _k), dot, dot);
            string st = StateText;
            int sx = pad + dot + (int)(6 * _k);
            TextRenderer.DrawText(g, st, _fState, new Point(sx, y1), text, TextFormatFlags.NoPadding);
            int stW = TextRenderer.MeasureText(g, st, _fState, Size.Empty, TextFormatFlags.NoPadding).Width;
            string msg = ShortMessage(_msg);
            if (msg.Length > 0)
                TextRenderer.DrawText(g, msg, _fSmall, new Rectangle(sx + stW + (int)(8 * _k), y1 + (int)(2 * _k), pctX - sx - stW - (int)(16 * _k), (int)(20 * _k)),
                    dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            // × button
            using (var p = new Pen(dim, 1.6f * _k))
            {
                int m = (int)(7 * _k);
                g.DrawLine(p, _closeRect.Left + m, _closeRect.Top + m, _closeRect.Right - m, _closeRect.Bottom - m);
                g.DrawLine(p, _closeRect.Right - m, _closeRect.Top + m, _closeRect.Left + m, _closeRect.Bottom - m);
            }

            // row 2: thick progress bar
            int barY = (int)(42 * _k), barH = (int)(10 * _k);
            var track = new Rectangle(pad, barY, W - 2 * pad, barH);
            using (var path = RoundRect(track, barH / 2))
            using (var b = new SolidBrush(Color.FromArgb(55, 59, 68))) g.FillPath(b, path);
            int fillW = (int)Math.Round(track.Width * frac);
            if (fillW > 0)
            {
                var fill = new Rectangle(track.X, track.Y, Math.Max(barH, fillW), barH);
                using (var path = RoundRect(fill, barH / 2))
                using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
            }

            // row 3: teleprompter strip — done text (dim) | current char (highlighted) | next text
            int stripY = (int)(60 * _k), stripH = (int)(32 * _k);
            var strip = new Rectangle(pad, stripY, W - 2 * pad, stripH);
            DrawStrip(g, strip, dim, text);

            // row 4: numbers
            string counts = $"{Math.Min(_done, _total)} / {_total} 字符" + (_stats.Length > 0 ? "  ·  " + _stats : "");
            TextRenderer.DrawText(g, counts, _fSmall, new Point(pad, (int)(96 * _k)), dim, TextFormatFlags.NoPadding);
        }

        private void DrawStrip(Graphics g, Rectangle r, Color dim, Color text)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var state = g.Save();
            g.SetClip(r);
            int cur = Math.Min(_done, _units.Count);
            int midX = r.X + (int)(r.Width * 0.38);
            int baseY = r.Y + (int)(3 * _k);

            // current character in a box
            string curText = cur < _units.Count ? Show(_units[cur]) : (_total > 0 ? "✓" : "");
            int curW = 0;
            if (curText.Length > 0)
            {
                var cs = TextRenderer.MeasureText(g, curText, _fCur, Size.Empty, flags);
                curW = cs.Width + (int)(8 * _k);
                var box = new Rectangle(midX, r.Y, curW, r.Height);
                using (var path = RoundRect(box, (int)(5 * _k)))
                using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
                TextRenderer.DrawText(g, curText, _fCur, new Point(midX + (int)(4 * _k), baseY), Color.FromArgb(20, 22, 26), flags);
            }

            // what's done: right-aligned up to the box
            var before = new System.Text.StringBuilder();
            for (int i = cur - 1; i >= 0 && before.Length < 60; i--) before.Insert(0, Show(_units[i]));
            string bs = before.ToString();
            int bw = TextRenderer.MeasureText(g, bs, _fStrip, Size.Empty, flags).Width;
            TextRenderer.DrawText(g, bs, _fStrip, new Point(midX - (int)(2 * _k) - bw, baseY), dim, flags);

            // what's next
            var after = new System.Text.StringBuilder();
            for (int i = cur + 1; i < _units.Count && after.Length < 90; i++) after.Append(Show(_units[i]));
            TextRenderer.DrawText(g, after.ToString(), _fStrip, new Point(midX + curW + (int)(2 * _k), baseY), text, flags);

            // fade the left edge so the done text trails off
            var fadeR = new Rectangle(r.X, r.Y, (int)(60 * _k), r.Height);
            using (var lg = new LinearGradientBrush(fadeR, BackColor, Color.FromArgb(0, BackColor), LinearGradientMode.Horizontal))
                g.FillRectangle(lg, fadeR);
            g.Restore(state);
        }

        private static string Show(string u) =>
            u == "\n" ? "↵" : u == "\t" ? "→" : u == " " ? " " : u;

        /// <summary>Drops the leading state word from engine messages ("已暂停 — 按 F8…" → "按 F8…").</summary>
        private static string ShortMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return "";
            int dash = msg.IndexOf(" — ", StringComparison.Ordinal);
            if (dash > 0 && dash < 12) msg = msg.Substring(dash + 3);
            if (msg == "正在输入…" || msg == "继续输入…") return "";
            return msg;
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fState.Dispose(); _fPct.Dispose(); _fStrip.Dispose(); _fCur.Dispose(); _fSmall.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
