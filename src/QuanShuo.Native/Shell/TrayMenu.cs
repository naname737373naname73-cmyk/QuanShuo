using System.ComponentModel;

namespace QuanShuo.Native.Shell;

/// <summary>托盘自己的排版；仍由原生菜单处理键盘、失焦关闭和屏幕边缘避让。</summary>
internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly TrayMenuRenderer skinRenderer = new();
    private readonly Dictionary<float, Font> fonts = [];
    private float scale = 1;

    public TrayMenu(string captureHotkey, string speechHotkey, Action capture, Action speech,
        Action history, Action settings, Action exit)
    {
        AutoSize = false;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        ShowItemToolTips = false;
        Renderer = skinRenderer;
        AccessibleName = "圈说托盘菜单";
        CaptureItem = CreateItem("圈选截图", capture);
        SpeechItem = CreateItem("说话", speech);
        Items.AddRange([CaptureItem, SpeechItem, new ToolStripSeparator(),
            CreateItem("历史记录", history), CreateItem("设置", settings),
            new ToolStripSeparator(), CreateItem("退出圈说", exit)]);
        NativeTheme.SkinChanged += RefreshTheme;
        UpdateHotkeys(captureHotkey, speechHotkey);
    }

    public ToolStripMenuItem CaptureItem { get; }
    public ToolStripMenuItem SpeechItem { get; }
    internal float MenuScale => scale;

    public void UpdateHotkeys(string capture, string speech)
    {
        // 只显示提示，不在菜单里再次注册快捷键。
        CaptureItem.ShortcutKeyDisplayString = capture;
        SpeechItem.ShortcutKeyDisplayString = speech;
        RefreshAppearance(scale);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        RefreshAppearance(NativeTheme.ScaleAt(Cursor.Position));
        base.OnOpening(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        // 键盘唤起菜单时鼠标可能在别的屏幕，以实际窗口的 DPI 为准。
        RefreshAppearance(DeviceDpi / 96F);
        Rectangle area = Screen.FromHandle(Handle).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        base.OnOpened(e);
    }

    private void RefreshTheme() => RefreshAppearance(scale);

    internal void RefreshAppearance(float nextScale)
    {
        scale = Math.Max(.5F, nextScale);
        SuspendLayout();
        // WinForms 对相同字体赋值可能保留旧实例；按缩放缓存，直到菜单释放再销毁。
        if (!fonts.TryGetValue(scale, out Font? font)) fonts.Add(scale, font = NativeTheme.TextFont(13, scale));
        Font = font;
        BackColor = TrayMenuRenderer.Surface;
        ForeColor = TrayMenuRenderer.Ink;
        Padding = new Padding(D(5), D(6), D(5), D(6));
        using var keyFont = NativeTheme.TextFont(12, scale);
        int width = D(224);
        foreach (ToolStripMenuItem item in Items.OfType<ToolStripMenuItem>())
        {
            int text = TextRenderer.MeasureText(item.Text, Font).Width;
            int key = string.IsNullOrEmpty(item.ShortcutKeyDisplayString) ? 0 :
                TextRenderer.MeasureText(item.ShortcutKeyDisplayString, keyFont).Width + D(28);
            width = Math.Max(width, D(24) + text + key);
        }
        int height = Padding.Vertical;
        foreach (ToolStripItem item in Items)
        {
            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = Padding.Empty;
            int rowHeight = item is ToolStripSeparator ? D(9) : D(32);
            item.Size = new Size(width, rowHeight);
            height += rowHeight;
        }
        Size = new Size(width + Padding.Horizontal, height);
        ResumeLayout(true);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) NativeTheme.SkinChanged -= RefreshTheme;
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (Font font in fonts.Values) font.Dispose();
        }
    }

    private int D(float value) => NativeTheme.Px(value, scale);

    private static ToolStripMenuItem CreateItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }
}
