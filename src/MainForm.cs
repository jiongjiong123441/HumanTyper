using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    internal sealed class MainForm : Form
    {
        private const int HK_STARTPAUSE = 1, HK_STOP = 2, HK_RESTART = 3;

        private readonly Settings _settings;
        private readonly TypingEngine _engine = new TypingEngine();
        private readonly InputMonitor _monitor = new InputMonitor();
        private readonly Timer _countdownTimer = new Timer { Interval = 1000 };
        private int _countdownLeft;
        private bool _counting;

        // text
        private RichTextBox _txt;
        private Label _lblStats;

        // speed & typo
        private NumericUpDown _numWpm, _numCpm, _numVar, _numTypo, _numNotice;

        // thinking
        private CheckBox _chkThink, _chkPunct;
        private NumericUpDown _numWEMin, _numWEMax, _numWPMin, _numWPMax;
        private NumericUpDown _numSEMin, _numSEMax, _numSPMin, _numSPMax;
        private NumericUpDown _numPPMin, _numPPMax;

        // control
        private CheckBox _chkAutoPause, _chkMin, _chkTop, _chkProof, _chkClipProof, _chkOcrProof, _chkOverlay;
        private NumericUpDown _numCountdown;
        private ComboBox _cboNewline, _cboMode, _cboImeState, _cboImeToggle;
        private LinkLabel _lnkDriver;
        private Control _rowIme;
        private HotkeyBox _hkStart, _hkStop, _hkRestart;

        // bottom
        private Button _btnStart, _btnPause, _btnStop, _btnRestart;
        private ProgressBar _bar;
        private Label _lblStatus, _lblProgress;

        public MainForm()
        {
            _settings = Settings.Load();
            var ver = typeof(MainForm).Assembly.GetName().Version;
            Text = $"HumanTyper v{ver.Major}.{ver.Minor}.{ver.Build} — 拟人自动打字（永久免费）";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 800);
            MinimumSize = new Size(960, 700);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSettingsToUi();
            UpdateStats();
            SetUiState(EngineState.Idle);

            _engine.StateChanged += (run, st, msg) => BeginInvokeSafe(() => { if (run == _engine.RunId) OnEngineState(st, msg); });
            _engine.ProgressChanged += (run, done, total) => BeginInvokeSafe(() => { if (run == _engine.RunId) OnProgress(done, total); });
            _monitor.UserInput += OnUserInput;
            _monitor.KeyDown += OnRawKeyDown;
            _countdownTimer.Tick += CountdownTick;
        }

        // ================================================================== UI construction

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- text
            var gText = new GroupBox { Text = "要输入的文本（支持中英文）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var textLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            textLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // RichTextBox so typed text can be coloured while typing (plain text only otherwise)
            _txt = new RichTextBox
            {
                Multiline = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                AcceptsTab = true,
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 10.5F),
                DetectUrls = false,
                WordWrap = true,
                HideSelection = false,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txt.TextChanged += (s, e) =>
            {
                if (_painting) return;
                if (_hlActive && !_engine.IsBusy) ClearHighlight();   // edited after a run: drop the colouring
                UpdateStats();
            };
            _txt.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A) { _txt.SelectAll(); e.SuppressKeyPress = true; }
                else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
                {
                    PastePlain();
                    e.SuppressKeyPress = true;
                }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("撤销", null, (s, e) => _txt.Undo());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("剪切", null, (s, e) => { if (!_txt.ReadOnly) _txt.Cut(); });
            menu.Items.Add("复制", null, (s, e) => _txt.Copy());
            menu.Items.Add("粘贴", null, (s, e) => PastePlain());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全选", null, (s, e) => _txt.SelectAll());
            _txt.ContextMenuStrip = menu;
            textLayout.Controls.Add(_txt, 0, 0);

            var textBar = Row();
            var btnPaste = SmallButton("粘贴剪贴板", (s, e) => { if (!_txt.ReadOnly && Clipboard.ContainsText()) _txt.Text = Clipboard.GetText(); });
            var btnOpen = SmallButton("打开文件…", (s, e) => OpenFile());
            var btnClear = SmallButton("清空", (s, e) => { if (!_txt.ReadOnly) _txt.Clear(); });
            _lblStats = new Label { AutoSize = true, Margin = new Padding(12, 8, 0, 0), ForeColor = SystemColors.GrayText };
            var lnkFree = new LinkLabel { Text = "本软件永久免费 · 付费购买请退款举报 · 问题反馈", AutoSize = true, Margin = new Padding(16, 8, 0, 0), LinkColor = Color.FromArgb(198, 40, 40) };
            lnkFree.LinkClicked += (s, e) => ShowFreeNotice(false);
            textBar.Controls.AddRange(new Control[] { btnPaste, btnOpen, btnClear, _lblStats, lnkFree });
            textLayout.Controls.Add(textBar, 0, 1);
            gText.Controls.Add(textLayout);
            root.Controls.Add(gText, 0, 0);

            // ---- settings area
            var settingsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // speed & typo
            var gSpeed = Group("速度与错字");
            var tSpeed = Grid(2);
            _numWpm = Num(5, 300, 0, 1);
            _numCpm = Num(5, 400, 0, 1);
            _numVar = Num(0, 100, 0, 5);
            _numTypo = Num(0, 30, 1, 0.5m);
            _numNotice = Num(0, 3, 0, 1);
            AddRow(tSpeed, "英文速度", Row(_numWpm, Lbl("词/分钟 (WPM)")));
            AddRow(tSpeed, "中文速度", Row(_numCpm, Lbl("字/分钟")));
            AddRow(tSpeed, "节奏波动", Row(_numVar, Lbl("%（越大越不均匀）")));
            AddRow(tSpeed, "错字频率", Row(_numTypo, Lbl("%（每个字出错的概率）")));
            AddRow(tSpeed, "发现错误", Row(Lbl("最多多打"), _numNotice, Lbl("个字后才回删")));
            gSpeed.Controls.Add(tSpeed);
            settingsGrid.Controls.Add(gSpeed, 0, 0);

            // thinking
            var gThink = Group("模拟思考停顿");
            var tThink = Grid(2);
            _chkThink = new CheckBox { Text = "启用思考停顿", AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
            _chkPunct = new CheckBox { Text = "逗号等标点后短暂停顿", AutoSize = true, Margin = new Padding(12, 5, 3, 3) };
            _numWEMin = Num(1, 200, 0, 1); _numWEMax = Num(1, 200, 0, 1);
            _numWPMin = Num(0, 60, 1, 0.1m); _numWPMax = Num(0, 60, 1, 0.1m);
            _numSEMin = Num(1, 50, 0, 1); _numSEMax = Num(1, 50, 0, 1);
            _numSPMin = Num(0, 120, 1, 0.5m); _numSPMax = Num(0, 120, 1, 0.5m);
            _numPPMin = Num(0, 300, 1, 0.5m); _numPPMax = Num(0, 300, 1, 0.5m);
            tThink.Controls.Add(Row(_chkThink, _chkPunct), 0, tThink.RowCount);
            tThink.SetColumnSpan(tThink.Controls[tThink.Controls.Count - 1], 2);
            tThink.RowCount++;
            AddRow(tThink, "打几个词", Row(Lbl("每"), _numWEMin, Lbl("–"), _numWEMax, Lbl("个词，停"), _numWPMin, Lbl("–"), _numWPMax, Lbl("秒")));
            AddRow(tThink, "打几句话", Row(Lbl("每"), _numSEMin, Lbl("–"), _numSEMax, Lbl("句话，停"), _numSPMin, Lbl("–"), _numSPMax, Lbl("秒")));
            AddRow(tThink, "换段落", Row(Lbl("每段结束停"), _numPPMin, Lbl("–"), _numPPMax, Lbl("秒")));
            AddRow(tThink, "", Row(Lbl("中文每 1–4 个字算一个词（模拟输入法整词上屏）", true)));
            gThink.Controls.Add(tThink);
            settingsGrid.Controls.Add(gThink, 1, 0);

            // control
            var gCtl = Group("控制");
            var tCtl = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            _hkStart = new HotkeyBox(); _hkStop = new HotkeyBox(); _hkRestart = new HotkeyBox();
            foreach (var hk in new[] { _hkStart, _hkStop, _hkRestart })
            {
                hk.GotFocus += (s, e) => UnregisterHotkeys();
                hk.LostFocus += (s, e) => { ReadUiToSettings(); RegisterHotkeys(); };
            }
            _numCountdown = Num(0, 30, 0, 1);
            _cboNewline = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Margin = new Padding(3, 4, 3, 3) };
            _cboNewline.Items.AddRange(new object[] { "Enter", "Shift+Enter" });
            _chkAutoPause = new CheckBox { Text = "光标离开原位置/窗口时自动暂停，回到原处自动继续", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkMin = new CheckBox { Text = "开始时最小化本窗口", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkTop = new CheckBox { Text = "窗口置顶", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkOverlay = new CheckBox { Text = "打字时显示进度悬浮窗", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkTop.CheckedChanged += (s, e) => TopMost = _chkTop.Checked;

            tCtl.Controls.Add(Row(Lbl("开始/暂停/继续"), _hkStart, Lbl("   停止"), _hkStop, Lbl("   重新开始"), _hkRestart,
                Lbl("（点击后按键设置，Del 清除）", true)));
            tCtl.SetFlowBreak(tCtl.Controls[tCtl.Controls.Count - 1], true);
            tCtl.Controls.Add(Row(Lbl("倒计时"), _numCountdown, Lbl("秒    换行方式"), _cboNewline, _chkMin, _chkTop, _chkOverlay));
            tCtl.SetFlowBreak(tCtl.Controls[tCtl.Controls.Count - 1], true);
            _chkProof = new CheckBox { Text = "打完后从头核对一遍，有错就用键盘移动光标修改", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
            _chkClipProof = new CheckBox { Text = "剪贴板复制", AutoSize = true, Margin = new Padding(3, 6, 6, 3) };
            _chkOcrProof = new CheckBox { Text = "截图识别（英文）", AutoSize = true, Margin = new Padding(3, 6, 6, 3) };
            _chkProof.CheckedChanged += (s, e) => { _chkClipProof.Enabled = _chkOcrProof.Enabled = _chkProof.Checked; };
            tCtl.Controls.Add(Row(_chkAutoPause, _chkProof));
            tCtl.SetFlowBreak(tCtl.Controls[tCtl.Controls.Count - 1], true);
            tCtl.Controls.Add(Row(Lbl("读不到目标文本时改用"), _chkClipProof, _chkOcrProof,
                Lbl("（复制完会还原剪贴板；截图识别只改两次识别一致的英文错词）", true)));
            tCtl.SetFlowBreak(tCtl.Controls[tCtl.Controls.Count - 1], true);

            // keyboard / remote mode
            _cboMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(3, 4, 3, 3) };
            _cboMode.Items.AddRange(new object[]
            {
                "标准（本机程序，中文直接上屏）",
                "键盘扫描码（远程桌面 / 远控软件 / 虚拟机）",
                "驱动级虚拟键盘（需安装 Interception 驱动）",
            });
            _cboImeState = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105, Margin = new Padding(3, 4, 3, 3) };
            _cboImeState.Items.AddRange(new object[] { "中文模式", "英文模式" });
            _cboImeToggle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(3, 4, 3, 3) };
            _cboImeToggle.Items.AddRange(new object[] { "Shift", "Ctrl+Space" });
            _lnkDriver = new LinkLabel { Text = "一键安装驱动", AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
            _lnkDriver.LinkClicked += (s, e) => OnDriverLink();
            _cboMode.SelectedIndexChanged += (s, e) => UpdateModeUi();
            tCtl.Controls.Add(Row(Lbl("输入方式"), _cboMode, _lnkDriver));
            tCtl.SetFlowBreak(tCtl.Controls[tCtl.Controls.Count - 1], true);
            _rowIme = Row(Lbl("目标输入法开始时为"), _cboImeState, Lbl("  中英切换键"), _cboImeToggle,
                Lbl("（中文会打拼音 + 空格选词，请在目标电脑上使用中文输入法）", true));
            tCtl.Controls.Add(_rowIme);
            gCtl.Controls.Add(tCtl);
            settingsGrid.Controls.Add(gCtl, 0, 1);
            settingsGrid.SetColumnSpan(gCtl, 2);

            root.Controls.Add(settingsGrid, 0, 1);

            // ---- buttons
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 6, Margin = new Padding(0, 6, 0, 0) };
            for (int i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _btnStart = BigButton("开始", (s, e) => BeginCountdown());
            _btnPause = BigButton("暂停", (s, e) => _engine.TogglePause());
            _btnStop = BigButton("停止", (s, e) => StopAll());
            _btnRestart = BigButton("重新开始", (s, e) => Restart(fromHotkey: false));
            _bar = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(10, 10, 6, 10), Height = 22 };
            _lblProgress = new Label { AutoSize = true, Margin = new Padding(0, 12, 0, 0), Text = "0 / 0" };
            bar.Controls.Add(_btnStart, 0, 0);
            bar.Controls.Add(_btnPause, 1, 0);
            bar.Controls.Add(_btnStop, 2, 0);
            bar.Controls.Add(_btnRestart, 3, 0);
            bar.Controls.Add(_bar, 4, 0);
            bar.Controls.Add(_lblProgress, 5, 0);
            root.Controls.Add(bar, 0, 2);

            _lblStatus = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 44,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(6, 4, 6, 4),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = SystemColors.Info,
                ForeColor = SystemColors.InfoText,
            };
            root.Controls.Add(_lblStatus, 0, 3);

            // ---- footer: copyright + the free notice in one line
            var footer = new LinkLabel
            {
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 6, 0, 0),
                ForeColor = SystemColors.GrayText,
                LinkColor = SystemColors.GrayText,
                ActiveLinkColor = SystemColors.HotTrack,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Text = $"{FreeNotice.CopyrightLine} · MIT 开源 · {FreeNotice.OneLine} · github.com/ydnw6/HumanTyper · QQ {FreeNotice.QQ}",
            };
            footer.Links.Clear();
            footer.Links.Add(0, FreeNotice.CopyrightLine.Length, FreeNotice.RepoUrl);
            footer.Links.Add(footer.Text.IndexOf(FreeNotice.OneLine), FreeNotice.OneLine.Length, "notice");
            footer.Links.Add(footer.Text.IndexOf("github.com"), "github.com/ydnw6/HumanTyper".Length, FreeNotice.RepoUrl);
            footer.LinkClicked += (s, e) =>
            {
                if ((string)e.Link.LinkData == "notice") ShowFreeNotice(false);
                else try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); } catch { }
            };
            root.Controls.Add(footer, 0, 4);
        }

        private static GroupBox Group(string text) =>
            new GroupBox { Text = text, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 6) };

        private static TableLayoutPanel Grid(int cols)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = cols, RowCount = 0 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        private static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 6, 3) }, 0, t.RowCount);
            t.Controls.Add(c, 1, t.RowCount);
            t.RowCount++;
        }

        private static FlowLayoutPanel Row(params Control[] items)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
            p.Controls.AddRange(items);
            return p;
        }

        private static Label Lbl(string text, bool gray = false) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(2, 7, 2, 3), ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText };

        private static NumericUpDown Num(decimal min, decimal max, int decimals, decimal inc) =>
            new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = inc, Width = 62, Margin = new Padding(3, 3, 3, 3), TextAlign = HorizontalAlignment.Right };

        private static Button SmallButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
            b.Click += onClick;
            return b;
        }

        private static Button BigButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Width = 96, Height = 34, Margin = new Padding(0, 4, 8, 4) };
            b.Click += onClick;
            return b;
        }

        // ================================================================== settings <-> UI

        private void LoadSettingsToUi()
        {
            var s = _settings;
            SetNum(_numWpm, s.EnWpm); SetNum(_numCpm, s.ZhCpm); SetNum(_numVar, s.Variance);
            SetNum(_numTypo, (decimal)s.TypoRate); SetNum(_numNotice, s.MaxNoticeDelay);
            _chkThink.Checked = s.ThinkEnabled; _chkPunct.Checked = s.PunctPause;
            SetNum(_numWEMin, s.WordEveryMin); SetNum(_numWEMax, s.WordEveryMax);
            SetNum(_numWPMin, (decimal)s.WordPauseMin); SetNum(_numWPMax, (decimal)s.WordPauseMax);
            SetNum(_numSEMin, s.SentEveryMin); SetNum(_numSEMax, s.SentEveryMax);
            SetNum(_numSPMin, (decimal)s.SentPauseMin); SetNum(_numSPMax, (decimal)s.SentPauseMax);
            SetNum(_numPPMin, (decimal)s.ParaPauseMin); SetNum(_numPPMax, (decimal)s.ParaPauseMax);
            _cboMode.SelectedIndex = Math.Max(0, Math.Min(2, s.InputMode));
            _cboImeState.SelectedIndex = s.RemoteImeChinese ? 0 : 1;
            _cboImeToggle.SelectedIndex = s.ImeToggleKey == 1 ? 1 : 0;
            UpdateModeUi();
            _chkAutoPause.Checked = s.AutoPause; _chkProof.Checked = s.Proofread; _chkClipProof.Checked = s.ClipboardProofread; _chkOcrProof.Checked = s.OcrProofread; _chkClipProof.Enabled = _chkOcrProof.Enabled = s.Proofread; _chkMin.Checked = s.MinimizeOnStart; _chkTop.Checked = s.TopMost; _chkOverlay.Checked = s.ShowOverlay;
            SetNum(_numCountdown, s.Countdown);
            _cboNewline.SelectedIndex = s.NewlineMode == 1 ? 1 : 0;
            _hkStart.Hotkey = s.StartPauseKey; _hkStop.Hotkey = s.StopKey; _hkRestart.Hotkey = s.RestartKey;

            foreach (var n in new[] { _numWpm, _numCpm, _numVar, _numTypo, _numWEMin, _numWEMax, _numWPMin, _numWPMax,
                                      _numSEMin, _numSEMax, _numSPMin, _numSPMax, _numPPMin, _numPPMax })
                n.ValueChanged += (sender, e) => UpdateStats();
            _chkThink.CheckedChanged += (sender, e) => UpdateStats();
        }

        private static void SetNum(NumericUpDown n, decimal v) => n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v));

        private void ReadUiToSettings()
        {
            var s = _settings;
            s.EnWpm = (int)_numWpm.Value; s.ZhCpm = (int)_numCpm.Value; s.Variance = (int)_numVar.Value;
            s.TypoRate = (double)_numTypo.Value; s.MaxNoticeDelay = (int)_numNotice.Value;
            s.ThinkEnabled = _chkThink.Checked; s.PunctPause = _chkPunct.Checked;
            s.WordEveryMin = (int)Math.Min(_numWEMin.Value, _numWEMax.Value); s.WordEveryMax = (int)Math.Max(_numWEMin.Value, _numWEMax.Value);
            s.WordPauseMin = (double)Math.Min(_numWPMin.Value, _numWPMax.Value); s.WordPauseMax = (double)Math.Max(_numWPMin.Value, _numWPMax.Value);
            s.SentEveryMin = (int)Math.Min(_numSEMin.Value, _numSEMax.Value); s.SentEveryMax = (int)Math.Max(_numSEMin.Value, _numSEMax.Value);
            s.SentPauseMin = (double)Math.Min(_numSPMin.Value, _numSPMax.Value); s.SentPauseMax = (double)Math.Max(_numSPMin.Value, _numSPMax.Value);
            s.ParaPauseMin = (double)Math.Min(_numPPMin.Value, _numPPMax.Value); s.ParaPauseMax = (double)Math.Max(_numPPMin.Value, _numPPMax.Value);
            s.InputMode = Math.Max(0, _cboMode.SelectedIndex);
            s.RemoteImeChinese = _cboImeState.SelectedIndex != 1;
            s.ImeToggleKey = _cboImeToggle.SelectedIndex == 1 ? 1 : 0;
            s.AutoPause = _chkAutoPause.Checked; s.Proofread = _chkProof.Checked; s.ClipboardProofread = _chkClipProof.Checked; s.OcrProofread = _chkOcrProof.Checked; s.MinimizeOnStart = _chkMin.Checked; s.TopMost = _chkTop.Checked; s.ShowOverlay = _chkOverlay.Checked;
            s.Countdown = (int)_numCountdown.Value;
            s.NewlineMode = _cboNewline.SelectedIndex == 1 ? 1 : 0;
            s.StartPauseKey = _hkStart.Hotkey; s.StopKey = _hkStop.Hotkey; s.RestartKey = _hkRestart.Hotkey;
        }

        private enum DriverState { NotInstalled, NeedsReboot, Ready }

        private static DriverState GetDriverState()
        {
            if (KeyboardOut.Interception.IsAvailable()) return DriverState.Ready;
            return DriverSetup.DriverInstalled && KeyboardOut.Interception.DllPresent ? DriverState.NeedsReboot : DriverState.NotInstalled;
        }

        private void UpdateModeUi(bool setStatus = true)
        {
            if (_cboMode == null || _rowIme == null) return;
            int mode = _cboMode.SelectedIndex;
            _rowIme.Enabled = mode != 0;
            _lnkDriver.Visible = mode == 2;
            if (mode != 2 && !setStatus) return;

            var state = mode == 2 ? GetDriverState() : DriverState.NotInstalled;
            _lnkDriver.Text = state == DriverState.Ready ? "卸载驱动" : state == DriverState.NeedsReboot ? "现在重启" : "一键安装驱动";
            if (_lblStatus == null || !setStatus) return;
            if (mode == 1)
                SetStatus("键盘扫描码模式：发送和实体键盘一样的按键，适用于 ToDesk、向日葵、远程桌面、虚拟机等。中文会以拼音输入，请确认目标电脑的输入法设置。");
            else if (mode == 2)
                SetStatus(state == DriverState.Ready ? "驱动级虚拟键盘已就绪：按键从键盘驱动层发出，和实体键盘无法区分。"
                        : state == DriverState.NeedsReboot ? "键盘驱动已安装，重启电脑后生效。"
                        : "驱动级模式需要先安装键盘驱动 — 点右边的“一键安装驱动”，同意管理员授权后自动装好。");
        }

        private void OnDriverLink()
        {
            switch (GetDriverState())
            {
                case DriverState.Ready: InstallDriver(false); break;
                case DriverState.NeedsReboot: AskReboot("键盘驱动已安装，需要重启电脑才能生效。"); break;
                default: InstallDriver(true); break;
            }
        }

        private async void InstallDriver(bool install)
        {
            string ask = install
                ? "将自动安装 Interception 键盘驱动（开源驱动，非商业用途免费）：\n\n" +
                  "• 从 Interception 官方 GitHub 下载（约 380 KB），并校验文件完整性\n" +
                  "• 接下来 Windows 会询问是否允许更改，请点“是”\n" +
                  "• 装好后需要重启电脑一次\n\n继续安装吗？"
                : "将卸载 Interception 键盘驱动（需要管理员授权，卸载后需重启电脑）。\n\n继续卸载吗？";
            if (MessageBox.Show(this, ask, install ? "一键安装驱动" : "卸载驱动", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes) return;

            _lnkDriver.Enabled = false;
            _btnStart.Enabled = false;
            try
            {
                await Task.Run(() => DriverSetup.EnsureFiles(m => BeginInvokeSafe(() => SetStatus(m))));
                SetStatus("请在弹出的 Windows 授权窗口中点“是”…");
                bool approved = await Task.Run(() => DriverSetup.RunInstaller(install));
                if (!approved)
                {
                    SetStatus("已取消：需要管理员授权才能" + (install ? "安装" : "卸载") + "驱动。");
                    return;
                }
                if (install && !DriverSetup.DriverInstalled)
                {
                    SetStatus("驱动安装没有成功，请重试；如果杀毒软件拦截了，请允许后再试。");
                    return;
                }
                SetStatus(install ? "驱动安装完成，重启电脑后即可使用驱动级输入。" : "驱动已卸载，重启电脑后完全移除。");
                AskReboot(install ? "驱动安装完成，需要重启电脑才能生效。" : "驱动已卸载，需要重启电脑才能完全移除。");
            }
            catch (Exception ex)
            {
                SetStatus((install ? "驱动安装失败：" : "驱动卸载失败：") + ex.Message);
            }
            finally
            {
                _lnkDriver.Enabled = true;
                _btnStart.Enabled = !_engine.IsBusy && !_counting;
                UpdateModeUi(setStatus: false);
            }
        }

        private void AskReboot(string why)
        {
            if (MessageBox.Show(this, why + "\n\n现在重启电脑吗？（请先保存其他程序里的工作）", "需要重启",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                ReadUiToSettings();
                _settings.Save();
                try { DriverSetup.Reboot(); }
                catch (Exception ex) { SetStatus("无法自动重启：" + ex.Message + "，请手动重启电脑。"); }
            }
        }

        private void UpdateStats()
        {
            if (_lblStats == null || _numWpm == null) return;
            ReadUiToSettings();
            var units = TypingEngine.Split(_txt.Text);
            int cjk = 0;
            foreach (var u in units) if (TypingEngine.IsCjk(u)) cjk++;
            double sec = TypingEngine.Estimate(_txt.Text, _settings);
            _lblStats.Text = $"共 {units.Count} 字符（中文 {cjk}）  预计约 {FormatDuration(sec)}";
        }

        private static string FormatDuration(double sec)
        {
            if (sec < 60) return $"{sec:0} 秒";
            if (sec < 3600) return $"{(int)(sec / 60)} 分 {(int)(sec % 60)} 秒";
            return $"{(int)(sec / 3600)} 小时 {(int)(sec % 3600 / 60)} 分";
        }

        private void PastePlain()
        {
            if (_txt.ReadOnly || !Clipboard.ContainsText()) return;
            _txt.SelectedText = Clipboard.GetText();
        }

        // ================================================================== typing progress
        //
        // Three places show where typing has got to:
        //   - the text box: typed text gets a green background, the character being typed is amber
        //     and kept in view;
        //   - a floating always-on-top panel (ProgressOverlay) that never takes focus;
        //   - the taskbar button (green bar, yellow when paused) and the window title (percentage).

        private static readonly Color TypedBack = Color.FromArgb(214, 240, 218);
        private static readonly Color TypedFore = Color.FromArgb(70, 96, 76);
        private static readonly Color CurrentBack = Color.FromArgb(255, 193, 7);

        private List<string> _runUnits = new List<string>();
        private int[] _unitOff = { 0 };       // text-box offset of every unit (+ the end)
        private int _hlEnd;                   // text up to here is coloured as typed
        private bool _hlActive, _painting;
        private ProgressOverlay _overlay;
        private bool _overlayClosed;          // closed with × for this run
        private readonly System.Diagnostics.Stopwatch _activeWatch = new System.Diagnostics.Stopwatch();
        private readonly Timer _overlayHideTimer = new Timer { Interval = 3500 };
        private int _lastDone, _lastTotal;
        private EngineState _lastState = EngineState.Idle;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        private const int WM_SETREDRAW = 0x000B;

        private string BaseTitle
        {
            get
            {
                var ver = typeof(MainForm).Assembly.GetName().Version;
                return $"HumanTyper v{ver.Major}.{ver.Minor}.{ver.Build} — 拟人自动打字（永久免费）";
            }
        }

        /// <summary>Called right before a run starts: remember the units and reset all the displays.</summary>
        private void BeginProgress(string text)
        {
            _runUnits = TypingEngine.Split(text);
            _unitOff = new int[_runUnits.Count + 1];
            for (int i = 0; i < _runUnits.Count; i++) _unitOff[i + 1] = _unitOff[i] + _runUnits[i].Length;

            ClearHighlight();
            _hlActive = true;
            _txt.ReadOnly = true;
            _txt.BackColor = SystemColors.Window;
            _activeWatch.Reset();
            _lastDone = 0; _lastTotal = _runUnits.Count;
            _overlayHideTimer.Stop();
            _overlayClosed = false;

            if (_settings.ShowOverlay)
            {
                EnsureOverlay();
                _overlay.SetText(_runUnits);
                _overlay.SetState(EngineState.Running, "");
                _overlay.SetProgress(0, _runUnits.Count, "");
                if (!_overlay.Visible) _overlay.Show();
                PlaceOverlay();
            }
            else _overlay?.Hide();
            PaintProgress(0);
        }

        private void EnsureOverlay()
        {
            if (_overlay != null && !_overlay.IsDisposed) return;
            _overlay = new ProgressOverlay();
            _overlay.ClosedByUser += () => _overlayClosed = true;
            _overlay.Moved += p => { _settings.OverlayX = p.X; _settings.OverlayY = p.Y; _settings.Save(); };
            _overlayHideTimer.Tick += (s, e) => { _overlayHideTimer.Stop(); if (!_engine.IsBusy) _overlay?.Hide(); };
        }

        /// <summary>Remembered position if it's still on a screen, otherwise top centre of the screen the target is on.</summary>
        private void PlaceOverlay()
        {
            var size = _overlay.Size;
            var p = new Point(_settings.OverlayX, _settings.OverlayY);
            bool onScreen = false;
            foreach (var scr in Screen.AllScreens)
                if (scr.WorkingArea.Contains(new Rectangle(p, new Size(Math.Min(size.Width, 80), Math.Min(size.Height, 40))))) onScreen = true;
            if (!onScreen)
            {
                var wa = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
                p = new Point(wa.Left + (wa.Width - size.Width) / 2, wa.Top + (int)(12 * DeviceDpi / 96f));
            }
            _overlay.Location = p;
        }

        /// <summary>Colours the text box: typed part green, current character amber, scrolled into view.</summary>
        private void PaintProgress(int done)
        {
            if (!_hlActive || _unitOff.Length == 0) return;
            int n = _runUnits.Count;
            done = Math.Max(0, Math.Min(done, n));
            int len = _txt.TextLength;
            int off = Math.Min(_unitOff[done], len);

            _painting = true;
            SendMessage(_txt.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                if (off < _hlEnd) Format(off, _hlEnd - off + 2, _txt.BackColor, _txt.ForeColor);   // went back (restart)
                if (off > _hlEnd || _hlEnd == 0) Format(_hlEnd, off - _hlEnd, TypedBack, TypedFore);
                _hlEnd = off;
                if (done < n) Format(off, Math.Min(_runUnits[done].Length, len - off), CurrentBack, Color.Black);
                _txt.Select(off, 0);
            }
            finally
            {
                SendMessage(_txt.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _txt.Invalidate();
                _painting = false;
            }
            // keep the current spot visible (only scroll when it has left the view)
            var pt = _txt.GetPositionFromCharIndex(Math.Min(off, Math.Max(0, len - 1)));
            int lineH = _txt.Font.Height;
            if (pt.Y < 0 || pt.Y > _txt.ClientSize.Height - lineH * 2) _txt.ScrollToCaret();
        }

        private void Format(int start, int length, Color back, Color fore)
        {
            if (length <= 0 || start < 0 || start >= _txt.TextLength) return;
            _txt.Select(start, Math.Min(length, _txt.TextLength - start));
            _txt.SelectionBackColor = back;
            _txt.SelectionColor = fore;
        }

        private void ClearHighlight()
        {
            _painting = true;
            int ss = _txt.SelectionStart, sl = _txt.SelectionLength;
            SendMessage(_txt.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                _txt.SelectAll();
                _txt.SelectionBackColor = _txt.BackColor;
                _txt.SelectionColor = _txt.ForeColor;
                _txt.Select(ss, sl);
            }
            finally
            {
                SendMessage(_txt.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _txt.Invalidate();
                _painting = false;
            }
            _hlEnd = 0;
            _hlActive = false;
        }

        /// <summary>"速度 82 字/分 · 剩余约 3 分 12 秒" — from the time actually spent typing (pauses excluded).</summary>
        private string SpeedText(int done, int total)
        {
            double sec = _activeWatch.Elapsed.TotalSeconds;
            if (done < 5 || sec < 2) return "";
            double perSec = done / sec;
            string s = $"速度 {perSec * 60:0} 字/分";
            if (done < total) s += $"  ·  剩余约 {FormatDuration((total - done) / perSec)}";
            return s + $"  ·  已用 {FormatDuration(sec)}";
        }

        private void UpdateProgressDisplays()
        {
            int done = _lastDone, total = Math.Max(_lastTotal, 0);
            string speed = SpeedText(done, total);
            int pct = total > 0 ? (int)Math.Round(100.0 * Math.Min(done, total) / total) : 0;
            _lblProgress.Text = $"{done} / {total}（{pct}%）" + (speed.Length > 0 ? "  " + speed.Split('·')[1].Trim() : "");
            if (_overlay != null && _overlay.Visible) _overlay.SetProgress(done, total, speed);

            bool busy = _engine.IsBusy && _lastState != EngineState.Finished && _lastState != EngineState.Stopped;
            Text = busy ? $"{pct}% · " + BaseTitle : BaseTitle;
            TaskbarProgress.Set(Handle,
                !busy ? TaskbarProgress.State.None :
                _lastState == EngineState.Paused ? TaskbarProgress.State.Paused : TaskbarProgress.State.Normal,
                done, total);
        }

        private void OpenFile()
        {
            using (var dlg = new OpenFileDialog { Filter = "文本文件|*.txt;*.md;*.csv;*.log|所有文件|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    byte[] bytes = File.ReadAllBytes(dlg.FileName);
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(bytes); if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1); }
                    catch (DecoderFallbackException) { text = Encoding.GetEncoding(936).GetString(bytes); }
                    _txt.Text = text;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开文件", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        // ================================================================== start / pause / stop / restart

        private bool CanStart(out string text)
        {
            text = _txt.Text;
            if (string.IsNullOrEmpty(text))
            {
                SetStatus("请先输入或粘贴要打的文本。");
                return false;
            }
            return true;
        }

        private void BeginCountdown()
        {
            if (_engine.IsBusy || _counting) return;
            if (!CanStart(out _)) return;
            ReadUiToSettings();
            _settings.Save();

            _countdownLeft = _settings.Countdown;
            if (_countdownLeft <= 0) _countdownLeft = 1;   // need at least a moment to switch window
            _counting = true;
            SetUiState(EngineState.Idle);
            if (_settings.MinimizeOnStart) WindowState = FormWindowState.Minimized;
            SetStatus($"{_countdownLeft} 秒后开始 — 请立刻点击要输入文字的位置（按 {Settings.KeyName(_settings.StopKey)} 取消）");
            _countdownTimer.Start();
        }

        private void CountdownTick(object sender, EventArgs e)
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                SetStatus($"{_countdownLeft} 秒后开始 — 请立刻点击要输入文字的位置（按 {Settings.KeyName(_settings.StopKey)} 取消）");
                return;
            }
            _countdownTimer.Stop();
            _counting = false;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg))
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                SetStatus("没有检测到目标窗口：请在倒计时结束前点击要输入的位置。也可以先点到目标位置，再按 "
                          + Settings.KeyName(_settings.StartPauseKey) + " 直接开始。");
                SetUiState(EngineState.Idle);
                return;
            }
            StartOn(fg);
        }

        private void StartOn(IntPtr target)
        {
            if (!CanStart(out string text)) return;
            ReadUiToSettings();
            _settings.Save();
            _bar.Value = 0;
            BeginProgress(text);
            _engine.Start(text, _settings, target);
            SetUiState(EngineState.Running);
        }

        /// <summary>Hotkey start: type straight into whatever window currently has focus.</summary>
        private void StartFromHotkey()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg)) { BeginCountdown(); return; }
            if (!CanStart(out _)) return;
            StartOn(fg);
        }

        private void StopAll()
        {
            if (_counting)
            {
                _countdownTimer.Stop();
                _counting = false;
                SetStatus("已取消倒计时。");
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                SetUiState(EngineState.Idle);
                return;
            }
            _engine.Stop();
        }

        private void Restart(bool fromHotkey)
        {
            if (_counting) { _countdownTimer.Stop(); _counting = false; }
            if (_engine.IsBusy) _engine.StopAndWait();

            if (fromHotkey) StartFromHotkey();
            else BeginCountdown();
        }

        // ================================================================== engine callbacks

        private void OnEngineState(EngineState st, string msg)
        {
            SetStatus(msg);
            SetUiState(st);
            _lastState = st;
            if (st == EngineState.Running || st == EngineState.Thinking) _activeWatch.Start(); else _activeWatch.Stop();
            if (st == EngineState.Finished) { _bar.Value = _bar.Maximum; _lastDone = _lastTotal; PaintProgress(_runUnits.Count); }
            if (st == EngineState.Finished || st == EngineState.Stopped)
            {
                _txt.ReadOnly = false;
                _txt.BackColor = SystemColors.Window;
                _overlayHideTimer.Stop();
                _overlayHideTimer.Start();          // leave the result up for a moment
            }
            if (_overlay != null && !_overlayClosed && _settings.ShowOverlay)
            {
                _overlay.SetState(st, msg);
                if (!_overlay.Visible && st != EngineState.Finished && st != EngineState.Stopped && st != EngineState.Idle) _overlay.Show();
            }
            UpdateProgressDisplays();
        }

        private void OnProgress(int done, int total)
        {
            _bar.Maximum = Math.Max(1, total);
            _bar.Value = Math.Min(_bar.Maximum, Math.Max(0, done));
            _lastDone = done; _lastTotal = total;
            PaintProgress(done);
            UpdateProgressDisplays();
        }

        private void SetUiState(EngineState st)
        {
            bool busy = _engine.IsBusy && st != EngineState.Finished && st != EngineState.Stopped;
            _btnStart.Enabled = !busy && !_counting;
            _btnPause.Enabled = busy;
            _btnStop.Enabled = busy || _counting;
            _btnRestart.Enabled = true;
            _btnPause.Text = st == EngineState.Paused ? "继续" : "暂停";
            _lblStatus.BackColor = st == EngineState.Paused ? Color.FromArgb(255, 236, 200)
                                 : (st == EngineState.Running || st == EngineState.Thinking) ? Color.FromArgb(220, 245, 222)
                                 : SystemColors.Info;
        }

        private void SetStatus(string msg)
        {
            _lblStatus.Text = msg;
        }

        private void OnUserInput(bool isMouse, uint vk, IntPtr root)
        {
            if (!isMouse && IsHotkeyVk(vk)) return;
            _engine.NotifyUserInput(isMouse, root);
        }

        private bool IsHotkeyVk(uint vk)
        {
            foreach (var k in new[] { _settings.StartPauseKey, _settings.StopKey, _settings.RestartKey })
                if ((uint)(k & Keys.KeyCode) == vk && vk != 0) return true;
            return false;
        }

        // ================================================================== hotkeys

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            var failed = new StringBuilder();
            TryRegister(HK_STARTPAUSE, _settings.StartPauseKey, failed);
            TryRegister(HK_STOP, _settings.StopKey, failed);
            TryRegister(HK_RESTART, _settings.RestartKey, failed);
            if (failed.Length > 0)
                SetStatus("以下快捷键注册失败（可能被其他程序占用），请换一个：" + failed);
        }

        private void TryRegister(int id, Keys k, StringBuilder failed)
        {
            Keys code = k & Keys.KeyCode;
            if (code == Keys.None) return;
            uint mod = MOD_NOREPEAT;
            if ((k & Keys.Control) != 0) mod |= MOD_CONTROL;
            if ((k & Keys.Alt) != 0) mod |= MOD_ALT;
            if ((k & Keys.Shift) != 0) mod |= MOD_SHIFT;
            if (!RegisterHotKey(Handle, id, mod, (uint)code))
                failed.Append(Settings.KeyName(k)).Append(' ');
        }

        private void UnregisterHotkeys()
        {
            if (!IsHandleCreated) return;
            UnregisterHotKey(Handle, HK_STARTPAUSE);
            UnregisterHotKey(Handle, HK_STOP);
            UnregisterHotKey(Handle, HK_RESTART);
        }

        private const int SC_FREE_NOTICE = 0x1F00, WM_SYSCOMMAND = 0x0112;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_FREE_NOTICE)
            {
                ShowFreeNotice(false);
                return;
            }
            if (m.Msg == WM_HOTKEY)
            {
                ExecuteHotkey(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        private readonly long[] _lastHotkeyAt = new long[4];
        private readonly System.Diagnostics.Stopwatch _hotkeyClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Runs a hotkey action. Both RegisterHotKey and raw input may report the same press
        /// (raw input is what still works inside ToDesk/Sunlogin windows), so presses within
        /// 400 ms of each other count once.
        /// </summary>
        private void ExecuteHotkey(int id)
        {
            if (id < 1 || id > 3) return;
            if (_hkStart.Focused || _hkStop.Focused || _hkRestart.Focused) return;   // user is setting a hotkey
            long now = _hotkeyClock.ElapsedMilliseconds;
            if (now - _lastHotkeyAt[id] < 400) return;
            _lastHotkeyAt[id] = now;
            switch (id)
            {
                case HK_STARTPAUSE:
                    if (_engine.IsBusy) _engine.TogglePause();
                    else if (!_counting) StartFromHotkey();
                    break;
                case HK_STOP:
                    StopAll();
                    break;
                case HK_RESTART:
                    Restart(fromHotkey: true);
                    break;
            }
        }

        /// <summary>Raw-input key press (hook thread): recognise our hotkeys even if another app ate them.</summary>
        private void OnRawKeyDown(Keys k)
        {
            int id = k == _settings.StartPauseKey ? HK_STARTPAUSE
                   : k == _settings.StopKey ? HK_STOP
                   : k == _settings.RestartKey ? HK_RESTART : 0;
            if (id != 0 && (k & Keys.KeyCode) != Keys.None) BeginInvokeSafe(() => ExecuteHotkey(id));
        }

        // ================================================================== lifecycle

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            TopMost = _settings.TopMost;
            RegisterHotkeys();
            _monitor.Install();
            // title-bar menu (right-click the title / Alt+Space): about + free notice
            IntPtr sys = GetSystemMenu(Handle, false);
            if (sys != IntPtr.Zero)
            {
                AppendMenu(sys, 0x0800 /* MF_SEPARATOR */, IntPtr.Zero, null);
                AppendMenu(sys, 0, (IntPtr)SC_FREE_NOTICE, "关于 / 永久免费声明…");
            }
            if (_lblStatus.Text.Length == 0)
                SetStatus($"粘贴文本后点“开始”，在倒计时内点击目标输入位置；或先点到目标位置再按 {Settings.KeyName(_settings.StartPauseKey)} 立即开始。"
                          + $"  {Settings.KeyName(_settings.StartPauseKey)} 暂停/继续，{Settings.KeyName(_settings.StopKey)} 停止，{Settings.KeyName(_settings.RestartKey)} 重新开始。  " + FreeNotice.OneLine + "。");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_settings.NoticeHiddenVersion != FreeNotice.VersionCode)
                BeginInvokeSafe(() => ShowFreeNotice(true));
        }

        /// <summary>The "free forever — refund and report if you paid" notice.</summary>
        private void ShowFreeNotice(bool startup)
        {
            using (var dlg = new FreeNotice(startup))
            {
                dlg.ShowDialog(this);
                if (startup && dlg.HideForThisVersion)
                {
                    _settings.NoticeHiddenVersion = FreeNotice.VersionCode;
                    _settings.Save();
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ReadUiToSettings();
            _settings.Save();
            _engine.StopAndWait(1000);
            UnregisterHotkeys();
            _monitor.Dispose();
            _overlay?.Close();
            base.OnFormClosing(e);
        }

        private void BeginInvokeSafe(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }
    }
}
