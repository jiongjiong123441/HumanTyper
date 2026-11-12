using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace HumanTyper
{
    internal sealed class Settings
    {
        // 速度
        public int EnWpm = 55;          // 英文 词/分钟
        public int ZhCpm = 80;          // 中文 字/分钟
        public int Variance = 30;       // 节奏波动 %

        // 错字
        public double TypoRate = 3.0;   // 每个字符出错概率 %
        public int MaxNoticeDelay = 3;  // 最多再打几个字才发现错误

        // 思考停顿
        public bool ThinkEnabled = true;
        public int WordEveryMin = 4, WordEveryMax = 12;
        public double WordPauseMin = 0.4, WordPauseMax = 1.8;
        public int SentEveryMin = 1, SentEveryMax = 3;
        public double SentPauseMin = 1.5, SentPauseMax = 5.0;
        public double ParaPauseMin = 2.5, ParaPauseMax = 7.0;
        public bool PunctPause = true;

        // 控制
        public bool AutoPause = true;
        public bool Proofread = true;   // 打完后整体核对并用键盘修正
        public int Countdown = 3;
        public int NewlineMode = 0;     // 0 = Enter, 1 = Shift+Enter
        public bool MinimizeOnStart = false;
        public bool TopMost = false;
        public Keys StartPauseKey = Keys.F8;
        public Keys StopKey = Keys.F9;
        public Keys RestartKey = Keys.F10;

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HumanTyper", "settings.ini");

        public Settings Clone() => (Settings)MemberwiseClone();

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var f = typeof(Settings).GetField(line.Substring(0, eq).Trim(), BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    string v = line.Substring(eq + 1).Trim();
                    try
                    {
                        object val;
                        if (f.FieldType == typeof(int)) val = int.Parse(v, CultureInfo.InvariantCulture);
                        else if (f.FieldType == typeof(double)) val = double.Parse(v, CultureInfo.InvariantCulture);
                        else if (f.FieldType == typeof(bool)) val = bool.Parse(v);
                        else if (f.FieldType == typeof(Keys)) val = (Keys)int.Parse(v, CultureInfo.InvariantCulture);
                        else continue;
                        f.SetValue(s, val);
                    }
                    catch { }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(this);
                    string str = v is Keys k ? ((int)k).ToString(CultureInfo.InvariantCulture)
                               : Convert.ToString(v, CultureInfo.InvariantCulture);
                    sb.Append(f.Name).Append('=').Append(str).AppendLine();
                }
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static string KeyName(Keys k)
        {
            if ((k & Keys.KeyCode) == Keys.None) return "无";
            var sb = new StringBuilder();
            if ((k & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((k & Keys.Alt) != 0) sb.Append("Alt+");
            if ((k & Keys.Shift) != 0) sb.Append("Shift+");
            Keys code = k & Keys.KeyCode;
            string name = code.ToString();
            if (code >= Keys.D0 && code <= Keys.D9) name = name.Substring(1);
            sb.Append(name);
            return sb.ToString();
        }
    }
}
