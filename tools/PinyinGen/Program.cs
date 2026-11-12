// Usage: PinyinGen <pinyin.txt> <phrase-pinyin.txt> <out pinyin.dat.gz>
//   pinyin.txt        https://github.com/mozillazg/pinyin-data          (MIT)
//   phrase-pinyin.txt https://github.com/mozillazg/phrase-pinyin-data   (MIT)
// Output (UTF-8, gzip): one "汉字<TAB>pin yin" line per entry, tones removed, ü written as v.
// Single characters keep their most common reading; phrases (2-4 chars) are kept only when
// their reading differs from the single-character readings, to resolve polyphones.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

static class Program
{
    // Common characters whose most frequent standalone reading differs from the dataset's first one.
    static readonly Dictionary<char, string> Overrides = new Dictionary<char, string>
    {
        ['长'] = "chang", ['的'] = "de", ['了'] = "le", ['着'] = "zhe", ['么'] = "me", ['和'] = "he",
        ['那'] = "na", ['哪'] = "na", ['呢'] = "ne", ['吗'] = "ma", ['嗯'] = "en", ['还'] = "hai",
    };

    static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("usage: PinyinGen pinyin.txt phrase.txt out.dat.gz"); return 1; }
        var chars = new SortedDictionary<char, string>();
        foreach (var line in File.ReadLines(args[0], Encoding.UTF8))
        {
            if (!line.StartsWith("U+")) continue;
            int colon = line.IndexOf(':');
            int cp = int.Parse(line.Substring(2, colon - 2), NumberStyles.HexNumber);
            if (cp > 0xFFFF) continue;                                 // BMP only
            char c = (char)cp;
            if (!((c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || c == 0x3007)) continue;
            string rest = line.Substring(colon + 1);
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest.Substring(0, hash);
            string first = rest.Split(',')[0].Trim();
            if (first.Length == 0) continue;
            chars[c] = Toneless(first);
        }
        foreach (var kv in Overrides) chars[kv.Key] = kv.Value;

        var phrases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(args[1], Encoding.UTF8))
        {
            if (line.StartsWith("#")) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string word = line.Substring(0, colon).Trim();
            if (word.Length < 2 || word.Length > 4 || word.Any(ch => !chars.ContainsKey(ch))) continue;
            var syl = line.Substring(colon + 1).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Toneless).ToArray();
            if (syl.Length != word.Length) continue;
            bool differs = false;
            for (int i = 0; i < word.Length; i++) if (syl[i] != chars[word[i]]) { differs = true; break; }
            if (differs) phrases[word] = string.Join(" ", syl);
        }

        using (var fs = File.Create(args[2]))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            w.NewLine = "\n";
            foreach (var kv in chars) w.WriteLine(kv.Key + "\t" + kv.Value);
            foreach (var kv in phrases) w.WriteLine(kv.Key + "\t" + kv.Value);
        }
        Console.WriteLine($"chars={chars.Count} phrases={phrases.Count} bytes={new FileInfo(args[2]).Length}");
        return 0;
    }

    static string Toneless(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                if (ch == '̈' && sb.Length > 0 && sb[sb.Length - 1] == 'u') sb[sb.Length - 1] = 'v';   // ü -> v
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Trim();
    }
}
