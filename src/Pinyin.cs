using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HumanTyper
{
    /// <summary>
    /// Chinese → toneless pinyin (ü written as v, as typed on a keyboard).
    /// Data: mozillazg/pinyin-data and mozillazg/phrase-pinyin-data (MIT), packed by tools/PinyinGen.
    /// Multi-character phrases are matched longest-first so polyphones get the reading that fits
    /// the word (银行 → yin hang, 睡觉 → shui jiao).
    /// </summary>
    internal static class Pinyin
    {
        private static Dictionary<string, string[]> _map;
        private static readonly object Lock = new object();

        private static Dictionary<string, string[]> Map
        {
            get
            {
                if (_map != null) return _map;
                lock (Lock)
                {
                    if (_map != null) return _map;
                    var d = new Dictionary<string, string[]>(32000, StringComparer.Ordinal);
                    using (var s = typeof(Pinyin).Assembly.GetManifestResourceStream("HumanTyper.pinyin.dat.gz"))
                    {
                        if (s != null)
                        {
                            using (var gz = new GZipStream(s, CompressionMode.Decompress))
                            using (var r = new StreamReader(gz, Encoding.UTF8))
                            {
                                string line;
                                while ((line = r.ReadLine()) != null)
                                {
                                    int tab = line.IndexOf('\t');
                                    if (tab <= 0) continue;
                                    d[line.Substring(0, tab)] = line.Substring(tab + 1).Split(' ');
                                }
                            }
                        }
                    }
                    _map = d;
                    return d;
                }
            }
        }

        /// <summary>Returns one syllable per unit, or null for units with no known reading.</summary>
        public static string[] Of(IList<string> units, int start, int count)
        {
            var result = new string[count];
            var map = Map;
            int i = 0;
            while (i < count)
            {
                bool matched = false;
                for (int len = Math.Min(4, count - i); len >= 2; len--)
                {
                    var sb = new StringBuilder();
                    for (int k = 0; k < len; k++) sb.Append(units[start + i + k]);
                    if (map.TryGetValue(sb.ToString(), out var syl) && syl.Length == len)
                    {
                        Array.Copy(syl, 0, result, i, len);
                        i += len;
                        matched = true;
                        break;
                    }
                }
                if (matched) continue;
                result[i] = map.TryGetValue(units[start + i], out var one) ? one[0] : null;
                i++;
            }
            return result;
        }

        /// <summary>True if units[start..start+len) is a known multi-character word.</summary>
        public static bool IsPhrase(IList<string> units, int start, int len)
        {
            if (len < 2) return false;
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++) sb.Append(units[start + k]);
            return Map.ContainsKey(sb.ToString()) || Words.Contains(sb.ToString());
        }

        // Common two-character words whose reading is regular (so the generator didn't keep them)
        // but which still make good context when retyping a fix.
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.Ordinal)
        {
            "我们","你们","他们","她们","什么","怎么","这个","那个","这样","那样","因为","所以","但是","如果","已经","可以",
            "没有","知道","觉得","时候","现在","今天","明天","昨天","一起","大家","自己","事情","问题","工作","学习","生活",
            "朋友","同意","方案","完成","开始","结束","努力","喜欢","希望","需要","应该","可能","非常","特别","真的","还是",
        };

        /// <summary>
        /// Joins syllables the way people type them, adding an apostrophe where the next syllable
        /// starts with a vowel and could otherwise merge (xi'an, fang'an).
        /// </summary>
        public static string Join(IList<string> syllables)
        {
            var sb = new StringBuilder();
            foreach (var s in syllables)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (sb.Length > 0 && "aeo".IndexOf(s[0]) >= 0) sb.Append('\'');
                sb.Append(s);
            }
            return sb.ToString();
        }
    }
}
