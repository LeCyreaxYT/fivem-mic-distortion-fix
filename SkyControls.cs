using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ApolloMicBridge;

// Desktop rendering of the Sky tablet dark theme: neutral surfaces, compact controls, cyan focus.
internal static class SkyTheme
{
    internal static readonly Color Background = Color.FromArgb(8, 8, 8);
    internal static readonly Color Surface = Color.FromArgb(16, 16, 16);
    internal static readonly Color Field = Color.FromArgb(23, 23, 23);
    internal static readonly Color Line = Color.FromArgb(42, 42, 42);
    internal static readonly Color Border = Color.FromArgb(64, 64, 64);
    internal static readonly Color Ink = Color.FromArgb(245, 245, 245);
    internal static readonly Color Muted = Color.FromArgb(163, 163, 163);
    internal static readonly Color Accent = Color.FromArgb(29, 209, 206);
    internal static readonly Color ErrorInk = Color.FromArgb(255, 96, 118);

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal enum WindowGlyph { None, Minimize, Maximize, Close }

internal sealed class SkyButton : Button
{
    private bool hovered;
    private bool pressed;
    [DefaultValue(false)] public bool Primary { get; set; }
    [DefaultValue(WindowGlyph.None)] public WindowGlyph Glyph { get; set; }

    public SkyButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = SkyTheme.Field;
        ForeColor = SkyTheme.Ink;
        Size = new Size(160, 38);
        Margin = new Padding(0, 0, 10, 0);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var canvas = new SolidBrush(Parent?.BackColor ?? SkyTheme.Background);
        e.Graphics.FillRectangle(canvas, ClientRectangle);
        var scale = DeviceDpi / 96f;
        var background = !Enabled ? SkyTheme.Field : Primary ? SkyTheme.Accent : hovered ? SkyTheme.Line : SkyTheme.Field;
        if (pressed && Enabled)
            background = Primary ? Color.FromArgb(22, 169, 166) : SkyTheme.Border;
        if (Glyph == WindowGlyph.Close && hovered)
            background = Color.FromArgb(131, 35, 49);
        using var shape = SkyTheme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 4 * scale);
        using var fill = new SolidBrush(background);
        e.Graphics.FillPath(fill, shape);
        using var border = new Pen(Focused && ShowFocusCues ? SkyTheme.Accent : Primary && Enabled ? background : SkyTheme.Border, scale);
        e.Graphics.DrawPath(border, shape);
        var color = !Enabled ? SkyTheme.Muted : Primary ? SkyTheme.Background : SkyTheme.Ink;
        if (Glyph == WindowGlyph.None)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        using var stroke = new Pen(color, 1.3f * scale);
        var x = Width / 2f;
        var y = Height / 2f;
        var r = 4 * scale;
        if (Glyph == WindowGlyph.Close)
        {
            e.Graphics.DrawLine(stroke, x - r, y - r, x + r, y + r);
            e.Graphics.DrawLine(stroke, x - r, y + r, x + r, y - r);
        }
        else if (Glyph == WindowGlyph.Minimize)
            e.Graphics.DrawLine(stroke, x - r, y, x + r, y);
        else
            e.Graphics.DrawRectangle(stroke, x - r, y - r, r * 2, r * 2);
    }
}

internal sealed class SkySelect : ComboBox
{
    public SkySelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = SkyTheme.Field;
        ForeColor = SkyTheme.Ink;
        IntegralHeight = false;
        DropDownHeight = 250;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? SkyTheme.Line : SkyTheme.Field);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = e.Index >= 0 ? GetItemText(Items[e.Index]) : "";
        var bounds = Rectangle.Inflate(e.Bounds, -(int)(10 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, text, Font, bounds, Enabled ? SkyTheme.Ink : SkyTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // TableLayoutPanel collapses the hidden ASIO selector to an empty client area.
        if (Width <= 2 || Height <= 2)
            return;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var canvas = new SolidBrush(Parent?.BackColor ?? SkyTheme.Surface);
        graphics.FillRectangle(canvas, ClientRectangle);
        var scale = DeviceDpi / 96f;
        using var shape = SkyTheme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 4 * scale);
        using var fill = new SolidBrush(SkyTheme.Field);
        using var border = new Pen(Focused && Enabled ? SkyTheme.Accent : SkyTheme.Border, scale);
        graphics.FillPath(fill, shape);
        graphics.DrawPath(border, shape);
        var text = SelectedIndex < 0 ? Texts.Get("SelectDevice") : GetItemText(SelectedItem);
        TextRenderer.DrawText(graphics, text, Font, new Rectangle((int)(12 * scale), 0, Width - (int)(42 * scale), Height),
            Enabled ? SkyTheme.Ink : SkyTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var arrow = new Pen(Enabled ? SkyTheme.Muted : SkyTheme.Border, 1.5f * scale);
        var x = Width - 18 * scale;
        var y = Height / 2f;
        graphics.DrawLines(arrow, [new PointF(x - 4 * scale, y - 2 * scale), new PointF(x, y + 2 * scale), new PointF(x + 4 * scale, y - 2 * scale)]);
    }
}

internal sealed class SkyToggle : CheckBox
{
    public SkyToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ForeColor = SkyTheme.Ink;
        Cursor = Cursors.Hand;
        Height = 26;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(text.Width + (int)(48 * DeviceDpi / 96f), Math.Max(text.Height, (int)(26 * DeviceDpi / 96f)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var canvas = new SolidBrush(Parent?.BackColor ?? SkyTheme.Surface);
        e.Graphics.FillRectangle(canvas, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var track = new RectangleF(1, (Height - 18 * scale) / 2, 34 * scale, 18 * scale);
        using var shape = SkyTheme.Rounded(track, 9 * scale);
        using var fill = new SolidBrush(Checked && Enabled ? SkyTheme.Accent : SkyTheme.Border);
        e.Graphics.FillPath(fill, shape);
        using var thumb = new SolidBrush(Checked && Enabled ? SkyTheme.Background : SkyTheme.Ink);
        e.Graphics.FillEllipse(thumb, track.Left + (Checked ? 18 : 3) * scale, track.Top + 3 * scale, 12 * scale, 12 * scale);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(46 * scale), 0, Width - (int)(46 * scale), Height),
            Enabled ? ForeColor : SkyTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(SkyTheme.Accent, scale);
            e.Graphics.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }
}

internal sealed class SkySlider : TrackBar
{
    public SkySlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        TickStyle = TickStyle.None;
        Height = 28;
        ValueChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var canvas = new SolidBrush(Parent?.BackColor ?? SkyTheme.Surface);
        e.Graphics.FillRectangle(canvas, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var inset = 12 * scale;
        var x = inset + (Width - inset * 2) * (Value - Minimum) / (Maximum - Minimum);
        var y = Height / 2f;
        using var track = new Pen(SkyTheme.Border, 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(Enabled ? SkyTheme.Accent : SkyTheme.Muted, 3 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, inset, y, Width - inset, y);
        e.Graphics.DrawLine(active, inset, y, x, y);
        using var thumb = new SolidBrush(Enabled ? SkyTheme.Accent : SkyTheme.Muted);
        using var border = new Pen(SkyTheme.Ink, 1.5f * scale);
        e.Graphics.FillEllipse(thumb, x - 6 * scale, y - 6 * scale, 12 * scale, 12 * scale);
        e.Graphics.DrawEllipse(border, x - 6 * scale, y - 6 * scale, 12 * scale, 12 * scale);
        if (Focused && ShowFocusCues)
            e.Graphics.DrawRectangle(active, 1, 1, Width - 3, Height - 3);
    }
}

internal class SkyProgress : Control
{
    private int value;
    [DefaultValue(0)] public int Value
    {
        get => value;
        set { this.value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    public SkyProgress()
    {
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var canvas = new SolidBrush(SkyTheme.Line);
        e.Graphics.FillRectangle(canvas, ClientRectangle);
        using var fill = new SolidBrush(SkyTheme.Accent);
        e.Graphics.FillRectangle(fill, 0, 0, Width * Value / 100, Height);
    }
}

internal sealed class AudioLevelMeter : Control
{
    private int value;
    [DefaultValue(0)] internal int Value
    {
        get => value;
        set { this.value = Math.Clamp(value, 0, 1000); Invalidate(); }
    }

    public AudioLevelMeter()
    {
        DoubleBuffered = true;
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var canvas = new SolidBrush(SkyTheme.Line);
        e.Graphics.FillRectangle(canvas, ClientRectangle);
        var fraction = value == 0 ? 0 : Math.Clamp((20 * Math.Log10(value / 1000.0) + 60) / 60, 0, 1);
        using var brush = new SolidBrush(value >= 1000 ? SkyTheme.ErrorInk : SkyTheme.Accent);
        e.Graphics.FillRectangle(brush, 0, 0, (int)(Width * fraction), Height);
    }
}

internal sealed class SkyMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => SkyTheme.Field;
    public override Color ImageMarginGradientBegin => SkyTheme.Field;
    public override Color ImageMarginGradientMiddle => SkyTheme.Field;
    public override Color ImageMarginGradientEnd => SkyTheme.Field;
    public override Color MenuItemSelected => SkyTheme.Line;
    public override Color MenuItemBorder => SkyTheme.Border;
    public override Color MenuBorder => SkyTheme.Border;
    public override Color SeparatorDark => SkyTheme.Border;
    public override Color SeparatorLight => SkyTheme.Field;
}
