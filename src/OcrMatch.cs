using System;
using System.Collections.Generic;
using System.Text;

namespace HumanTyper
{
    /// <summary>
    /// Compares OCR'd words from the screen with the text we meant to type, word by word, and
    /// reports only differences OCR can't plausibly have caused itself. Characters OCR often mixes
    /// up (0/o, 1/l/I, rn/m…), letter case and punctuation are ignored; Chinese isn't checked.
    /// </summary>
    internal static class OcrMatch
    {
        internal sealed class Fix
        {
            public int WordStart, WordEnd;   // range of wanted words [start, end)
            public string Seen;              // what is on screen instead (raw OCR text)
            public override bool Equals(object o) => o is Fix f && f.WordStart == WordStart && f.WordEnd == WordEnd && f.Seen == Seen;
            public override int GetHashCode() => WordStart * 31 + WordEnd + (Seen ?? "").GetHashCode();
        }

        internal sealed class Result
        {
            public List<Fix> Fixes = new List<Fix>();
            public int Uncertain;            // differences that were not safe to act on
            /// <summary>Last wanted word (index) with a difference we did not act on, -1 if none.
            /// Fixes are positioned by counting back from the caret, so a fix is only safe after it.</summary>
            public int LastUnsure = -1;
        }

        private const string Wild = "~";

        /// <summary>Spans [start, end) of the whitespace-separated words in a list of text units.</summary>
        public static List<(int start, int end)> WordSpans(List<string> units)
        {
            var spans = new List<(int, int)>();
            int s = -1;
            for (int i = 0; i <= units.Count; i++)
            {
                bool ws = i == units.Count || units[i] == " " || units[i] == "\n" || units[i] == "\t" || units[i] == "　";
                if (!ws && s < 0) s = i;
                else if (ws && s >= 0) { spans.Add((s, i)); s = -1; }
            }
            return spans;
        }

        /// <summary>Comparison key: letters and digits only, lower case, OCR look-alikes merged.</summary>
        public static string Key(string word)
        {
            var sb = new StringBuilder(word.Length);
            foreach (char ch in word)
            {
                if (ch >= 0x80) return Wild;                 // Chinese etc.: not checked
                if (!char.IsLetterOrDigit(ch)) continue;
                char c = char.ToLowerInvariant(ch);
                switch (c)
                {
                    case '0': c = 'o'; break;
                    case '1': case 'i': case 'j': c = 'l'; break;
                    case '5': c = 's'; break;
                    case '8': c = 'b'; break;
                    case '2': c = 'z'; break;
                    case 'q': c = 'g'; break;
                }
                sb.Append(c);
            }
            return sb.Replace("rn", "m").Replace("vv", "w").Replace("cl", "d").ToString();
        }

        /// <summary>
        /// Locates the end of our text among the OCR'd words and lists the words that differ.
        /// Returns null if our text can't be found on screen.
        /// </summary>
        public static Result Compare(List<string> ocrWords, List<string> wantWords)
        {
            if (ocrWords == null || wantWords.Count == 0) return null;
            var a = ocrWords.ConvertAll(Key);
            var b = wantWords.ConvertAll(Key);
            // Words that are only punctuation have an empty key: drop them on both sides by mapping
            // them away would shift indexes, so give them a key that matches anything punctuation-like.
            for (int i = 0; i < a.Count; i++) if (a[i].Length == 0) a[i] = ".";
            for (int i = 0; i < b.Count; i++) if (b[i].Length == 0) b[i] = ".";

            // 1) Where does our text end on screen? The OCR position whose preceding words match
            //    the last few wanted words best (latest one wins on ties).
            int k = Math.Min(5, b.Count);
            int need = k <= 2 ? k : k - 2;
            int bestJ = -1, bestScore = -1;
            for (int j = k - 1; j < a.Count; j++)
            {
                int score = 0;
                for (int t = 0; t < k; t++)
                    if (a[j - t] == b[b.Count - 1 - t] && b[b.Count - 1 - t] != Wild) score++;
                if (score >= bestScore) { bestScore = score; bestJ = j; }
            }
            if (bestJ < 0 || bestScore < need) return null;

            var aw = a.GetRange(0, bestJ + 1);
            int L = Math.Min(b.Count, aw.Count + 10);
            int off = b.Count - L;
            var bw = b.GetRange(off, L);

            var hunks = TypingEngine.DiffHunks(aw, bw, 6 + L / 4);
            if (hunks == null) return null;

            var res = new Result();
            foreach (var h in hunks)
            {
                int na = h.AEnd - h.AStart, nb = h.Insert.Count;
                int bStart = off + h.BStart, bLast = bStart + Math.Max(nb, 1) - 1;
                void Unsure(bool count) { if (count) res.Uncertain++; res.LastUnsure = Math.Max(res.LastUnsure, bLast); }
                // Differences at the very start are just where the visible text begins.
                if (h.AStart == 0) continue;
                if (na == 0 || nb == 0) { Unsure(true); continue; }   // a whole word missing/extra: OCR drops words too
                bool wild = false;
                for (int i = 0; i < na; i++) if (aw[h.AStart + i] == Wild) wild = true;
                foreach (var x in h.Insert) if (x == Wild) wild = true;
                if (wild) { Unsure(false); continue; }   // Chinese: not checked, length unknown

                if (na == nb)
                {
                    for (int i = 0; i < na; i++)
                    {
                        string seen = ocrWords[h.AStart + i];
                        string want = wantWords[bStart + i];
                        if (aw[h.AStart + i] == bw[h.BStart + i]) continue;
                        if (Math.Abs(seen.Length - want.Length) > 2 || Dist(Key(seen), Key(want)) > 2) { res.Uncertain++; res.LastUnsure = Math.Max(res.LastUnsure, bStart + i); continue; }
                        res.Fixes.Add(new Fix { WordStart = bStart + i, WordEnd = bStart + i + 1, Seen = seen });
                    }
                }
                else if (string.Concat(aw.GetRange(h.AStart, na)) == string.Concat(h.Insert))
                {
                    // Same letters, different spacing: "todifferent" for "to different".
                    res.Fixes.Add(new Fix
                    {
                        WordStart = bStart, WordEnd = bStart + nb,
                        Seen = string.Join(" ", ocrWords.GetRange(h.AStart, na))
                    });
                }
                else Unsure(true);
            }
            return res;
        }

        /// <summary>
        /// Combines two readings: a fix is kept only if both saw the very same wrong word and
        /// nothing unsure lies between it and the caret (that could shift its position).
        /// </summary>
        public static List<Fix> Agree(Result r1, Result r2, out int uncertain)
        {
            uncertain = Math.Max(r1?.Uncertain ?? 0, r2?.Uncertain ?? 0);
            var fixes = new List<Fix>();
            if (r1 == null || r2 == null) return fixes;
            int lastUnsure = Math.Max(r1.LastUnsure, r2.LastUnsure);
            foreach (var f in r1.Fixes) if (!r2.Fixes.Contains(f)) lastUnsure = Math.Max(lastUnsure, f.WordEnd - 1);
            foreach (var f in r2.Fixes) if (!r1.Fixes.Contains(f)) lastUnsure = Math.Max(lastUnsure, f.WordEnd - 1);
            foreach (var f in r1.Fixes)
            {
                if (r2.Fixes.Contains(f) && f.WordStart > lastUnsure) fixes.Add(f);
                else uncertain++;
            }
            return fixes;
        }

        private static int Dist(string s, string t)
        {
            var d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;
            for (int i = 1; i <= s.Length; i++)
                for (int j = 1; j <= t.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
            return d[s.Length, t.Length];
        }
    }
}
