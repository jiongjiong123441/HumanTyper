using System;
using System.Threading;
using System.Windows.Forms;

namespace HumanTyper
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var mutex = new Mutex(true, "HumanTyper.SingleInstance", out bool created))
            {
                if (!created)
                {
                    MessageBox.Show("HumanTyper 已经在运行。", "HumanTyper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                    MessageBox.Show(e.Exception.ToString(), "HumanTyper 出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Run(new MainForm());
            }
        }
    }
}
