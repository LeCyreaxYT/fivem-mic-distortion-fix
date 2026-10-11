using System.Runtime.InteropServices;

namespace ApolloMicBridge;

internal class SkyWindow : Form
{
    protected readonly Panel Content = new() { Dock = DockStyle.Fill };

    protected SkyWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = SkyTheme.Background;
        ForeColor = SkyTheme.Ink;
        Padding = new Padding(1);
        DoubleBuffered = true;
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = SkyTheme.Background, Padding = new Padding(18, 6, 8, 4) };
        var brand = new Label { Text = "SKY-SYSTEMS", AutoSize = true, ForeColor = SkyTheme.Accent,
            Font = new Font("Segoe UI Semibold", 9), Location = new Point(20, 11) };
        titleBar.Controls.Add(brand);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var (glyph, key) in new[] { (WindowGlyph.Minimize, "Minimize"), (WindowGlyph.Maximize, "Maximize"), (WindowGlyph.Close, "CloseWindow") })
        {
            var button = new SkyButton { Glyph = glyph, AccessibleName = Texts.Get(key), Size = new Size(32, 26), Margin = new Padding(6, 0, 0, 0) };
            button.Click += (_, _) =>
            {
                if (glyph == WindowGlyph.Close)
                    Close();
                else if (glyph == WindowGlyph.Minimize)
                    WindowState = FormWindowState.Minimized;
                else
                {
                    MaximizedBounds = Screen.FromControl(this).WorkingArea;
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                }
            };
            buttons.Controls.Add(button);
        }
        titleBar.Controls.Add(buttons);
        foreach (var control in new Control[] { titleBar, brand })
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left)
                    return;
                ReleaseCapture();
                SendMessageW(Handle, 0x00A1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION: Windows owns move/snap.
            };
            control.DoubleClick += (_, _) =>
            {
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            };
        }
        Controls.Add(Content);
        Controls.Add(titleBar);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(SkyTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x0084 || WindowState != FormWindowState.Normal) // WM_NCHITTEST
            return;
        var position = PointToClient(new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16))));
        var grip = 6 * DeviceDpi / 96;
        var left = position.X < grip;
        var right = position.X >= ClientSize.Width - grip;
        var top = position.Y < grip;
        var bottom = position.Y >= ClientSize.Height - grip;
        m.Result = (IntPtr)(top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : m.Result.ToInt32());
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class SkyDialog : SkyWindow
{
    internal static void Show(IWin32Window? owner, string title, string message)
    {
        using var dialog = new SkyDialog();
        dialog.SuspendLayout();
        dialog.Text = title;
        dialog.Font = new Font("Segoe UI", 10);
        dialog.AutoScaleDimensions = new SizeF(96, 96);
        dialog.AutoScaleMode = AutoScaleMode.Dpi;
        dialog.ClientSize = new Size(880, 650);
        dialog.MinimumSize = new Size(700, 560);
        dialog.StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 16, 26, 22) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 18), AutoSize = true }, 0, 0);
        layout.Controls.Add(new RichTextBox { Text = message, Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = SkyTheme.Background, ForeColor = SkyTheme.Ink, ScrollBars = RichTextBoxScrollBars.None, DetectUrls = false }, 0, 1);
        var close = new SkyButton { Text = Texts.Get("CloseWindow"), Anchor = AnchorStyles.Right, Primary = true, DialogResult = DialogResult.OK };
        layout.Controls.Add(close, 0, 2);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.Content.Controls.Add(layout);
        dialog.ResumeLayout(true);
        dialog.ShowDialog(owner);
    }
}
