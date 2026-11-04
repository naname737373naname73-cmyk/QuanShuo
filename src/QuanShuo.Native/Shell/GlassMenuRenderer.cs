namespace QuanShuo.Native.Shell;

internal sealed class GlassMenuRenderer : ToolStripProfessionalRenderer
{
    public GlassMenuRenderer() : base(new SkinColors()) { RoundedEdges = false; }

    public static ContextMenuStrip CreateMenu()
    {
        var font = NativeTheme.TextFont(13, NativeTheme.ScaleAt(Cursor.Position));
        var menu = new ContextMenuStrip { Font = font, ShowImageMargin = false };
        Restyle(menu);
        menu.Disposed += (_, _) => font.Dispose();
        return menu;
    }

    /// <summary>托盘菜单一直存在，换皮肤时要重新上色。</summary>
    public static void Restyle(ContextMenuStrip menu)
    {
        menu.Renderer = new GlassMenuRenderer();
        menu.BackColor = NativeTheme.SurfaceRaised;
        menu.ForeColor = NativeTheme.Text;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? NativeTheme.Text : NativeTheme.TextMuted;
        base.OnRenderItemText(e);
    }

    private sealed class SkinColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => NativeTheme.SurfaceRaised;
        public override Color MenuBorder => NativeTheme.IsCrabapple ? NativeTheme.Brass : NativeTheme.Indigo;
        public override Color MenuItemBorder => NativeTheme.SurfaceHover;
        public override Color MenuItemSelected => NativeTheme.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => NativeTheme.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => NativeTheme.SurfaceHover;
        public override Color SeparatorDark => NativeTheme.SurfaceHover;
        public override Color SeparatorLight => NativeTheme.SurfaceRaised;
        public override Color ImageMarginGradientBegin => NativeTheme.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => NativeTheme.SurfaceRaised;
        public override Color ImageMarginGradientEnd => NativeTheme.SurfaceRaised;
    }
}
