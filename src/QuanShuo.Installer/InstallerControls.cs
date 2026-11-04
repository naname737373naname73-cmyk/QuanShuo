using System.ComponentModel;
using QuanShuo.Native.Shell;

namespace QuanShuo.Installer;

/// <summary>深色背景上的复选框。系统 CheckBox 无法改方框颜色。</summary>
internal sealed class GlassCheck : Control
{
    private bool isChecked;
    private bool hovered;

    public GlassCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    [DefaultValue(false)]
    public bool Checked
    {
        get => isChecked;
        set { isChecked = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        NativeTheme.Prepare(e.Graphics);
        float scale = DeviceDpi / 96F;
        float size = 16 * scale;
        var box = new RectangleF(.5F, (Height - size) / 2, size, size);
        using var path = NativeTheme.RoundedRectangle(box, 4 * scale);
        using var fill = new SolidBrush(isChecked ? (Enabled ? NativeTheme.Ice : NativeTheme.Mist) : NativeTheme.Alpha(NativeTheme.Sapphire, 200));
        using var border = new Pen(hovered || (Focused && ShowFocusCues) ? NativeTheme.Ice : NativeTheme.Hairline, scale);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        if (isChecked)
        {
            using var mark = new Pen(NativeTheme.Midnight, 1.75F * scale) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.DrawLines(mark, new[]
            {
                new PointF(box.X + 3.75F * scale, box.Y + 8.25F * scale),
                new PointF(box.X + 6.75F * scale, box.Y + 11.25F * scale),
                new PointF(box.X + 12.25F * scale, box.Y + 5 * scale),
            });
        }

        using var font = NativeTheme.TextFont(13, scale);
        using var text = new SolidBrush(Enabled ? NativeTheme.Moon : NativeTheme.Mist);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        e.Graphics.DrawString(Text, font, text, new RectangleF(24 * scale, 0, Width - 24 * scale, Height), format);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode != Keys.Space) return;
        e.Handled = true;
        Checked = !Checked;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
}

/// <summary>无边框窗口右上角的关闭按钮。</summary>
internal sealed class GlassClose : Control
{
    private bool hovered;

    public GlassClose()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "关闭";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        NativeTheme.Prepare(e.Graphics);
        float scale = DeviceDpi / 96F;
        if (hovered && Enabled)
        {
            using var path = NativeTheme.RoundedRectangle(new RectangleF(0, 0, Width, Height), NativeTheme.ControlRadius * scale);
            using var fill = new SolidBrush(NativeTheme.Alpha(NativeTheme.Moon, 28));
            e.Graphics.FillPath(fill, path);
        }

        float arm = 5 * scale;
        var center = new PointF(Width / 2F, Height / 2F);
        using var pen = new Pen(Enabled ? NativeTheme.Moon : NativeTheme.Mist, NativeTheme.IconStroke * scale);
        e.Graphics.DrawLine(pen, center.X - arm, center.Y - arm, center.X + arm, center.Y + arm);
        e.Graphics.DrawLine(pen, center.X + arm, center.Y - arm, center.X - arm, center.Y + arm);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
}
