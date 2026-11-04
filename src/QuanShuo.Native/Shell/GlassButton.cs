using System.ComponentModel;

namespace QuanShuo.Native.Shell;

public enum ButtonSkin
{
    Glass,
    /// <summary>星湖语音框的按钮，和主窗口的 primary-button、secondary-button 一样，底是透明的。</summary>
    Starlit,
    /// <summary>星湖语音框的关闭：只有一个细线叉。</summary>
    StarlitClose,
    /// <summary>海棠的主按钮：一方花色的印，里面一道纸色细线。</summary>
    Seal,
    /// <summary>海棠的次要按钮：只有花色的字和下划线，像印在题签上。</summary>
    Ink,
    /// <summary>海棠的关闭：题签角上一个细线叉。</summary>
    PaperClose,
}

public sealed class GlassButton : Control
{
    private const float StarlitRadius = 10;
    private const float StarlitTextSize = 13;
    private const float StarlitKeySize = 11;
    private const float StarlitKeyGap = 7;
    private const float SealTextSize = 15;
    private const float SealKeySize = 12;
    private const float SealKeyGap = 9;
    private const float SealPadding = 16;
    private const float InkTextSize = 14;
    private const float InkPadding = 6;
    /// <summary>思源宋体是可变字体，GDI+ 只取得到最细的字重，所以纸面按钮的字走 GDI。</summary>
    private const TextFormatFlags ExactText = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private static readonly Color StarlitTint = Color.FromArgb(190, 210, 255);
    private static readonly Color StarlitInk = Color.FromArgb(11, 18, 48);
    private bool hovered;
    private bool pressed;
    private bool primary;
    private ButtonSkin skin;

    public GlassButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    [DefaultValue(false)]
    public bool Primary { get => primary; set { primary = value; Invalidate(); } }

    /// <summary>录音窗口按皮肤换的样式。默认是星湖的玻璃样式。</summary>
    [DefaultValue(ButtonSkin.Glass)]
    public ButtonSkin Skin { get => skin; set { skin = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        NativeTheme.Prepare(e.Graphics);
        float scale = DeviceDpi / 96F;
        if (skin == ButtonSkin.Starlit)
        {
            PaintStarlit(e.Graphics, scale);
            return;
        }

        if (skin == ButtonSkin.StarlitClose)
        {
            PaintStarlitClose(e.Graphics, scale);
            return;
        }

        if (skin == ButtonSkin.Seal)
        {
            PaintSeal(e.Graphics, scale);
            return;
        }

        if (skin == ButtonSkin.Ink)
        {
            PaintInk(e.Graphics, scale);
            return;
        }

        if (skin == ButtonSkin.PaperClose)
        {
            PaintPaperClose(e.Graphics, scale);
            return;
        }

        using var path = NativeTheme.RoundedRectangle(new RectangleF(.5F, .5F, Width - 1, Height - 1), NativeTheme.ControlRadius * scale);
        Color fill = primary ? NativeTheme.Ice : NativeTheme.Sapphire;
        if (pressed) fill = primary ? NativeTheme.Mist : NativeTheme.Indigo;
        else if (hovered) fill = primary ? NativeTheme.Moon : NativeTheme.Indigo;
        if (!Enabled) fill = NativeTheme.Sapphire;
        using var background = new SolidBrush(fill);
        // 只在键盘导航时显示焦点框，否则卡片一打开关闭按钮就带着高亮
        using var border = new Pen(Focused && ShowFocusCues ? NativeTheme.Ice : NativeTheme.Hairline);
        using var text = new SolidBrush(!Enabled ? NativeTheme.Mist : primary ? NativeTheme.Midnight : NativeTheme.Moon);
        using var font = NativeTheme.TextFont(13, scale);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);
        e.Graphics.DrawString(Text, font, text, ClientRectangle, format);
    }

    /// <summary>按钮文字最少要多宽。贴到哪个程序是会变的，宽度要跟着文字走。</summary>
    public int MeasureContent(float scale)
    {
        (string label, string? key) = SplitText();
        if (skin is ButtonSkin.Seal or ButtonSkin.Ink)
        {
            bool seal = skin == ButtonSkin.Seal;
            using Font face = NativeTheme.TitleFont(seal ? SealTextSize : InkTextSize, scale);
            int content = TextRenderer.MeasureText(label, face, Size.Empty, ExactText).Width;
            if (seal && key is not null)
            {
                using Font keyFace = NativeTheme.TitleFont(SealKeySize, scale);
                content += (int)MathF.Round(SealKeyGap * scale) + TextRenderer.MeasureText(key, keyFace, Size.Empty, ExactText).Width;
            }

            return content + (int)MathF.Round((seal ? SealPadding : InkPadding) * 2 * scale);
        }

        using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
        using Font font = primary ? NativeTheme.StrongFont(StarlitTextSize, scale) : NativeTheme.TextFont(StarlitTextSize, scale);
        using Font small = NativeTheme.MonoFont(StarlitKeySize, scale);
        float width = graphics.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        if (key is not null) width += StarlitKeyGap * scale + graphics.MeasureString(key, small, PointF.Empty, StringFormat.GenericTypographic).Width;
        return (int)MathF.Ceiling(width);
    }

    /// <summary>“贴到 PowerShell  Enter”：两个空格后面是快捷键，画得小一点。</summary>
    private (string Label, string? Key) SplitText()
    {
        int gap = Text.IndexOf("  ", StringComparison.Ordinal);
        return gap < 0 ? (Text, null) : (Text[..gap], Text[(gap + 2)..].Trim());
    }

    private void PaintStarlit(Graphics graphics, float scale)
    {
        using var path = NativeTheme.RoundedRectangle(new RectangleF(.5F, .5F, Width - 1, Height - 1), StarlitRadius * scale);
        Color ink;
        if (primary)
        {
            Color fill = !Enabled ? NativeTheme.Mix(NativeTheme.Ice, NativeTheme.Midnight, .5F) : pressed ? NativeTheme.Mist : hovered ? NativeTheme.Moon : NativeTheme.Ice;
            using var background = new SolidBrush(fill);
            graphics.FillPath(background, path);
            using var shine = new Pen(Color.FromArgb(71, 255, 255, 255), scale);
            graphics.DrawLine(shine, StarlitRadius * scale, scale, Width - StarlitRadius * scale, scale);
            ink = StarlitInk;
        }
        else
        {
            using var background = new SolidBrush(NativeTheme.Alpha(StarlitTint, pressed ? 38 : hovered ? 26 : 18));
            using var border = new Pen(NativeTheme.Alpha(StarlitTint, hovered ? 77 : 41));
            graphics.FillPath(background, path);
            graphics.DrawPath(border, path);
            ink = Enabled ? NativeTheme.Moon : NativeTheme.Mist;
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(primary ? NativeTheme.Moon : NativeTheme.Ice, scale);
            graphics.DrawPath(focus, path);
        }

        (string label, string? key) = SplitText();
        using Font font = primary ? NativeTheme.StrongFont(StarlitTextSize, scale) : NativeTheme.TextFont(StarlitTextSize, scale);
        using Font small = NativeTheme.MonoFont(StarlitKeySize, scale);
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        format.FormatFlags |= StringFormatFlags.NoWrap;
        float labelWidth = graphics.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        float keyWidth = key is null ? 0 : graphics.MeasureString(key, small, PointF.Empty, StringFormat.GenericTypographic).Width;
        float gap = key is null ? 0 : StarlitKeyGap * scale;
        float x = Math.Max(4 * scale, (Width - labelWidth - gap - keyWidth) / 2);
        using var text = new SolidBrush(ink);
        graphics.DrawString(label, font, text, new RectangleF(x, 0, Math.Max(1, Width - x - keyWidth - gap), Height), format);
        if (key is null) return;
        using var hint = new SolidBrush(NativeTheme.Alpha(ink, 158));
        graphics.DrawString(key, small, hint, new RectangleF(x + labelWidth + gap, 0, keyWidth + 2 * scale, Height), format);
    }

    private void PaintStarlitClose(Graphics graphics, float scale)
    {
        if (hovered || pressed)
        {
            using var round = NativeTheme.RoundedRectangle(new RectangleF(.5F, .5F, Width - 1, Height - 1), NativeTheme.ControlRadius * scale);
            using var background = new SolidBrush(NativeTheme.Alpha(StarlitTint, pressed ? 38 : 26));
            graphics.FillPath(background, round);
        }

        if (Focused && ShowFocusCues)
        {
            using var round = NativeTheme.RoundedRectangle(new RectangleF(.5F, .5F, Width - 1, Height - 1), NativeTheme.ControlRadius * scale);
            using var focus = new Pen(NativeTheme.Ice, scale);
            graphics.DrawPath(focus, round);
        }

        float half = 4.5F * scale, cx = Width / 2F, cy = Height / 2F;
        using var stroke = new Pen(NativeTheme.Alpha(NativeTheme.Moon, hovered ? 235 : 158), 1.2F * scale);
        graphics.DrawLine(stroke, cx - half, cy - half, cx + half, cy + half);
        graphics.DrawLine(stroke, cx + half, cy - half, cx - half, cy + half);
    }

    /// <summary>“复制  Enter”：字是纸色，快捷键小一号、淡一点；放不下时快捷键先让位。</summary>
    private void PaintSeal(Graphics graphics, float scale)
    {
        Color fill = NativeTheme.Bloom;
        if (!Enabled) fill = NativeTheme.Mix(fill, NativeTheme.Slip, .45F);
        else if (pressed) fill = NativeTheme.Mix(fill, NativeTheme.SlipInk, .16F);
        else if (hovered) fill = NativeTheme.Mix(fill, Color.White, .1F);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        using var background = new SolidBrush(fill);
        graphics.FillRectangle(background, ClientRectangle);
        int inset = (int)MathF.Round(3 * scale);
        using var rule = new Pen(NativeTheme.Alpha(NativeTheme.Slip, 128), Math.Max(1, (int)MathF.Round(scale)));
        graphics.DrawRectangle(rule, inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(NativeTheme.SlipInk, Math.Max(1, (int)MathF.Round(scale)));
            graphics.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }

        (string label, string? key) = SplitText();
        using Font face = NativeTheme.TitleFont(SealTextSize, scale);
        using Font keyFace = NativeTheme.TitleFont(SealKeySize, scale);
        Size labelSize = TextRenderer.MeasureText(graphics, label, face, Size.Empty, ExactText);
        int keyWidth = key is null ? 0 : TextRenderer.MeasureText(graphics, key, keyFace, Size.Empty, ExactText).Width;
        int gap = key is null ? 0 : (int)MathF.Round(SealKeyGap * scale);
        int padding = (int)MathF.Round(SealPadding * scale);
        if (labelSize.Width + gap + keyWidth > Width - padding * 2) { key = null; keyWidth = 0; gap = 0; }
        int x = Math.Max(padding / 2, (Width - labelSize.Width - gap - keyWidth) / 2);
        Color ink = Enabled ? NativeTheme.Slip : NativeTheme.Alpha(NativeTheme.Slip, 210);
        var row = new Rectangle(x, 0, Math.Max(1, Width - x - padding / 2), Height);
        TextRenderer.DrawText(graphics, label, face, row, ink, ExactText | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (key is null) return;
        var keyRow = new Rectangle(x + labelSize.Width + gap, 0, keyWidth + 2, Height);
        TextRenderer.DrawText(graphics, key, keyFace, keyRow, NativeTheme.Mix(NativeTheme.Slip, fill, .3F), ExactText | TextFormatFlags.VerticalCenter);
    }

    private void PaintInk(Graphics graphics, float scale)
    {
        if (hovered || pressed)
        {
            using var tint = new SolidBrush(NativeTheme.Alpha(NativeTheme.Bloom, pressed ? 36 : 20));
            using var round = NativeTheme.RoundedRectangle(new RectangleF(0, 0, Width, Height), 3 * scale);
            graphics.FillPath(tint, round);
        }

        using Font face = NativeTheme.TitleFont(InkTextSize, scale);
        Size size = TextRenderer.MeasureText(graphics, Text, face, Size.Empty, ExactText);
        int x = Math.Max(0, (Width - size.Width) / 2), y = (Height - size.Height) / 2;
        Color ink = Enabled ? NativeTheme.Bloom : NativeTheme.Alpha(NativeTheme.Bloom, 120);
        TextRenderer.DrawText(graphics, Text, face, new Rectangle(x, y, Math.Min(size.Width + 2, Width - x), size.Height), ink, ExactText | TextFormatFlags.EndEllipsis);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        int underline = y + size.Height + (int)MathF.Round(2 * scale);
        using var line = new Pen(NativeTheme.Alpha(NativeTheme.Bloom, Focused && ShowFocusCues ? 255 : Enabled ? 90 : 50), Math.Max(1, (int)MathF.Round(scale)));
        graphics.DrawLine(line, x, underline, x + Math.Min(size.Width, Width - x) - 1, underline);
    }

    private void PaintPaperClose(Graphics graphics, float scale)
    {
        if (hovered || pressed || Focused && ShowFocusCues)
        {
            using var tint = new SolidBrush(NativeTheme.Alpha(NativeTheme.Bloom, pressed ? 36 : 20));
            using var round = NativeTheme.RoundedRectangle(new RectangleF(0, 0, Width, Height), 3 * scale);
            graphics.FillPath(tint, round);
        }

        float half = 4.5F * scale, cx = Width / 2F, cy = Height / 2F;
        using var stroke = new Pen(hovered ? NativeTheme.SlipInk : NativeTheme.SlipMuted, 1.2F * scale);
        graphics.DrawLine(stroke, cx - half, cy - half, cx + half, cy + half);
        graphics.DrawLine(stroke, cx + half, cy - half, cx - half, cy + half);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); pressed = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { pressed = true; e.Handled = true; Invalidate(); }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (pressed && e.KeyCode is Keys.Space or Keys.Enter) { pressed = false; e.Handled = true; Invalidate(); OnClick(EventArgs.Empty); }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }
}
