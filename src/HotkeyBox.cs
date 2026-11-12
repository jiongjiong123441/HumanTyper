using System;
using System.Drawing;
using System.Windows.Forms;

namespace HumanTyper
{
    /// <summary>A read-only text box that captures a key combination when focused.</summary>
    internal sealed class HotkeyBox : TextBox
    {
        private Keys _hotkey;
        public event EventHandler HotkeyChanged;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
            ShortcutsEnabled = false;
            Width = 110;
            TextAlign = HorizontalAlignment.Center;
        }

        public Keys Hotkey
        {
            get => _hotkey;
            set { _hotkey = value; Text = Settings.KeyName(value); }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Text = "请按下按键…";
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Text = Settings.KeyName(_hotkey);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Tab && (keyData & Keys.Modifiers) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (code == Keys.ShiftKey || code == Keys.ControlKey || code == Keys.Menu ||
                code == Keys.LWin || code == Keys.RWin)
                return true;
            if (code == Keys.Escape) { Parent?.SelectNextControl(this, true, true, true, true); return true; }
            if (code == Keys.Back || code == Keys.Delete) { Hotkey = Keys.None; HotkeyChanged?.Invoke(this, EventArgs.Empty); return true; }

            Hotkey = keyData;
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
            Parent?.SelectNextControl(this, true, true, true, true);
            return true;
        }
    }
}
