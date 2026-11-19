using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowSocks.Recovery;

internal sealed class InstallLogDialog : Form
{
    public InstallLogDialog(string title, string details)
    {
        Text = title;
        Font = new Font("Microsoft JhengHei UI", 10);
        ClientSize = new Size(740, 400);
        MinimumSize = new Size(400, 240);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(14);
        var content = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = Color.White, Text = details };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "關閉", AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "複製紀錄", AutoSize = true };
        copy.Click += (_, _) => {
            try { Clipboard.SetText(content.Text); copy.Text = "已複製"; }
            catch (ExternalException) { content.SelectAll(); content.Focus(); MessageBox.Show(this, "剪貼簿忙碌中，請再試一次。", Text); }
        };
        footer.Controls.Add(close);
        footer.Controls.Add(copy);
        Controls.Add(content);
        Controls.Add(footer);
        CancelButton = close;
        Shown += (_, _) => { content.SelectionStart = content.TextLength; content.ScrollToCaret(); };
    }
}
