using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuanShuo.Native.Shell;

/// <summary>题签：印花上面贴的一张纸。<paramref name="Ruled"/> 为 true 时里面再印一道细线框。</summary>
public readonly record struct PaperSlip(Rectangle Bounds, bool Ruled);

/// <summary>
/// 海棠录音窗口的底：圆画那棵树用花的颜色淡淡印满一层（Assets\Crabapple\print.jpg，由
/// frontend/tools/bake-crabapple-print.mjs 生成），上面贴几张题签，字写在题签上。
/// 音量动画每秒重绘 30 次，所以印花和题签按窗口尺寸合成一张缓存起来，之后只贴图。
/// </summary>
public sealed class CrabapplePrint : IDisposable
{
    private const float SlipLineAlpha = 71;
    private const float SlipRuleAlpha = 107;
    private const float SlipRuleInset = 5;
    private readonly Bitmap source;
    private readonly PointF origin;
    private readonly float zoom;
    private Bitmap? cached;
    private Size cachedSize;
    private float cachedScale;
    private PaperSlip[] cachedSlips = [];

    /// <param name="origin">窗口左上角对着印花原图的哪个像素。选在花多的地方，四周露出来的都是花枝。</param>
    /// <param name="zoom">原图一像素对应多少逻辑像素。</param>
    public CrabapplePrint(PointF origin, float zoom)
    {
        using var file = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "Crabapple", "print.jpg"));
        source = new Bitmap(file);
        this.origin = origin;
        this.zoom = zoom;
    }

    public Bitmap Render(Size size, float scale, IReadOnlyList<PaperSlip> slips)
    {
        if (cached is not null && cachedSize == size && cachedScale == scale && cachedSlips.SequenceEqual(slips)) return cached;
        cached?.Dispose();
        cached = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        cachedSize = size;
        cachedScale = scale;
        cachedSlips = [.. slips];
        using var graphics = Graphics.FromImage(cached);
        graphics.Clear(NativeTheme.PrintGround);
        DrawPrint(graphics, size, scale);
        NativeTheme.Prepare(graphics);
        foreach (PaperSlip slip in slips) DrawSlip(graphics, slip, scale);
        return cached;
    }

    public void Dispose()
    {
        cached?.Dispose();
        source.Dispose();
    }

    /// <summary>窗口拉大到印花盖不住时把印花放大，不留空白，也不拼接。</summary>
    private void DrawPrint(Graphics graphics, Size size, float scale)
    {
        float ratio = zoom * scale;
        ratio = Math.Max(ratio, size.Width / Math.Max(1F, source.Width - origin.X));
        ratio = Math.Max(ratio, size.Height / Math.Max(1F, source.Height - origin.Y));
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var from = new RectangleF(origin.X, origin.Y, size.Width / ratio, size.Height / ratio);
        graphics.DrawImage(source, new RectangleF(0, 0, size.Width, size.Height), from, GraphicsUnit.Pixel);
    }

    /// <summary>纸浮起一点：两层很淡的影子，一道花色细边，题签里面再一道细线。</summary>
    private static void DrawSlip(Graphics graphics, PaperSlip slip, float scale)
    {
        Rectangle bounds = slip.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Color shade = Color.FromArgb(110, 60, 50);
        for (int layer = 6; layer >= 1; layer--)
        {
            float spread = layer * 2.2F * scale;
            var halo = RectangleF.Inflate(bounds, spread * .55F, spread * .55F);
            halo.Offset(0, (2 + layer * .9F) * scale);
            using var brush = new SolidBrush(Color.FromArgb(Math.Max(2, 9 - layer), shade));
            graphics.FillRectangle(brush, halo);
        }

        using var near = new SolidBrush(Color.FromArgb(22, shade));
        graphics.FillRectangle(near, bounds.X, bounds.Y + 1.5F * scale, bounds.Width, bounds.Height);
        using var paper = new SolidBrush(NativeTheme.Slip);
        graphics.FillRectangle(paper, bounds);
        graphics.SmoothingMode = SmoothingMode.None;
        using var edge = new Pen(Color.FromArgb((int)SlipLineAlpha, 150, 80, 70));
        graphics.DrawRectangle(edge, bounds.X - 1, bounds.Y - 1, bounds.Width + 1, bounds.Height + 1);
        if (slip.Ruled)
        {
            int inset = (int)MathF.Round(SlipRuleInset * scale);
            using var rule = new Pen(Color.FromArgb((int)SlipRuleAlpha, NativeTheme.Bloom), Math.Max(1, (int)MathF.Round(scale)));
            graphics.DrawRectangle(rule, bounds.X + inset, bounds.Y + inset, bounds.Width - inset * 2 - 1, bounds.Height - inset * 2 - 1);
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
    }
}
