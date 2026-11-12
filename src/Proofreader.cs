using System;
using System.Collections.Generic;
using System.Threading;
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

        private void Proofread()
        {
            var want = new List<string>(_u.Count);
            foreach (var x in _u) want.Add(NormUnit(x));

            int totalFixed = 0, skippedCn = 0;
            for (int pass = 1; pass <= 2; pass++)
            {
                Report(EngineState.Thinking, pass == 1 ? "正在从头核对一遍…" : "再核对一遍…");
                Wait(Rand(200, 450));
                Gate();
                SleepPlain(120);   // let the target finish processing our keystrokes

                if (!TryReadTextBeforeCaret(out string before))
                {
                    _proofMsg = totalFixed > 0 ? $"已修正 {totalFixed} 处（之后无法再读取目标文本）"
                                               : "目标程序不支持读取文本，已跳过核对";
                    return;
                }

                var doc = Split(Normalize(before));
                int slack = 20 + want.Count / 10;
                List<string> w;
                bool exact = PriorStillThere(doc);
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
                if (hunks == null)
                {
                    _proofMsg = "与原文差异过大（光标可能不在文末），已跳过自动修正";
                    return;
                }
                if (_kb.IsKeyMode)
                {
                    // Typing through an IME: a Chinese character swapped for another one is just the
                    // IME picking a homophone. Let it be — only fix missing/extra text and non-Chinese.
                    int before0 = hunks.Count;
                    hunks.RemoveAll(h => h.Insert.Count > 0 && h.AEnd - h.AStart == h.Insert.Count &&
                                         h.Insert.TrueForAll(IsCjk) && w.GetRange(h.AStart, h.AEnd - h.AStart).TrueForAll(IsCjk));
                    if (pass == 1) skippedCn = before0 - hunks.Count;
                }
                if (hunks.Count == 0)
                {
                    string skip = skippedCn > 0 ? $"（{skippedCn} 处中文选词差异未处理）" : "";
                    _proofMsg = (totalFixed > 0 ? $"核对完成，已修正 {totalFixed} 处" : "核对完成，没有发现错误") + skip;
                    return;
                }

                int changed = 0;
                foreach (var h in hunks) changed += Math.Max(h.AEnd - h.AStart, h.Insert.Count);
                if (changed > Math.Max(8, want.Count / 4))
                {
                    _proofMsg = $"发现 {changed} 处差异，差异过大，为避免误改已跳过自动修正";
                    return;
                }

                Report(EngineState.Running, $"发现 {hunks.Count} 处错误，正在修改…");
                // Missing Chinese gets retyped as a whole word so the IME has context.
                if (_kb.IsKeyMode) ExpandToWords(hunks, w, want);
                _fixing = true;
                try { ApplyHunks(hunks, w.Count); } finally { _fixing = false; }
                totalFixed += hunks.Count;
            }
            _proofMsg = $"已修正 {totalFixed} 处" + (skippedCn > 0 ? $"（{skippedCn} 处中文选词差异未处理）" : "");
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
