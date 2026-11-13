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
            BackColor = Bg;
            Opacity = 0.94;
            Text = "HumanTyper 进度";

            using (var g = CreateGraphics()) _k = g.DpiX / 96f;
            ClientSize = new Size((int)(620 * _k), (int)(128 * _k));
            _fState = new Font("Microsoft YaHei UI", 10f);
            _fPct = new Font("Microsoft YaHei UI", 13f);
            _fStrip = new Font("Microsoft YaHei UI", 14f);
            _fCur = _fStrip;
            _fSmall = new Font("Microsoft YaHei UI", 8.5f);

            var rgn = RoundRect(new Rectangle(Point.Empty, ClientSize), (int)(12 * _k));
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
        //
        // Layout on a 4px grid (logical pixels, scaled by _k):
        //   12–32  state · message …………………… percentage  ×
        //   40–46  progress bar
        //   52–92  teleprompter line, current character at a fixed reading point
        //   100–116 counts · speed …………………… HumanTyper · 永久免费
        // Colour: one graphite ramp; the state colour appears only on the dot, the bar and
        // the current-character block.

        private static readonly Color Bg = Color.FromArgb(27, 28, 32);
        private static readonly Color Track = Color.FromArgb(45, 47, 53);
        private static readonly Color Hairline = Color.FromArgb(58, 60, 67);
        private static readonly Color TextPrimary = Color.FromArgb(231, 233, 236);
        private static readonly Color TextSecondary = Color.FromArgb(163, 167, 175);
        private static readonly Color TextMuted = Color.FromArgb(111, 115, 123);
        private static readonly Color OnAccent = Color.FromArgb(18, 19, 22);

        private Color Accent =>
            _state == EngineState.Paused ? Color.FromArgb(240, 178, 72) :
            _state == EngineState.Finished ? Color.FromArgb(98, 166, 232) :
            _state == EngineState.Stopped ? Color.FromArgb(229, 98, 92) :
            Color.FromArgb(88, 196, 120);

        private string StateText =>
            _state == EngineState.Paused ? "已暂停" :
            _state == EngineState.Thinking ? (_done >= _total && _total > 0 ? "核对中" : "思考中") :
            _state == EngineState.Finished ? "完成" :
            _state == EngineState.Stopped ? "已停止" : "正在输入";

        private int S(double v) => (int)Math.Round(v * _k);

        private const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int W = ClientSize.Width, H = ClientSize.Height, pad = S(16);
            double frac = _total > 0 ? Math.Min(1.0, (double)_done / _total) : 0;

            // hairline edge instead of a coloured stripe
            using (var path = RoundRect(new Rectangle(0, 0, W - 1, H - 1), S(12)))
            using (var p = new Pen(Hairline, 1f)) g.DrawPath(p, path);

            // ---- row 1: dot · state · message …… percentage ×
            var row1 = new Rectangle(pad, S(12), W - 2 * pad, S(20));
            _closeRect = new Rectangle(W - S(12) - S(24), S(10), S(24), S(24));
            using (var b = new SolidBrush(Accent))
                g.FillEllipse(b, row1.X, row1.Y + (row1.Height - S(8)) / 2, S(8), S(8));
            int x = row1.X + S(16);
            string st = StateText;
            int stW = TextRenderer.MeasureText(g, st, _fState, Size.Empty, Line).Width;
            TextRenderer.DrawText(g, st, _fState, new Rectangle(x, row1.Y, stW + S(4), row1.Height), TextPrimary, Line);

            string pct = $"{frac * 100:0}%";
            int pctW = TextRenderer.MeasureText(g, pct, _fPct, Size.Empty, Line).Width;
            int pctX = _closeRect.Left - S(8) - pctW;
            TextRenderer.DrawText(g, pct, _fPct, new Rectangle(pctX, row1.Y - S(2), pctW + S(4), row1.Height + S(4)), TextPrimary, Line);

            string msg = ShortMessage(_msg);
            int msgX = x + stW + S(8);
            if (msg.Length > 0 && pctX - S(12) > msgX)
                TextRenderer.DrawText(g, msg, _fSmall, new Rectangle(msgX, row1.Y, pctX - S(12) - msgX, row1.Height),
                    TextSecondary, Line | TextFormatFlags.EndEllipsis);

            using (var p = new Pen(TextMuted, 1.5f * _k))
            {
                int m = S(8);
                g.DrawLine(p, _closeRect.Left + m, _closeRect.Top + m, _closeRect.Right - m, _closeRect.Bottom - m);
                g.DrawLine(p, _closeRect.Right - m, _closeRect.Top + m, _closeRect.Left + m, _closeRect.Bottom - m);
            }

            // ---- row 2: progress bar
            var track = new Rectangle(pad, S(40), W - 2 * pad, S(6));
            using (var path = RoundRect(track, track.Height / 2))
            using (var b = new SolidBrush(Track)) g.FillPath(b, path);
            int fillW = (int)Math.Round(track.Width * frac);
            if (fillW > 0)
            {
                using (var path = RoundRect(new Rectangle(track.X, track.Y, Math.Max(track.Height, fillW), track.Height), track.Height / 2))
                using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
            }

            // ---- row 3: teleprompter line
            DrawStrip(g, new Rectangle(pad, S(52), W - 2 * pad, S(40)));

            // ---- row 4: numbers …… brand
            var row4 = new Rectangle(pad, S(100), W - 2 * pad, S(16));
            string brand = "HumanTyper · 永久免费";
            int brandW = TextRenderer.MeasureText(g, brand, _fSmall, Size.Empty, Line).Width;
            TextRenderer.DrawText(g, brand, _fSmall, new Rectangle(row4.Right - brandW, row4.Y, brandW + S(2), row4.Height), TextMuted, Line);
            string counts = $"{Math.Min(_done, _total)} / {_total} 字符" + (_stats.Length > 0 ? "  ·  " + _stats : "");
            TextRenderer.DrawText(g, counts, _fSmall, new Rectangle(row4.X, row4.Y, row4.Width - brandW - S(16), row4.Height),
                TextSecondary, Line | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// Done text (muted) runs up to a fixed reading point, the character being typed sits in
        /// the accent block there, the upcoming text follows in primary. Both ends fade out.
        /// </summary>
        private void DrawStrip(Graphics g, Rectangle r)
        {
            var state = g.Save();
            g.SetClip(r);
            int cur = Math.Min(_done, _units.Count);
            int readX = r.X + (int)(r.Width * 0.36);

            // one font for all three parts, so they share a baseline
            // text is centred in the full strip height, so descenders and CJK glyphs never clip
            var textBand = r;

            string curText = cur < _units.Count ? Show(_units[cur]) : (_total > 0 ? "✓" : "");
            int boxW = 0;
            if (curText.Length > 0)
            {
                int glyphW = TextRenderer.MeasureText(g, curText, _fStrip, Size.Empty, Line).Width;
                boxW = Math.Max(S(20), glyphW + S(8));
                var box = new Rectangle(readX, r.Y + S(2), boxW, r.Height - S(4));
                using (var path = RoundRect(box, S(6)))
                using (var b = new SolidBrush(Accent)) g.FillPath(b, path);
                TextRenderer.DrawText(g, curText, _fStrip, new Rectangle(box.X, textBand.Y, box.Width, textBand.Height),
                    OnAccent, Line | TextFormatFlags.HorizontalCenter);
            }

            var before = new System.Text.StringBuilder();
            for (int i = cur - 1; i >= 0 && before.Length < 60; i--) before.Insert(0, Show(_units[i]));
            string bs = before.ToString();
            // right-aligned inside the strip: GDI text clips to its rectangle, not to g.Clip
            TextRenderer.DrawText(g, bs, _fStrip, new Rectangle(r.X, textBand.Y, Math.Max(0, readX - S(2) - r.X), textBand.Height), TextMuted, Line | TextFormatFlags.Right);

            var after = new System.Text.StringBuilder();
            for (int i = cur + 1; i < _units.Count && after.Length < 90; i++) after.Append(Show(_units[i]));
            int ax = readX + boxW + S(2);
            TextRenderer.DrawText(g, after.ToString(), _fStrip, new Rectangle(ax, textBand.Y, Math.Max(0, r.Right - ax), textBand.Height), TextPrimary, Line);

            // fade both ends into the panel
            int fw = S(40);
            var left = new Rectangle(r.X - 1, r.Y, fw, r.Height);
            using (var lg = new LinearGradientBrush(left, Bg, Color.FromArgb(0, Bg), LinearGradientMode.Horizontal)) g.FillRectangle(lg, left);
            var right = new Rectangle(r.Right - fw + 1, r.Y, fw, r.Height);
            using (var lg = new LinearGradientBrush(right, Color.FromArgb(0, Bg), Bg, LinearGradientMode.Horizontal)) g.FillRectangle(lg, right);
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
            if (disposing) { _fState.Dispose(); _fPct.Dispose(); _fStrip.Dispose(); _fSmall.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
