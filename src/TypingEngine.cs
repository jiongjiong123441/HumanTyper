using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    internal enum EngineState { Idle, Running, Thinking, Paused, Finished, Stopped }

    /// <summary>
    /// Types text into a target window the way a person would: uneven rhythm, typos that get
    /// noticed and corrected, pauses to think between words / sentences / paragraphs, and an
    /// IME-like cadence for Chinese. Pauses itself when the user takes over the keyboard/mouse
    /// or leaves the target window, and resumes once the caret is back where it was.
    /// </summary>
    internal sealed partial class TypingEngine
    {
        private string _proofMsg;
        public event Action<int, EngineState, string> StateChanged;   // (runId, state, message)
        public event Action<int, int, int> ProgressChanged;          // (runId, done, total)
        private volatile int _runId;
        public int RunId => _runId;

        private readonly Random _rnd = new Random();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private Settings _s;
        private List<string> _u;          // text split into units (one visible character each)

        private volatile bool _stop;
        private volatile bool _manualPause;
        private volatile bool _rebaseline;
        private volatile bool _autoPaused;
        private volatile bool _interfered;
        private long _lastUserInputMs;
        private long _lastKeyMs;
        private long _lastSampleMs;
        private long _lastProgressMs;

        private volatile IntPtr _target;
        private IntPtr _targetFocus;
        private CaretProbe.Snapshot _baseline;
        private int _unsampled;      // net characters typed since _baseline was sampled (backspace = -1)
        private int _catchUp;        // Right-arrow presses needed after resuming at _baseline
        private bool _caretTracks;   // we've seen the caret move as we typed (so its position is meaningful)

        private double _flow = 1.0;
        private int _wordsLeft, _sentencesLeft;
        private bool _pendingSentence;
        private int _noTypoAt = -1;

        public bool IsBusy => _thread != null && _thread.IsAlive;
        public bool IsPaused => _manualPause || _autoPaused;
        public bool IsManualPaused => _manualPause;
        public IntPtr TargetWindow => _target;

        private long Now => _clock.ElapsedMilliseconds;

        // ------------------------------------------------------------------ control API

        public void Start(string text, Settings s, IntPtr target)
        {
            if (IsBusy) return;
            _runId++;
            _s = s.Clone();
            _u = Split(text);
            _target = target;
            _stop = false; _manualPause = false; _rebaseline = false; _autoPaused = false; _interfered = false;
            _unsampled = 0; _catchUp = 0; _caretTracks = false;
            _flow = 1.0; _pendingSentence = false; _noTypoAt = -1;
            _wordsLeft = _rnd.Next(_s.WordEveryMin, Math.Max(_s.WordEveryMin, _s.WordEveryMax) + 1);
            _sentencesLeft = _rnd.Next(_s.SentEveryMin, Math.Max(_s.SentEveryMin, _s.SentEveryMax) + 1);
            _thread = new Thread(Worker) { IsBackground = true, Name = "HumanTyper" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Running → manual pause. Paused (any reason) → resume at the current caret.</summary>
        public void TogglePause()
        {
            if (!IsBusy) return;
            if (_manualPause || _autoPaused)
            {
                _rebaseline = true;
                _manualPause = false;
            }
            else
            {
                _manualPause = true;
            }
        }

        public void Stop()
        {
            _stop = true;
        }

        public void StopAndWait(int timeoutMs = 2000)
        {
            _stop = true;
            var t = _thread;
            if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(timeoutMs);
        }

        /// <summary>Called from the low-level input hooks for physical (non-injected) input.</summary>
        public void NotifyUserInput(bool isMouse, IntPtr mouseRootWindow)
        {
            if (!IsBusy || _manualPause) return;
            if (!isMouse && KeyboardOut.IsSelfEcho) return;   // our own driver-level keystrokes
            IntPtr target = _target;
            if (target == IntPtr.Zero) return;
            if (isMouse)
            {
                // clicks in other programs are caught by the foreground check
                if (mouseRootWindow != target && !SameProcess(mouseRootWindow, target)) return;
            }
            else
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != target && !SameProcess(fg, target)) return;
            }

            Interlocked.Exchange(ref _lastUserInputMs, Now);
            _interfered = true;
        }

        // ------------------------------------------------------------------ worker

        private void Worker()
        {
            _myRun = _runId;
            EngineState final = EngineState.Finished;
            try
            {
                try { _kb = new KeyboardOut((InputMode)_s.InputMode); }
                catch (Exception ex)
                {
                    Report(EngineState.Stopped, "驱动级输入不可用：" + ex.Message);
                    return;
                }
                _imeChinese = _s.RemoteImeChinese;
                var snap = CaretProbe.Read(_target);
                _targetFocus = snap.Focus;
                _baseline = snap;
                _lastSampleMs = Now;
                Report(EngineState.Running, "正在输入…");
                Progress(0, true);
                _proofMsg = null;
                RememberPrior();
                Run();
                Progress(_u.Count, true);
                if (_s.Proofread) Proofread();
                if (_kb.IsKeyMode) SetIme(_s.RemoteImeChinese);   // leave the input method as we found it
            }
            catch (OperationCanceledException)
            {
                final = EngineState.Stopped;
            }
            catch (Exception ex)
            {
                final = EngineState.Stopped;
                Report(EngineState.Stopped, "出错：" + ex.Message);
                return;
            }
            Report(final, final == EngineState.Finished ? "输入完成" + (_proofMsg != null ? " — " + _proofMsg : "") : "已停止");
        }

        private void Run()
        {
            int n = _u.Count;
            int i = 0;
            while (i < n)
            {
                string u = _u[i];
                if (IsCjk(u))
                {
                    int runEnd = i;
                    while (runEnd < n && IsCjk(_u[runEnd])) runEnd++;
                    int len;
                    if (_kb.IsKeyMode)
                    {
                        // Typing pinyin: longer chunks give the remote IME context to pick the right words.
                        int run = runEnd - i;
                        len = run <= 7 ? run : _rnd.Next(2, 7);
                        TypePinyinChunk(i, len);
                    }
                    else
                    {
                        len = PickChunkLength(runEnd - i);
                        TypeCjkChunk(i, len);
                    }
                    i += len;
                    Progress(i, false);
                    AfterWord(i - 1);
                    AfterUnit(i - 1);
                    continue;
                }

                if (i != _noTypoAt && IsTypoCandidate(u) && Chance(_s.TypoRate / 100.0))
                {
                    int ni = DoLatinTypo(i);
                    if (ni >= 0)
                    {
                        _noTypoAt = ni;
                        for (int k = i; k < ni; k++) AfterUnit(k);
                        i = ni;
                        Progress(i, false);
                        continue;
                    }
                }

                Wait(LatinDelay(i));
                Key(u);
                // In keyboard mode one Shift+6 / Shift+- already gives "……" / "——".
                if (_kb.IsKeyMode && (u == "…" || u == "—") && i + 1 < n && _u[i + 1] == u) i++;
                i++;
                Progress(i, false);
                AfterUnit(i - 1);
            }
        }

        // ------------------------------------------------------------------ rhythm

        private double LatinBaseMs => 60000.0 / (Math.Max(5, _s.EnWpm) * 5.0);
        private double CjkBaseMs => 60000.0 / Math.Max(5, _s.ZhCpm);

        private static readonly HashSet<string> FastBigrams = new HashSet<string>
        {
            "th","he","in","er","an","re","on","at","en","nd","ti","es","or","te","of","ed","is","it","al","ar",
            "st","to","nt","ng","se","ha","as","ou","io","le","ve","co","me","de","hi","ri","ro","ic","ne","ea"
        };

        private double LatinDelay(int i)
        {
            string cur = _u[i];
            string prev = i > 0 ? _u[i - 1] : "";
            double f = _flow;
            char c = cur[0];

            if (char.IsUpper(c) || "~!@#$%^&*()_+{}|:\"<>?".IndexOf(c) >= 0) f *= 1.35;
            else if (char.IsDigit(c)) f *= 1.15;
            else if (char.IsPunctuation(c) || char.IsSymbol(c)) f *= 1.2;

            if (prev == " ") f *= 1.12;
            if (prev.Length == 1 && FastBigrams.Contains((prev + cur).ToLowerInvariant())) f *= 0.75;
            if (prev == cur) f *= 0.85;
            if (cur == " ") f *= 0.9;

            return Clamp(LatinBaseMs * f * Jitter(), 18, LatinBaseMs * 6);
        }

        private double Jitter()
        {
            double sigma = _s.Variance / 100.0 * 0.6;
            return Math.Exp(sigma * Gaussian());
        }

        private void DriftFlow()
        {
            double v = _s.Variance / 100.0;
            _flow *= Math.Exp(Gaussian() * 0.12 * v);
            _flow += (1.0 - _flow) * 0.15;   // pull back towards normal pace
            _flow = Clamp(_flow, 0.75, 1.35);
        }

        private int PickChunkLength(int max)
        {
            double r = _rnd.NextDouble();
            int len = r < 0.25 ? 1 : r < 0.80 ? 2 : r < 0.95 ? 3 : 4;
            return Math.Max(1, Math.Min(len, max));
        }

        // ------------------------------------------------------------------ Chinese (IME-like)

        private void TypeCjkChunk(int start, int len)
        {
            // Time spent "spelling pinyin" for the whole word, then the word is committed in a burst.
            Wait(len * CjkBaseMs * 0.85 * _flow * Jitter());

            double p = 1 - Math.Pow(1 - _s.TypoRate / 100.0, len);
            if (Chance(p))
            {
                // Picked the wrong IME candidate: type wrong characters, notice, delete, re-pick.
                // Either the whole word is a wrong homophone, or one character of it is off.
                var wrong = new List<string>();
                int bad = _rnd.Next(len);
                bool whole = Chance(0.5);
                for (int k = 0; k < len; k++)
                    wrong.Add(whole || k == bad ? RandomCommonHan(_u[start + k]) : _u[start + k]);

                for (int k = 0; k < len; k++)
                {
                    Key(wrong[k]);
                    if (k + 1 < len) Wait(Rand(12, 40));
                }
                Wait(Rand(300, 1000));                      // notice it
                for (int k = 0; k < len; k++) { Key("\b"); Wait(Rand(70, 160)); }
                Wait(Rand(250, 700));                       // re-pick the right candidate
            }
            else
            {
                Wait(Rand(40, 160));     // choosing candidate / pressing space
            }

            for (int k = 0; k < len; k++)
            {
                Key(_u[start + k]);
                if (k + 1 < len) Wait(Rand(12, 40));
            }
        }

        private const string CommonHan =
            "的一是在不了有和人这中大为上个国我以要他时来用们生到作地于出就分对成会可主发年动同工也能下过子说产种面而方后多定行学法所民得经十三之进着等部度家电力里如水化高自二理起小物现实加量都两体制机当使点从业本去把性好应开它合还因由其些然前外天政四日那社义事平形相全表间样与关各重新线内数正心反你明看原又么利比或但质气第向道命此变条只没结解问意建月公无系军很情者最立代想已通并提直题程展五果料象员革位入常文总次品式活设及管特件长求老头基资边流路级少图山统接知较将组见计别她手角期根论运农指几九区强放决西被干做必战先回则任取据处府研";

        private string RandomCommonHan(string avoid)
        {
            for (int t = 0; t < 5; t++)
            {
                string c = CommonHan[_rnd.Next(CommonHan.Length)].ToString();
                if (c != avoid) return c;
            }
            return "的";
        }

        // ------------------------------------------------------------------ Chinese via pinyin keys (remote mode)

        private KeyboardOut _kb;
        private bool _imeChinese;    // what we believe the target's IME mode is (Chinese / English)
        private string _commitKey = " ";
        private bool _fixing;        // proofreading fixes are typed faster

        /// <summary>
        /// Types Chinese the way a person does on a real keyboard: pinyin letters into the IME,
        /// then Space to commit the top candidate. Unknown characters fall back to Unicode.
        /// </summary>
        private void TypePinyinChunk(int start, int len)
        {
            var syl = Pinyin.Of(_u, start, len);
            if (Array.IndexOf(syl, null) >= 0)
            {
                // Some character has no known reading: go one by one so the rest still works.
                for (int k = 0; k < len; k++)
                {
                    if (syl[k] == null) { Wait(CjkBaseMs * Jitter()); Key(_u[start + k]); }
                    else TypePinyin(new[] { syl[k] }, 1);
                }
                return;
            }
            TypePinyin(syl, len);
        }

        private void TypePinyin(IList<string> syllables, int chars)
        {
            string py = Pinyin.Join(syllables);
            SetIme(true);
            int unsampledBefore = _unsampled;
            double perLetter = chars * CjkBaseMs * (_fixing ? 0.45 : 0.8) / Math.Max(1, py.Length);
            for (int k = 0; k < py.Length; k++)
            {
                string letter = py[k].ToString();
                Wait(Clamp(perLetter * _flow * Jitter(), 25, perLetter * 5));
                if (letter != "'" && Chance(_s.TypoRate / 100.0) &&
                    Neighbors.TryGetValue(py[k], out string nb))
                {
                    // fat-fingered a pinyin letter: notice in the candidate bar, delete, retype
                    KeyRaw(nb[_rnd.Next(nb.Length)].ToString());
                    Wait(Rand(250, 700));
                    KeyRaw("\b");
                    Wait(Rand(90, 220));
                }
                KeyRaw(letter);
            }
            Wait(_fixing ? Rand(60, 160) : Rand(150, 550));     // look at the candidates
            KeyRaw(_commitKey);       // Space = top candidate; "2"/"3" = another one
            _unsampled = unsampledBefore + chars;
        }

        /// <summary>Switches the target IME between Chinese and English mode if needed.</summary>
        private void SetIme(bool chinese)
        {
            if (!_kb.IsKeyMode || _imeChinese == chinese) return;
            Gate();
            Wait(Rand(120, 350));
            Gate();
            if (_s.ImeToggleKey == 1) _kb.CtrlSpace(); else _kb.LoneShift();
            _imeChinese = chinese;
            _lastKeyMs = Now;
            Wait(Rand(150, 350));
        }

        // Chinese punctuation → the key that produces it while the IME is in Chinese mode.
        private static readonly Dictionary<string, string> CnPunctKeys = new Dictionary<string, string>
        {
            {"，", ","}, {"。", "."}, {"？", "?"}, {"！", "!"}, {"：", ":"}, {"；", ";"}, {"、", "\\"},
            {"（", "("}, {"）", ")"}, {"《", "<"}, {"》", ">"}, {"“", "\""}, {"”", "\""}, {"‘", "'"}, {"’", "'"},
            {"【", "["}, {"】", "]"}, {"…", "^"}, {"—", "_"}, {"·", "`"}, {"￥", "$"}, {"「", "["}, {"」", "]"},
        };

        // ------------------------------------------------------------------ Latin typos

        private static readonly Dictionary<char, string> Neighbors = new Dictionary<char, string>
        {
            {'q',"wa"},{'w',"qeas"},{'e',"wrsd"},{'r',"etdf"},{'t',"ryfg"},{'y',"tugh"},{'u',"yihj"},
            {'i',"uojk"},{'o',"ipkl"},{'p',"ol"},{'a',"qwsz"},{'s',"awedxz"},{'d',"serfcx"},{'f',"drtgvc"},
            {'g',"ftyhbv"},{'h',"gyujnb"},{'j',"huikmn"},{'k',"jiolm"},{'l',"kop"},{'z',"asx"},{'x',"zsdc"},
            {'c',"xdfv"},{'v',"cfgb"},{'b',"vghn"},{'n',"bhjm"},{'m',"njk"},
            {'1',"2q"},{'2',"13w"},{'3',"24e"},{'4',"35r"},{'5',"46t"},{'6',"57y"},{'7',"68u"},{'8',"79i"},{'9',"80o"},{'0',"9p"}
        };

        private static bool IsTypoCandidate(string u) => u.Length == 1 && char.IsLetterOrDigit(u[0]) && u[0] < 128;

        private static bool IsLatinLetter(string u) => u != null && u.Length == 1 && u[0] < 128 && char.IsLetter(u[0]);

        /// <summary>Types a mistake starting at i, then fixes it. Returns the index to continue from, or -1.</summary>
        private int DoLatinTypo(int i)
        {
            string cur = _u[i];
            string next = i + 1 < _u.Count ? _u[i + 1] : null;
            var typed = new List<string>();
            int follow; // index of the first correct char the typist would continue with

            double r = _rnd.NextDouble();
            if (r < 0.15 && IsLatinLetter(next) && next != cur)
            {
                typed.Add(next); typed.Add(cur);           // transposition: "teh"
                follow = i + 2;
            }
            else if (r < 0.30)
            {
                typed.Add(cur); typed.Add(cur);            // doubled key
                follow = i + 1;
            }
            else if (r < 0.42 && IsLatinLetter(next))
            {
                typed.Add(next);                           // skipped a key
                follow = i + 2;
            }
            else
            {
                char lc = char.ToLowerInvariant(cur[0]);
                if (!Neighbors.TryGetValue(lc, out string nb)) return -1;
                char w = nb[_rnd.Next(nb.Length)];
                if (char.IsUpper(cur[0])) w = char.ToUpperInvariant(w);
                typed.Add(w.ToString());                   // fat-fingered neighbour key
                follow = i + 1;
            }

            // Keep typing a few more characters before noticing (never across a line break).
            int extra = PickNoticeDelay();
            for (int k = 0; k < extra && follow + k < _u.Count; k++)
            {
                string x = _u[follow + k];
                if (x == "\n" || IsCjk(x)) break;
                typed.Add(x);
            }

            // How much of what we typed is accidentally correct?
            int prefix = 0;
            while (prefix < typed.Count && i + prefix < _u.Count && typed[prefix] == _u[i + prefix]) prefix++;
            if (prefix == typed.Count) return -1;           // not actually a mistake

            for (int k = 0; k < typed.Count; k++)
            {
                Wait(LatinDelay(Math.Min(i + k, _u.Count - 1)));
                Key(typed[k]);
            }

            Wait(Rand(250, 750) * (0.8 + 0.4 * _rnd.NextDouble()));      // "wait, that's wrong"
            int back = typed.Count - prefix;
            for (int k = 0; k < back; k++)
            {
                Key("\b");
                Wait(Math.Max(45, LatinBaseMs * 0.6 * Jitter()));
            }
            Wait(Rand(100, 300));
            return i + prefix;
        }

        private int PickNoticeDelay()
        {
            int max = Math.Max(0, _s.MaxNoticeDelay);
            if (max == 0) return 0;
            double r = _rnd.NextDouble();
            int k = r < 0.5 ? 0 : r < 0.75 ? 1 : r < 0.9 ? 2 : 3;
            return Math.Min(k, max);
        }

        // ------------------------------------------------------------------ thinking pauses

        private void AfterUnit(int idx)
        {
            string u = _u[idx];
            string prev = idx > 0 ? _u[idx - 1] : null;
            string next = idx + 1 < _u.Count ? _u[idx + 1] : null;
            if (next == null) return;

            if (u == "\n")
            {
                _pendingSentence = false;
                if (prev == "\n") { Wait(Rand(150, 400)); return; }
                if (next == "\n") return;   // pause after the last of several line breaks
                if (_s.ThinkEnabled)
                {
                    Think(Rand(_s.ParaPauseMin, _s.ParaPauseMax) * 1000, "构思下一段");
                    ResetWordCounter();
                    _sentencesLeft = _rnd.Next(_s.SentEveryMin, Math.Max(_s.SentEveryMin, _s.SentEveryMax) + 1);
                }
                else Wait(Rand(200, 600));
                return;
            }

            if (IsSentenceEnd(idx)) _pendingSentence = true;
            else if (_s.PunctPause && IsClausePunct(u)) Wait(Rand(80, 350));

            if (u == " " && prev != " " && !IsCjk(prev ?? "")) AfterWord(idx);

            if (_pendingSentence && next != " ")
            {
                _pendingSentence = false;
                Wait(Rand(150, 500));
                if (_s.ThinkEnabled && --_sentencesLeft <= 0)
                {
                    Think(Rand(_s.SentPauseMin, _s.SentPauseMax) * 1000, "思考下一句");
                    _sentencesLeft = _rnd.Next(_s.SentEveryMin, Math.Max(_s.SentEveryMin, _s.SentEveryMax) + 1);
                    ResetWordCounter();
                }
            }
        }

        private void AfterWord(int idx)
        {
            DriftFlow();
            if (!_s.ThinkEnabled) return;
            if (--_wordsLeft > 0) return;
            ResetWordCounter();
            if (idx + 1 >= _u.Count) return;
            Think(Rand(_s.WordPauseMin, _s.WordPauseMax) * 1000, "停顿");
        }

        private void ResetWordCounter()
        {
            _wordsLeft = _rnd.Next(_s.WordEveryMin, Math.Max(_s.WordEveryMin, _s.WordEveryMax) + 1);
        }

        private void Think(double ms, string what)
        {
            if (ms <= 0) return;
            Report(EngineState.Thinking, $"{what}…（{ms / 1000:0.0} 秒）");
            Wait(ms);
            Report(EngineState.Running, "正在输入…");
        }

        private bool IsSentenceEnd(int idx)
        {
            string u = _u[idx];
            string next = idx + 1 < _u.Count ? _u[idx + 1] : null;
            if (IsClosing(u))
            {
                string prev = idx > 0 ? _u[idx - 1] : null;
                return prev != null && IsEndPunct(prev, true) && (next == null || !IsClosing(next));
            }
            if (!IsEndPunct(u, false)) return false;
            if (next == null) return true;
            if (IsClosing(next) || IsEndPunct(next, true)) return false;
            if (u == ".") return next == " " || next == "\n" || IsCjk(next);
            return true;
        }

        private static bool IsEndPunct(string u, bool loose) =>
            u == "。" || u == "！" || u == "？" || u == "!" || u == "?" || u == "…" || u == "." || (loose && u == "；");

        private static bool IsClosing(string u) =>
            u == "”" || u == "’" || u == "」" || u == "』" || u == "）" || u == ")" || u == "\"" || u == "'" || u == "》";

        private static bool IsClausePunct(string u) =>
            u == "," || u == ";" || u == ":" || u == "，" || u == "；" || u == "：" || u == "、";

        // ------------------------------------------------------------------ gating / pause handling

        /// <summary>Sleeps for ms while honouring pause/stop and sampling the caret.</summary>
        private void Wait(double ms)
        {
            long end = Now + (long)ms;
            while (true)
            {
                end += Gate();
                long now = Now;
                if (now >= end) return;
                SampleCaret();
                Thread.Sleep((int)Math.Min(20, end - now));
            }
        }

        /// <summary>Blocks while paused. Returns the time spent blocked.</summary>
        private long Gate()
        {
            long t0 = Now;
            bool wasPaused = false, wasManual = false;
            int okStreak = 0;
            string lastMsg = null;

            while (true)
            {
                if (_stop) throw new OperationCanceledException();

                if (_rebaseline)
                {
                    _rebaseline = false;
                    Rebaseline();
                    _autoPaused = false;
                    if (wasPaused) break;
                }

                if (_manualPause)
                {
                    wasPaused = true; wasManual = true;
                    Say(ref lastMsg, EngineState.Paused, "已暂停 — 按 " + Settings.KeyName(_s.StartPauseKey) + " 或点“继续”恢复");
                    Thread.Sleep(40);
                    continue;
                }

                string reason = _s.AutoPause ? CheckTarget() : null;
                if (reason != null)
                {
                    _autoPaused = true; wasPaused = true; okStreak = 0;
                    Say(ref lastMsg, EngineState.Paused, reason);
                    Thread.Sleep(60);
                    continue;
                }

                if (wasPaused && !wasManual && ++okStreak < 4)
                {
                    Thread.Sleep(80);   // make sure the user really is back
                    continue;
                }
                break;
            }

            if (wasPaused)
            {
                _autoPaused = false;
                Report(EngineState.Running, "继续输入…");
                SleepPlain(Rand(500, 1300));   // re-orient before typing again
                CatchUp();
                _lastSampleMs = 0;
            }
            return Now - t0;
        }

        private void Say(ref string last, EngineState st, string msg)
        {
            if (msg == last) return;
            last = msg;
            Report(st, msg);
        }


        /// <summary>
        /// True if both windows belong to the same program — same process, or another process of
        /// the same executable. Remote-control viewers (ToDesk, Sunlogin…) juggle several windows,
        /// sometimes across processes, while the user just sees "the remote screen".
        /// </summary>
        private static bool SameProcess(IntPtr a, IntPtr b)
        {
            if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
            GetWindowThreadProcessId(a, out uint pa);
            GetWindowThreadProcessId(b, out uint pb);
            if (pa == 0 || pb == 0) return false;
            if (pa == pb) return true;
            string na = ProcessName(pa), nb = ProcessName(pb);
            return na != null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<uint, string> NameCache = new Dictionary<uint, string>();

        private static string ProcessName(uint pid)
        {
            lock (NameCache)
            {
                if (NameCache.TryGetValue(pid, out string n)) return n;
                try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) n = p.ProcessName; }
                catch { n = null; }
                if (NameCache.Count > 200) NameCache.Clear();
                NameCache[pid] = n;
                return n;
            }
        }

        private IntPtr _describedHwnd;
        private string _described;

        /// <summary>"process.exe「title」" for status messages (cached per window).</summary>
        private string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "无";
            if (hwnd == _describedHwnd && _described != null) return _described;
            string name = "?";
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName + ".exe";
            }
            catch { }
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (title.Length > 30) title = title.Substring(0, 30) + "…";
            _describedHwnd = hwnd;
            _described = IsOwnWindow(hwnd) ? "HumanTyper 本身" : (title.Length > 0 ? $"{name}「{title}」" : name);
            return _described;
        }

        private string CheckTarget()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != _target)
            {
                // Same program, different window (an app swapping its own windows, ToDesk's viewer
                // re-creating its window…): if nobody touched anything, keep typing there.
                if (!_interfered && fg != IntPtr.Zero && SameProcess(fg, _target))
                {
                    var s2 = CaretProbe.Read(fg);
                    _target = fg; _targetFocus = s2.Focus; _baseline = s2; _unsampled = 0; _caretTracks = false;
                }
                else
                    return "目标窗口不在前台（当前前台：" + Describe(fg) + "）— 切回原输入位置后自动继续，或按 " +
                           Settings.KeyName(_s.StartPauseKey) + " 在当前位置继续";
            }

            var snap = CaretProbe.Read(fg);
            if (_targetFocus != IntPtr.Zero && snap.Focus != IntPtr.Zero && snap.Focus != _targetFocus)
            {
                if (!_interfered)
                {
                    // the program moved focus by itself (no click / key from the user): follow it
                    _targetFocus = snap.Focus; _baseline = snap; _unsampled = 0; _caretTracks = false;
                }
                else
                    return "输入焦点离开了原输入框 — 点回原处后自动继续，或按 " + Settings.KeyName(_s.StartPauseKey) + " 在当前位置继续";
            }

            if (_interfered)
            {
                if (Now - Interlocked.Read(ref _lastUserInputMs) < 450)
                    return "检测到手动操作，已暂停…";
                // A caret we never saw move while typing isn't the real one (e.g. the local shell
                // of a remote-control window), so it can't tell us whether the user came back.
                if (!_baseline.CaretOk || !snap.CaretOk || !_caretTracks)
                {
                    // Can't check where the caret is (remote window, or an app that hides it):
                    // once the user has left everything alone for a few seconds, carry on there.
                    int idleMs = Math.Max(0, _s.IdleResumeSec) * 1000;
                    long quiet = Now - Interlocked.Read(ref _lastUserInputMs);
                    if (idleMs > 0 && quiet >= idleMs)
                    {
                        _interfered = false;
                        _targetFocus = snap.Focus != IntPtr.Zero ? snap.Focus : _targetFocus;
                        _baseline = snap;
                        _unsampled = 0;
                        return null;
                    }
                    string key = Settings.KeyName(_s.StartPauseKey);
                    string how = idleMs > 0
                        ? $"把光标放回原处后停手 {_s.IdleResumeSec} 秒自动继续（还剩 {Math.Max(1, (idleMs - quiet + 999) / 1000)} 秒），或按 {key} 立即继续"
                        : $"把光标放回原处后按 {key} 继续";
                    return (_kb != null && _kb.IsKeyMode ? "检测到手动操作，已暂停 — 远程窗口读不到光标位置，" : "检测到手动操作，已暂停 — 读不到光标位置，") + how;
                }
                if (!snap.Near(_baseline))
                {
                    // The last few keystrokes may not have been sampled yet: accept the caret if it
                    // sits just after the remembered spot on the same line (= the real end).
                    bool justAfter = _unsampled > 0 && Math.Abs(snap.Y - _baseline.Y) <= 3 &&
                                     snap.X > _baseline.X &&
                                     snap.X - _baseline.X <= _unsampled * Math.Max(12, _baseline.H) * 1.5;
                    if (!justAfter)
                        return "光标已离开原位置 — 把光标点回原处后自动继续";
                    _unsampled = 0;
                    _catchUp = 0;
                    _baseline = snap;
                    _interfered = false;
                    return null;
                }
                // The caret is back where we last saw it. If some keystrokes were sent after that
                // sample, the real end of our text is that many characters further right.
                _catchUp = _unsampled;
                _interfered = false;
            }
            return null;
        }

        /// <summary>Moves the caret over characters we typed after the last caret sample.</summary>
        private void CatchUp()
        {
            int n = _catchUp;
            _catchUp = 0;
            if (n == 0) return;
            for (int k = 0; k < Math.Abs(n); k++)
            {
                Tap(n > 0 ? VK_RIGHT : VK_LEFT);
                Thread.Sleep(_rnd.Next(30, 70));
            }
            _unsampled = 0;
            _lastKeyMs = Now;
        }

        private void Rebaseline()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsOwnWindow(fg))
            {
                _target = fg;
                var snap = CaretProbe.Read(fg);
                _targetFocus = snap.Focus;
                _baseline = snap;
                _unsampled = 0;
                _catchUp = 0;
                _interfered = false;
            }
        }

        /// <summary>
        /// Remembers where the caret is while nobody is touching anything. Sampled shortly after
        /// every keystroke so the remembered position is (almost) never behind what was typed;
        /// _unsampled counts the keystrokes sent since the last good sample.
        /// </summary>
        private void SampleCaret()
        {
            long now = Now;
            if (_interfered || now - _lastKeyMs < 25) return;
            if (_unsampled == 0 && now - _lastSampleMs < 150) return;
            _lastSampleMs = now;
            IntPtr fg = GetForegroundWindow();
            if (fg != _target) return;
            var snap = CaretProbe.Read(fg);
            if (_interfered || GetForegroundWindow() != _target) return;
            if (_targetFocus == IntPtr.Zero) _targetFocus = snap.Focus;
            if (snap.Focus == _targetFocus || snap.Focus == IntPtr.Zero)
            {
                _baseline = snap;
                _unsampled = 0;
            }
        }

        private void SleepPlain(double ms)
        {
            long end = Now + (long)ms;
            while (Now < end)
            {
                if (_stop) throw new OperationCanceledException();
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------ keyboard output

        private void Key(string unit)
        {
            if (_kb.IsKeyMode && unit != "\b" && unit != "\n" && unit != "\t" && unit != " ")
            {
                // Real-keyboard mode: Chinese goes through the IME as pinyin, Chinese punctuation
                // needs the IME in Chinese mode, ASCII letters/symbols need it in English mode.
                if (IsCjk(unit))
                {
                    var syl = Pinyin.Of(new[] { unit }, 0, 1);
                    if (syl[0] != null) { TypePinyin(syl, 1); return; }
                }
                else if (CnPunctKeys.TryGetValue(unit, out string key))
                {
                    SetIme(true);
                    KeyRaw(key);
                    return;
                }
                else if (unit.Length == 1 && unit[0] < 0x80 && !char.IsDigit(unit[0]))
                {
                    SetIme(false);
                }
            }
            KeyRaw(unit);
        }

        /// <summary>Sends one key/character as-is (no IME mode handling).</summary>
        private void KeyRaw(string unit)
        {
            Gate();
            SampleCaret();
            if (_interfered) Gate();
            if (unit == "\n") _kb.Enter(_s.NewlineMode == 1);
            else if (unit == "\t") _kb.Tab();
            else if (unit == "\b") _kb.Backspace();
            else if (unit == " ") _kb.Space();
            else _kb.Char(unit);
            _lastKeyMs = Now;
            _unsampled += unit == "\b" ? -1 : 1;
            SettleSample();
        }

        /// <summary>
        /// Right after a keystroke, waits (max ~40 ms) until the target has moved its caret and
        /// remembers the new position, so the "original position" is never a character behind.
        /// </summary>
        private void SettleSample()
        {
            var before = _baseline;
            if (!before.CaretOk || _interfered) return;
            long until = Now + 40;
            while (Now < until)
            {
                Thread.Sleep(4);
                if (_interfered) return;
                IntPtr fg = GetForegroundWindow();
                if (fg != _target) return;
                var snap = CaretProbe.Read(fg);
                if (_interfered || !snap.CaretOk) return;
                if (!snap.Near(before, 0))
                {
                    if (snap.Focus == _targetFocus || snap.Focus == IntPtr.Zero)
                    {
                        _caretTracks = true;      // the caret follows our typing: it's the real one
                        _baseline = snap;
                        _unsampled = 0;
                        _lastSampleMs = Now;
                    }
                    return;
                }
            }
        }

        /// <summary>Arrow keys for caret movement (catch-up / proofreading).</summary>
        private void Tap(ushort vk)
        {
            if (vk == VK_LEFT) _kb.Left();
            else if (vk == VK_RIGHT) _kb.Right();
        }

        // ------------------------------------------------------------------ helpers

        public static List<string> Split(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var list = new List<string>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    list.Add(text.Substring(i, 2));
                    i++;
                }
                else list.Add(text[i].ToString());
            }
            return list;
        }

        public static bool IsCjk(string u)
        {
            if (string.IsNullOrEmpty(u)) return false;
            if (u.Length == 2 && char.IsHighSurrogate(u[0]))
            {
                int cp = char.ConvertToUtf32(u[0], u[1]);
                return cp >= 0x20000 && cp <= 0x3FFFF;
            }
            char c = u[0];
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF)
                   || (c >= 0x3040 && c <= 0x30FF) || (c >= 0xAC00 && c <= 0xD7AF);
        }

        private bool Chance(double p) => _rnd.NextDouble() < p;

        private double Rand(double a, double b)
        {
            if (b < a) { var t = a; a = b; b = t; }
            return a + _rnd.NextDouble() * (b - a);
        }

        private double Gaussian()
        {
            double u1 = 1.0 - _rnd.NextDouble(), u2 = _rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        private void Report(EngineState st, string msg) => StateChanged?.Invoke(_myRun, st, msg);
        [ThreadStatic] private static int _myRun;

        private void Progress(int done, bool force)
        {
            long now = Now;
            if (!force && now - _lastProgressMs < 60) return;
            _lastProgressMs = now;
            ProgressChanged?.Invoke(_myRun, done, _u.Count);
        }

        /// <summary>Rough duration estimate in seconds.</summary>
        public static double Estimate(string text, Settings s)
        {
            var u = Split(text);
            int cjk = 0, latin = 0, words = 0, sentences = 0, paras = 0;
            for (int i = 0; i < u.Count; i++)
            {
                string x = u[i];
                if (IsCjk(x)) cjk++;
                else
                {
                    latin++;
                    if (x == " " && i > 0 && u[i - 1] != " ") words++;
                    if (x == "\n" && i > 0 && u[i - 1] != "\n") paras++;
                }
                if (x == "。" || x == "！" || x == "？" || x == "." || x == "!" || x == "?") sentences++;
            }
            words += cjk / 2;
            double sec = latin * 12.0 / Math.Max(5, s.EnWpm) + cjk * 60.0 / Math.Max(5, s.ZhCpm);
            sec *= 1 + s.TypoRate / 100.0 * 4;
            sec += sentences * 0.35;
            if (s.ThinkEnabled)
            {
                sec += words / Math.Max(1.0, (s.WordEveryMin + s.WordEveryMax) / 2.0) * (s.WordPauseMin + s.WordPauseMax) / 2;
                sec += sentences / Math.Max(1.0, (s.SentEveryMin + s.SentEveryMax) / 2.0) * (s.SentPauseMin + s.SentPauseMax) / 2;
                sec += paras * (s.ParaPauseMin + s.ParaPauseMax) / 2;
            }
            return sec;
        }
    }
}
