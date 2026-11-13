using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HumanTyper
{
    /// <summary>
    /// "This software is free forever" notice: shown at start-up (until dismissed for the current
    /// version) and from the link in the main window. People who paid for it should get a refund
    /// and report the seller; problems go to GitHub or QQ.
    /// </summary>
    internal sealed class FreeNotice : Form
    {
        public const string RepoUrl = "https://github.com/ydnw6/HumanTyper";
        public const string IssuesUrl = RepoUrl + "/issues";
        public const string QQ = "959649446";
        public const string Owner = "ydnw6";
        public const string CopyrightLine = "© 2026 " + Owner;
        /// <summary>The one-sentence version, shown in the footer, title, status bar, overlay and settings file.</summary>
        public const string OneLine = "本软件永久免费开源，付费购买请退款并举报卖家";

        private readonly CheckBox _chkHide;

        /// <summary>True if the user ticked "don't show again for this version".</summary>
        public bool HideForThisVersion => _chkHide.Checked;

        public FreeNotice(bool startup)
        {
            Text = "HumanTyper — 永久免费声明";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;
            Padding = new Padding(0);

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 14) };

            var title = new Label
            {
                Text = "本软件永久免费、开源",
                Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
                ForeColor = Color.FromArgb(46, 125, 50),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
            };
            root.Controls.Add(title);

            root.Controls.Add(Para("HumanTyper 是免费的开源软件（MIT 许可证），不收取任何费用，也没有付费版、会员或激活码。"));

            var warn = new Label
            {
                Text = "如果你是花钱买到的，说明你被骗了：\n请立即向卖家所在平台申请退款，并举报该卖家。",
                Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(198, 40, 40),
                BackColor = Color.FromArgb(255, 235, 238),
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Padding = new Padding(10, 8, 10, 8),
                Margin = new Padding(0, 6, 0, 10),
            };
            root.Controls.Add(warn);

            root.Controls.Add(Para("官方下载、问题反馈、功能建议："));

            var gh = new LinkLabel { Text = "GitHub：" + RepoUrl, AutoSize = true, Margin = new Padding(12, 4, 0, 2) };
            gh.LinkArea = new LinkArea(7, RepoUrl.Length);
            gh.LinkClicked += (s, e) => Open(RepoUrl);
            root.Controls.Add(gh);

            var issues = new LinkLabel { Text = "提交问题（Issues）：" + IssuesUrl, AutoSize = true, Margin = new Padding(12, 2, 0, 2) };
            issues.LinkArea = new LinkArea(issues.Text.Length - IssuesUrl.Length, IssuesUrl.Length);
            issues.LinkClicked += (s, e) => Open(IssuesUrl);
            root.Controls.Add(issues);

            var qqRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(12, 2, 0, 0), WrapContents = false };
            qqRow.Controls.Add(new Label { Text = "QQ：" + QQ, AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            var copy = new Button { Text = "复制 QQ 号", AutoSize = true, FlatStyle = FlatStyle.System };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(QQ); copy.Text = "已复制"; } catch { }
            };
            qqRow.Controls.Add(copy);
            root.Controls.Add(qqRow);

            var bottom = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _chkHide = new CheckBox { Text = "本版本不再自动弹出", AutoSize = true, Visible = startup, Margin = new Padding(0, 8, 12, 0) };
            var ok = new Button { Text = "我知道了", AutoSize = true, MinimumSize = new Size(100, 32), DialogResult = DialogResult.OK };
            bottom.Controls.Add(_chkHide, 0, 0);
            bottom.Controls.Add(ok, 1, 0);
            root.Controls.Add(bottom);

            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = ok;
        }

        private static Label Para(string text) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 2) };

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Version as one number (major·minor·build) for "don't show again for this version".</summary>
        public static int VersionCode
        {
            get
            {
                var v = typeof(FreeNotice).Assembly.GetName().Version;
                return v.Major * 1000000 + v.Minor * 1000 + Math.Max(0, v.Build);
            }
        }
    }
}
