namespace QuanShuo.Native.Shell;

/// <summary>轻量托盘菜单：中性色、纯文字快捷键，沿用原生菜单的交互。</summary>
internal sealed class TrayMenuRenderer : ToolStripRenderer
{
    private static bool HighContrast => SystemInformation.HighContrast;
    internal static Color Surface => HighContrast ? SystemColors.Menu : NativeTheme.IsCrabapple ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
    internal static Color Ink => HighContrast ? SystemColors.MenuText : NativeTheme.IsCrabapple ? Color.FromArgb(32, 32, 32) : Color.FromArgb(242, 242, 242);
    private static Color Muted => HighContrast ? SystemColors.MenuText : NativeTheme.IsCrabapple ? Color.FromArgb(102, 102, 102) : Color.FromArgb(170, 170, 170);
    private static Color Hover => HighContrast ? SystemColors.Highlight : NativeTheme.IsCrabapple ? Color.FromArgb(234, 234, 234) : Color.FromArgb(49, 49, 49);
    private static Color Line => HighContrast ? SystemColors.WindowText : NativeTheme.IsCrabapple ? Color.FromArgb(224, 224, 224) : Color.FromArgb(61, 61, 61);
    private static float Scale(ToolStrip? strip) => strip is TrayMenu menu ? menu.MenuScale : (strip?.DeviceDpi ?? 96) / 96F;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Surface);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        float s = Scale(e.ToolStrip);
        var rect = new RectangleF(0, s, e.Item.Width - 1, e.Item.Height - 2 * s);
        NativeTheme.Prepare(e.Graphics);
        using var path = NativeTheme.RoundedRectangle(rect, 4 * s);
        using var fill = new SolidBrush(Hover);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = Scale(e.ToolStrip);
        using var line = new Pen(Line);
        e.Graphics.DrawLine(line, 7 * s, e.Item.Height / 2F, e.Item.Width - 7 * s, e.Item.Height / 2F);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item) { base.OnRenderItemText(e); return; }
        // WinForms 分别回调标题和快捷键；统一绘制一次，保证列对齐。
        if (e.Text != item.Text) return;
        float s = Scale(e.ToolStrip);
        Color text = !item.Enabled ? SystemColors.GrayText : HighContrast && item.Selected ? SystemColors.HighlightText : Ink;
        int keyWidth = 0;
        if (!string.IsNullOrEmpty(item.ShortcutKeyDisplayString))
        {
            using var font = NativeTheme.TextFont(12, s);
            keyWidth = TextRenderer.MeasureText(item.ShortcutKeyDisplayString, font).Width;
            var keyBounds = new Rectangle(item.Width - (int)(12 * s) - keyWidth, 0, keyWidth, item.Height);
            TextRenderer.DrawText(e.Graphics, item.ShortcutKeyDisplayString, font, keyBounds,
                !item.Enabled || HighContrast ? text : Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        var label = new Rectangle((int)(12 * s), 0, item.Width - (int)(24 * s) - (keyWidth > 0 ? keyWidth + (int)(28 * s) : 0), item.Height);
        TextRenderer.DrawText(e.Graphics, item.Text, item.Font, label, text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
