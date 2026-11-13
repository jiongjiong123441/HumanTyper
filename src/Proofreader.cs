using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    /// <summary>
    /// After typing: read back what actually landed in the target (text before the caret, via
    /// UI Automation), diff it against the source text, and fix every difference the way a person
    /// would — arrow keys to walk back, Backspace to delete, then type the right characters.
    /// </summary>
    internal sealed partial class TypingEngine
    {
        internal sealed class Hunk
        {
            public int AStart, AEnd;                 // range in the document window to delete
            public int BStart;                       // where Insert starts in the wanted text
            public List<string> Insert = new List<string>();
        }

        private int _priorLen = -1;                  // units of text before the caret when typing started
        private List<string> _priorTail;             // the last few of them, to check they're unchanged

        /// <summary>Remembers what was before the caret when typing started.</summary>
        private void RememberPrior()
        {
            _priorLen = -1; _priorTail = null;
            if (!_s.Proofread || !TryReadTextBeforeCaret(out string before)) return;
            var p = Split(Normalize(before));
            _priorLen = p.Count;
            int t = Math.Min(30, p.Count);
            _priorTail = p.GetRange(p.Count - t, t);
        }

        private bool PriorStillThere(List<string> doc)
        {
            if (_priorLen < 0 || _priorTail == null || doc.Count < _priorLen) return false;
            int t = _priorTail.Count;
            for (int i = 0; i < t; i++)
                if (doc[_priorLen - t + i] != _priorTail[i]) return false;
            return true;
        }

        private enum ReadVia { Uia, Clipboard, Ocr }

        private void Proofread()
        {
            var want = new List<string>(_u.Count);
            foreach (var x in _u) want.Add(NormUnit(x));

            int totalFixed = 0, skippedCn = 0, uncertain = 0;
            ReadVia? via = null;           // once a fallback was needed, keep using it
            for (int pass = 1; pass <= 2; pass++)
            {
                Report(EngineState.Thinking, pass == 1 ? "正在从头核对一遍…" : "再核对一遍…");
                Wait(Rand(200, 450));
                Gate();
                SleepPlain(120);   // let the target finish processing our keystrokes

                bool read = false;
                List<string> w = null;
                List<Hunk> hunks = null;
                int skipped = 0;
                ReadVia used = ReadVia.Uia;

                // 1) UI Automation: reads the text without touching anything.
                if ((via == null || via == ReadVia.Uia) && TryReadTextBeforeCaret(out string uia) && uia.Length > 0)
                {
                    read = true;
                    hunks = Analyze(uia, want, true, out w, out skipped);
                }
                // 2) Clipboard: select back to the start, copy, put the user's clipboard back.
                //    Also tried when UIA returned something that doesn't look like our text.
                if ((hunks == null || TooManyChanges(hunks, want)) && via != ReadVia.Ocr && _s.ClipboardProofread &&
                    TryReadByClipboard(out string clip) && clip.Length > 0)
                {
                    var h2 = Analyze(clip, want, false, out var w2, out int sk2);
                    if (h2 != null || !read)
                    {
                        read = true; used = ReadVia.Clipboard;
                        hunks = h2; w = w2; skipped = sk2;
                    }
                }
                // 3) Screenshot + OCR (English words only, both readings must agree).
                if ((!read || hunks == null) && via != ReadVia.Clipboard && _s.OcrProofread && TryProofreadByOcr(want, out var w3, out var h3, out int unc))
                {
                    read = true; used = ReadVia.Ocr;
                    hunks = h3; w = w3;
                    uncertain = unc;
                }
                if (read && via == null) via = used;
                string how = via == ReadVia.Clipboard ? "（剪贴板方式）" : via == ReadVia.Ocr ? "（截图识别，仅英文）" : "";

                if (!read)
                {
                    _proofMsg = totalFixed > 0 ? $"已修正 {totalFixed} 处（之后无法再读取目标文本）"
                                               : "目标程序读不到文本，也无法复制或识别，已跳过核对";
                    return;
                }
                if (hunks == null)
                {
                    _proofMsg = "与原文差异过大（光标可能不在文末），已跳过自动修正" + how;
                    return;
                }
                if (pass == 1) skippedCn = skipped;
                string notes = (skippedCn > 0 ? $"（{skippedCn} 处中文选词差异未处理）" : "") +
                               (uncertain > 0 ? $"（{uncertain} 处疑似差异无法确认，未改动）" : "");
                if (hunks.Count == 0)
                {
                    _proofMsg = (totalFixed > 0 ? $"核对完成，已修正 {totalFixed} 处" : "核对完成，没有发现错误") + how + notes;
                    return;
                }
                if (TooManyChanges(hunks, want))
                {
                    _proofMsg = $"发现 {Changed(hunks)} 处差异，差异过大，为避免误改已跳过自动修正" + how;
                    return;
                }

                Report(EngineState.Running, $"发现 {hunks.Count} 处错误，正在修改…");
                // Missing Chinese gets retyped as a whole word so the IME has context.
                if (_kb.IsKeyMode) ExpandToWords(hunks, w, want);
                _fixing = true;
                try { ApplyHunks(hunks, w.Count); } finally { _fixing = false; }
                totalFixed += hunks.Count;
            }
            _proofMsg = $"已修正 {totalFixed} 处" +
                        (via == ReadVia.Clipboard ? "（剪贴板方式）" : via == ReadVia.Ocr ? "（截图识别，仅英文）" : "") +
                        (skippedCn > 0 ? $"（{skippedCn} 处中文选词差异未处理）" : "") +
                        (uncertain > 0 ? $"（{uncertain} 处疑似差异无法确认，未改动）" : "");
        }

        /// <summary>
        /// Diffs the text before the caret against the wanted text. Returns null if they differ
        /// too much to align; in keyboard mode Chinese-for-Chinese swaps (IME homophones) are dropped.
        /// </summary>
        private List<Hunk> Analyze(string before, List<string> want, bool allowExact, out List<string> w, out int skippedCn)
        {
            skippedCn = 0;
            var doc = Split(Normalize(before));
            int slack = 20 + want.Count / 10;
            bool exact = allowExact && PriorStillThere(doc);
            if (exact)
            {
                // We know exactly where our text starts: compare just that stretch.
                w = doc.GetRange(_priorLen, doc.Count - _priorLen);
            }
            else
            {
                int wLen = Math.Min(doc.Count, want.Count + slack);
                w = doc.GetRange(doc.Count - wLen, wLen);
            }

            var hunks = DiffHunks(w, want, slack + 40 + want.Count / 5, exact);
            if (hunks == null) return null;
            if (_kb.IsKeyMode)
            {
                // Typing through an IME: a Chinese character swapped for another one is just the
                // IME picking a homophone. Let it be — only fix missing/extra text and non-Chinese.
                var ww = w;
                int n0 = hunks.Count;
                hunks.RemoveAll(h => h.Insert.Count > 0 && h.AEnd - h.AStart == h.Insert.Count &&
                                     h.Insert.TrueForAll(IsCjk) && ww.GetRange(h.AStart, h.AEnd - h.AStart).TrueForAll(IsCjk));
                skippedCn = n0 - hunks.Count;
            }
            return hunks;
        }

        private static int Changed(List<Hunk> hunks)
        {
            int changed = 0;
            foreach (var h in hunks) changed += Math.Max(h.AEnd - h.AStart, h.Insert.Count);
            return changed;
        }

        private static bool TooManyChanges(List<Hunk> hunks, List<string> want) =>
            Changed(hunks) > Math.Max(8, want.Count / 4);

        /// <summary>
        /// Reads the text before the caret the way a person would check it: Ctrl+Shift+Home to
        /// select back to the start, Ctrl+C, then → to drop the selection with the caret back at
        /// the end. The user's clipboard is saved first and put back afterwards. Works in any app
        /// that supports copying — including remote windows when clipboard sync is on.
        /// </summary>
        private bool TryReadByClipboard(out string before)
        {
            before = null;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg) || IsTerminal(fg)) return false;   // Ctrl+C would interrupt a console

            Report(EngineState.Thinking, "读不到文本，改用剪贴板读取已输入的内容…");
            var saved = ClipboardText.Save();
            uint seq0 = ClipboardText.Sequence;
            bool remote = _kb.IsKeyMode;
            string text = null;
            try
            {
                _kb.SelectToStart();
                _lastKeyMs = Now;
                SleepPlain(remote ? 250 : 90);
                _kb.Copy();
                _lastKeyMs = Now;
                long until = Now + (remote ? 3000 : 1500);    // remote clipboard sync takes a moment
                while (Now < until && ClipboardText.Sequence == seq0) SleepPlain(25);
                if (ClipboardText.Sequence != seq0)
                {
                    SleepPlain(remote ? 150 : 50);           // the owner may still be adding formats
                    text = ClipboardText.GetText();
                }
            }
            finally
            {
                // → collapses the selection to its end, which is where the caret was.
                _kb.Right();
                _lastKeyMs = Now;
                Thread.Sleep(remote ? 200 : 60);
                ClipboardText.Restore(saved);
            }
            before = text;
            return text != null;
        }

        private static bool IsTerminal(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(128);
            GetClassName(hwnd, sb, 128);
            string c = sb.ToString();
            return c == "ConsoleWindowClass" || c == "CASCADIA_HOSTING_WINDOW_CLASS" || c == "mintty" ||
                   c == "VirtualConsoleClass" || c == "PuTTY";
        }

        /// <summary>
        /// Screenshot check: OCR the target window twice at different zoom levels and fix only the
        /// English words both readings agree are wrong. Returns false if our text can't be found.
        /// </summary>
        private bool TryProofreadByOcr(List<string> want, out List<string> w, out List<Hunk> hunks, out int uncertain)
        {
            w = null; hunks = null; uncertain = 0;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg) || !ScreenOcr.Available) return false;
            if (!GetWindowRect(fg, out RECT r)) return false;
            var area = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            area.Intersect(System.Windows.Forms.SystemInformation.VirtualScreen);
            // Only what's above the caret line: our text ends there, anything below is other UI.
            var c = _baseline;
            if (_caretTracks && c.CaretOk && c.Y > area.Top && c.Y < area.Bottom)
                area.Height = Math.Min(area.Height, c.Y + Math.Max(c.H, 16) + 4 - area.Top);

            // Something of ours on top of the target (our window shows the source text!)?
            // Then render the target window by itself instead of grabbing the screen.
            bool covered = OwnWindowOverlaps(System.Drawing.Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom));

            Report(EngineState.Thinking, "读不到文本，改用截图识别核对…");
            var wantWords = new List<string>();
            var spans = OcrMatch.WordSpans(want);
            foreach (var (s, e) in spans) wantWords.Add(string.Concat(want.GetRange(s, e - s)));

            var pages = ReadPagesByOcr(fg, area, covered, wantWords);
            if (pages == null) return false;

            // A fix is made only if both readings saw the very same wrong word and nothing
            // unsure lies between it and the caret.
            var fixes = OcrMatch.Agree(pages, out uncertain);

            // What we believe is on screen: the wanted text with those words as seen.
            w = new List<string>(want);
            fixes.Sort((x, y) => y.WordStart.CompareTo(x.WordStart));
            int changed = 0;
            foreach (var f in fixes)
            {
                int s = spans[f.WordStart].start, e = spans[f.WordEnd - 1].end;
                var seenUnits = Split(f.Seen);
                w.RemoveRange(s, e - s);
                w.InsertRange(s, seenUnits);
                changed += Math.Max(e - s, seenUnits.Count);
            }
            hunks = fixes.Count == 0 ? new List<Hunk>() : DiffHunks(w, want, changed * 2 + 10, true);
            if (hunks == null) { hunks = new List<Hunk>(); uncertain += fixes.Count; }
            return true;
        }

        private const int MaxOcrPages = 15;

        /// <summary>
        /// Reads our text off the screen, page by page. The first page is what's visible now (it
        /// ends at the caret); if our text starts further up, the mouse wheel scrolls up a bit
        /// more than half a screen at a time, and every page is matched against the part of the
        /// text just before the page below it. While one page is being recognised the next one is
        /// already scrolled to and captured. Afterwards the view and the mouse are put back.
        /// Returns null if our text isn't found on the first page.
        /// </summary>
        private List<OcrMatch.Result[]> ReadPagesByOcr(IntPtr fg, System.Drawing.Rectangle area, bool covered, List<string> wantWords)
        {
            var pages = new List<OcrMatch.Result[]>();
            ScreenOcr.Reading[] read0;
            System.Drawing.Bitmap prev = ScreenOcr.Capture(area, covered, fg);
            if (prev == null) return null;
            read0 = ScreenOcr.ReadAll(prev);
            var p0 = new[] { OcrMatch.Compare(read0[0]?.Words, wantWords), OcrMatch.Compare(read0[1]?.Words, wantWords) };
            if (p0[0] == null && p0[1] == null) { prev.Dispose(); return null; }
            pages.Add(p0);

            int FirstOf(OcrMatch.Result[] p) => Math.Max(p[0]?.FirstWord ?? 0, p[1]?.FirstWord ?? 0);
            int firstWord = FirstOf(p0);
            if (firstWord <= 0) { prev.Dispose(); return pages; }   // all of it was on screen

            // ---- more text above: scroll up page by page
            Report(EngineState.Thinking, "截图识别：内容较长，向上滚动继续核对…");
            bool remote = _kb.IsKeyMode;
            double lineH = Math.Max(read0[0]?.LineHeight ?? 0, read0[1]?.LineHeight ?? 0);
            if (lineH < 8) lineH = _baseline.CaretOk && _baseline.H > 8 ? _baseline.H : 20;
            uint wheelLines = 3;
            if (SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out uint wl, 0) && wl > 0 && wl < 100) wheelLines = wl;
            int notches = Math.Max(1, (int)(area.Height * 0.55 / (lineH * wheelLines)));
            int visible = Math.Max(1, wantWords.Count - firstWord);
            int pagesWanted = Math.Min(MaxOcrPages, 1 + (int)Math.Ceiling(firstWord / Math.Max(1.0, visible * 0.5)));

            GetCursorPos(out POINT mouse0);
            var c = _baseline;
            int mx = area.Left + area.Width / 2, my = area.Top + area.Height / 2;
            if (_caretTracks && c.CaretOk && area.Contains(c.X, c.Y - (int)lineH))
            { mx = c.X; my = c.Y - (int)lineH; }   // over the text box itself (chat apps: the box scrolls, not the window)
            int scrolled = 0;

            // Recognition runs in the background, one page after another, in page order; each page
            // is compared against the text just before the first word the page below it showed.
            object gate = new object();
            int nextEnd = Math.Min(wantWords.Count, firstWord + 4);   // a few words of overlap
            bool chainDone = false;
            Task chain = Task.CompletedTask;
            try
            {
                SetCursorPos(mx, my);
                for (int page = 1; page < pagesWanted; page++)
                {
                    lock (gate) if (chainDone) break;
                    Wheel(notches);
                    scrolled += notches;
                    SleepPlain(remote ? 500 : 220);              // let it repaint (remote: let the frame arrive)
                    var shot = ScreenOcr.Capture(area, covered, fg);
                    if (shot != null && ScreenOcr.Same(shot, prev))
                    {
                        SleepPlain(remote ? 600 : 200);          // maybe just slow to repaint
                        shot.Dispose();
                        shot = ScreenOcr.Capture(area, covered, fg);
                        if (shot == null || ScreenOcr.Same(shot, prev)) { shot?.Dispose(); break; }   // reached the top
                    }
                    if (shot == null) break;

                    // Keep at most one page waiting: let the previous page finish first.
                    WaitTask(chain);
                    prev.Dispose();
                    prev = shot;
                    var img = (System.Drawing.Bitmap)shot.Clone();
                    chain = chain.ContinueWith(_ =>
                    {
                        try
                        {
                            int end;
                            lock (gate) { if (chainDone) return; end = nextEnd; }
                            var rd = ScreenOcr.ReadAll(img);
                            var p = new[] { OcrMatch.Compare(rd[0]?.Words, wantWords, end), OcrMatch.Compare(rd[1]?.Words, wantWords, end) };
                            lock (gate)
                            {
                                if (p[0] == null && p[1] == null) { chainDone = true; return; }   // lost track: stop here
                                pages.Add(p);
                                int fw = FirstOf(p);
                                if (fw <= 0 || fw >= end) chainDone = true;                     // reached the start / no progress
                                else nextEnd = Math.Min(end, fw + 4);
                            }
                        }
                        finally { img.Dispose(); }
                    }, TaskScheduler.Default);
                }
                WaitTask(chain);
            }
            finally
            {
                prev.Dispose();
                // Put the view and the mouse back where they were.
                for (int left = scrolled; left > 0; left -= 10) { Wheel(-Math.Min(10, left)); Thread.Sleep(30); }
                SetCursorPos(mouse0.X, mouse0.Y);
                if (scrolled > 0) Thread.Sleep(remote ? 400 : 150);
            }
            lock (gate) return new List<OcrMatch.Result[]>(pages);
        }

        /// <summary>Waits for background work while still honouring Stop.</summary>
        private void WaitTask(Task t)
        {
            while (!t.Wait(50))
                if (_stop) throw new OperationCanceledException();
        }

        /// <summary>
        /// Widens Chinese fixes to the surrounding word (from the phrase table) or at least one
        /// neighbouring character, so the IME gets context: "长得" instead of a lone "得".
        /// Only widens over text that is already correct and never into another fix.
        /// </summary>
        internal static void ExpandToWords(List<Hunk> hunks, List<string> a, List<string> b)
        {
            for (int i = 0; i < hunks.Count; i++)
            {
                var h = hunks[i];
                int bEnd = h.BStart + h.Insert.Count;
                if (h.Insert.Count == 0 || !h.Insert.TrueForAll(IsCjk)) continue;
                int prevEnd = i > 0 ? hunks[i - 1].AEnd : 0;
                int nextStart = i + 1 < hunks.Count ? hunks[i + 1].AStart : a.Count;

                int bestL = 0, bestR = 0;
                bool found = false;
                for (int len = 4; len >= 2 && !found; len--)
                {
                    for (int s = Math.Max(0, bEnd - len); s <= h.BStart && !found; s++)
                    {
                        int e = s + len;
                        if (e < bEnd || e > b.Count) continue;
                        int l = h.BStart - s, r = e - bEnd;
                        if (l == 0 && r == 0) continue;
                        if (!CanWiden(a, b, h, l, r, prevEnd, nextStart)) continue;
                        if (!Pinyin.IsPhrase(b, s, len)) continue;
                        bestL = l; bestR = r; found = true;
                    }
                }
                if (!found && CanWiden(a, b, h, 1, 0, prevEnd, nextStart)) { bestL = 1; found = true; }
                if (!found) continue;

                h.Insert.InsertRange(0, b.GetRange(h.BStart - bestL, bestL));
                h.Insert.AddRange(b.GetRange(bEnd, bestR));
                h.AStart -= bestL; h.AEnd += bestR; h.BStart -= bestL;
            }
        }

        private static bool CanWiden(List<string> a, List<string> b, Hunk h, int l, int r, int prevEnd, int nextStart)
        {
            int bEnd = h.BStart + h.Insert.Count;
            if (h.BStart - l < 0 || bEnd + r > b.Count) return false;
            if (h.AStart - l < prevEnd || h.AEnd + r > nextStart) return false;
            for (int k = 1; k <= l; k++)
                if (!IsCjk(b[h.BStart - k]) || a[h.AStart - k] != b[h.BStart - k]) return false;
            for (int k = 0; k < r; k++)
                if (!IsCjk(b[bEnd + k]) || a[h.AEnd + k] != b[bEnd + k]) return false;
            return true;
        }

        /// <summary>Walks back from the end, fixing the last mistake first so earlier positions stay valid.</summary>
        private void ApplyHunks(List<Hunk> hunks, int docLen)
        {
            int caret = docLen;
            int newEnd = docLen;
            for (int i = hunks.Count - 1; i >= 0; i--)
            {
                var h = hunks[i];
                Wait(Rand(80, 200));                     // spot it
                MoveCaret(ref caret, h.AEnd);
                Wait(Rand(50, 120));
                for (int k = h.AEnd; k > h.AStart; k--)
                {
                    Key("\b");
                    caret--;
                    Wait(Math.Max(30, LatinBaseMs * 0.4 * Jitter()));
                }
                for (int k = 0; k < h.Insert.Count;)
                {
                    string s = h.Insert[k];
                    if (_kb.IsKeyMode && IsCjk(s))
                    {
                        // a run of Chinese: type it as one pinyin word
                        int e = k;
                        while (e < h.Insert.Count && IsCjk(h.Insert[e])) e++;
                        var syl = Pinyin.Of(h.Insert, k, e - k);
                        if (Array.IndexOf(syl, null) < 0)
                        {
                            TypePinyin(syl, e - k);
                            caret += e - k;
                            k = e;
                            continue;
                        }
                    }
                    Wait(IsCjk(s) ? CjkBaseMs * 0.5 * Jitter() : LatinBaseMs * 0.6 * Jitter());
                    Key(s);
                    caret++;
                    k++;
                }
                newEnd += h.Insert.Count - (h.AEnd - h.AStart);
            }
            Wait(Rand(60, 150));
            MoveCaret(ref caret, newEnd);                // back to where we stopped typing
        }

        private void MoveCaret(ref int caret, int to)
        {
            while (caret != to)
            {
                Gate();
                bool left = to < caret;
                Tap(left ? VK_LEFT : VK_RIGHT);
                caret += left ? -1 : 1;
                _unsampled += left ? -1 : 1;
                _lastKeyMs = Now;
                Wait(Rand(8, 20));
            }
        }

        private bool TryReadTextBeforeCaret(out string before)
        {
            before = null;
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el == null) return false;
                if (!el.TryGetCurrentPattern(TextPattern.Pattern, out object p)) return false;
                var tp = (TextPattern)p;
                var sel = tp.GetSelection();
                if (sel == null || sel.Length == 0) return false;
                var r = tp.DocumentRange.Clone();
                r.MoveEndpointByRange(TextPatternRangeEndpoint.End, sel[0], TextPatternRangeEndpoint.Start);
                before = r.GetText(-1) ?? "";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Normalize(string s) =>
            s.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Replace('\u000B', '\n');

        private static string NormUnit(string u) => u == " " ? " " : u;

        // ------------------------------------------------------------------ diff

        /// <summary>
        /// Aligns the wanted text b against the tail of the document window a. The alignment is
        /// anchored at the caret (the end of a) and runs backwards, so text that was already in
        /// the document before ours is never touched. Returns the edits (in ascending document
        /// order), or null if the texts differ too much.
        /// </summary>
        private const int Invalid = -1000000;

        internal static List<Hunk> DiffHunks(List<string> a, List<string> b, int maxD, bool global = false)
        {
            int n = a.Count, m = b.Count;
            // reversed views
            string A(int i) => a[n - 1 - i];
            string B(int i) => b[m - 1 - i];

            int max = n + m;
            int off = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int dFinal = -1, fx = 0;

            for (int d = 0; d <= Math.Min(max, maxD) && dFinal < 0; d++)
            {
                var snap = new int[2 * d + 3];
                Array.Copy(v, off - d - 1, snap, 0, 2 * d + 3);
                trace.Add(snap);

                for (int k = -d; k <= d; k += 2)
                {
                    int x = (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) ? v[off + k + 1] : v[off + k - 1] + 1;
                    int y = x - k;
                    if (x < 0 || x > n || y < 0 || y > m) { v[off + k] = Invalid; continue; }   // off the grid
                    while (x < n && y < m && A(x) == B(y)) { x++; y++; }
                    v[off + k] = x;
                    if (y >= m && (!global || x >= n)) { dFinal = d; fx = x; break; }   // all wanted text consumed
                }
            }
            if (dFinal < 0) return null;

            // Backtrack (yields ops from the end of the reversed sequences = original ascending order).
            // op: 0 = equal, 1 = delete a[ai], 2 = insert b[bi] at a-position ai
            var ops = new List<(int op, int ai, int bi)>();
            int cx = fx, cy = m;
            for (int d = dFinal; d >= 0; d--)
            {
                int px, py;
                if (d == 0) { px = 0; py = 0; }
                else
                {
                    var vs = trace[d];
                    int dd = d;
                    int V(int k) => vs[k + dd + 1];
                    int k0 = cx - cy;
                    int prevK = (k0 == -d || (k0 != d && V(k0 - 1) < V(k0 + 1))) ? k0 + 1 : k0 - 1;
                    px = V(prevK);
                    py = px - prevK;
                }
                while (cx > px && cy > py) { ops.Add((0, n - cx, m - cy)); cx--; cy--; }
                if (d > 0)
                {
                    if (cx == px) ops.Add((2, n - cx, m - 1 - py));   // insert B(py)
                    else ops.Add((1, n - 1 - px, m - py));          // delete A(px); bi = position in b
                }
                cx = px; cy = py;
            }

            var hunks = new List<Hunk>();
            Hunk cur = null;
            foreach (var (op, ai, bi) in ops)
            {
                if (op == 0) { cur = null; continue; }
                if (cur == null)
                {
                    cur = new Hunk { AStart = ai, AEnd = ai, BStart = bi };
                    hunks.Add(cur);
                }
                if (op == 1) { cur.AStart = Math.Min(cur.AStart, ai); cur.AEnd = Math.Max(cur.AEnd, ai + 1); }
                else
                {
                    cur.AStart = Math.Min(cur.AStart, ai); cur.AEnd = Math.Max(cur.AEnd, ai);
                    cur.Insert.Add(b[bi]);
                }
            }
            return hunks;
        }
    }
}
