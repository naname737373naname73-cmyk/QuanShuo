using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Recording;

/// <summary>
/// 星湖语音框的底：和星图日历同一块玻璃，加星尘、星轨弧线、双线内凹角边框和月亮。
/// 这些都不随说话变化，画一次存起来。窗口每秒重绘 30 次音量，文字框也要从这里切背景。
/// </summary>
internal sealed class StarlakeGlass : IDisposable
{
    public const float CornerRadius = 22;
    private const float OuterInset = 11;
    private const float OuterScoop = 13;
    private const float InnerInset = 15;
    private const float InnerScoop = 11;
    private const float CornerStarInset = 13;
    private const float CornerStarRadius = 5.5F;
    private const float RingRightOffset = 31;
    private const float RingTop = -95;
    private const float RingOuter = 268;
    private const float RingInner = 196;
    private const int RingTicks = 96;
    private const int LongTickEvery = 8;
    private const float DustPerArea = 1 / 2200F;
    private const int DustSeed = 20261102;
    private const double SynodicMonth = 29.530588;
    private static readonly DateTime NewMoon = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
    private static readonly Color Line = Color.FromArgb(207, 220, 255);
    private static readonly Color Edge = Color.FromArgb(196, 210, 255);
    private static readonly Color ColdStar = Color.FromArgb(230, 238, 255);
    private static readonly Color WarmStar = Color.FromArgb(255, 227, 200);
    private static readonly Color MoonDark = Color.FromArgb(36, 44, 82);
    private static readonly Color MoonLit = Color.FromArgb(239, 230, 207);
    private static readonly Lazy<Bitmap> Night = new(() => Load("night.png"));
    private static readonly Lazy<Bitmap> Dusk = new(() => Load("dusk.png"));
    private Bitmap? cached;
    private (Size Size, float Scale, bool Dusk, PointF Moon) cachedFor;

    /// <summary>月亮的圆心，音量刻度绕着它画。</summary>
    public PointF MoonCenter { get; set; }

    public Bitmap Render(Size size, float scale)
    {
        bool dusk = NativeTheme.IsDusk;
        if (cached is not null && cachedFor == (size, scale, dusk, MoonCenter)) return cached;
        cached?.Dispose();
        cached = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        cachedFor = (size, scale, dusk, MoonCenter);
        using Graphics graphics = Graphics.FromImage(cached);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage((dusk ? Dusk : Night).Value, new Rectangle(Point.Empty, size));
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DrawDust(graphics, size, scale);
        DrawRing(graphics, size, scale);
        DrawFrame(graphics, size, scale);
        DrawMoon(graphics, scale);
        return cached;
    }

    public void Dispose()
    {
        cached?.Dispose();
        cached = null;
    }

    private static Bitmap Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Starlake", name);
        using var source = new Bitmap(path);
        return new Bitmap(source);
    }

    private static void DrawDust(Graphics graphics, Size size, float scale)
    {
        float width = size.Width / scale, height = size.Height / scale;
        int seed = DustSeed;
        float Next()
        {
            seed = (int)(seed * 16807L % int.MaxValue);
            return seed / (float)int.MaxValue;
        }

        int count = (int)(width * height * DustPerArea);
        using var brush = new SolidBrush(Color.Empty);
        for (int i = 0; i < count; i++)
        {
            float x = Next() * width, y = Next() * height;
            float radius = .35F + Next() * .55F;
            brush.Color = Color.FromArgb((int)((.08F + Next() * .2F) * 255), 223, 232, 255);
            graphics.FillEllipse(brush, (x - radius) * scale, (y - radius) * scale, radius * 2 * scale, radius * 2 * scale);
        }
    }

    private static void DrawRing(Graphics graphics, Size size, float scale)
    {
        float centerX = size.Width - RingRightOffset * scale, centerY = RingTop * scale;
        using var line = new Pen(NativeTheme.Alpha(Line, 20), scale);
        using var faint = new Pen(NativeTheme.Alpha(Line, 15), scale);
        float outer = RingOuter * scale, inner = RingInner * scale;
        graphics.DrawEllipse(line, centerX - outer, centerY - outer, outer * 2, outer * 2);
        graphics.DrawEllipse(faint, centerX - inner, centerY - inner, inner * 2, inner * 2);
        for (int i = 0; i < RingTicks; i++)
        {
            double angle = i / (double)RingTicks * Math.PI * 2;
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            float end = outer + (i % LongTickEvery == 0 ? 10 : 5) * scale;
            graphics.DrawLine(line, centerX + cos * outer, centerY + sin * outer, centerX + cos * end, centerY + sin * end);
        }
    }

    private static void DrawFrame(Graphics graphics, Size size, float scale)
    {
        var whole = new RectangleF(0, 0, size.Width, size.Height);
        using (var edge = new Pen(NativeTheme.Alpha(Edge, 38), scale))
        using (GraphicsPath outline = NativeTheme.RoundedRectangle(new RectangleF(.5F, .5F, size.Width - 1, size.Height - 1), CornerRadius * scale))
        {
            graphics.DrawPath(edge, outline);
        }

        // 下边框偏桃色，和地平线的光是一回事
        using var outerBrush = new LinearGradientBrush(whole, Color.Black, Color.Black, LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(92, 223, 231, 255), Color.FromArgb(61, 207, 214, 246), Color.FromArgb(107, 255, 201, 163)],
                Positions = [0, .6F, 1],
            },
        };
        using var innerBrush = new LinearGradientBrush(whole, Color.FromArgb(36, 223, 231, 255), Color.FromArgb(41, 255, 201, 163), LinearGradientMode.Vertical);
        using var outerPen = new Pen(outerBrush, scale);
        using var innerPen = new Pen(innerBrush, scale);
        using GraphicsPath outer = ScoopedFrame(size, OuterInset * scale, OuterScoop * scale);
        using GraphicsPath inner = ScoopedFrame(size, InnerInset * scale, InnerScoop * scale);
        graphics.DrawPath(outerPen, outer);
        graphics.DrawPath(innerPen, inner);

        float inset = CornerStarInset * scale, radius = CornerStarRadius * scale;
        float right = size.Width - inset, bottom = size.Height - inset;
        using var cold = new SolidBrush(NativeTheme.Alpha(ColdStar, 217));
        using var warm = new SolidBrush(NativeTheme.Alpha(WarmStar, 217));
        FillSparkle(graphics, cold, new PointF(inset, inset), radius);
        FillSparkle(graphics, cold, new PointF(right, inset), radius);
        FillSparkle(graphics, warm, new PointF(inset, bottom), radius);
        FillSparkle(graphics, warm, new PointF(right, bottom), radius);
    }

    /// <summary>四边是直线，四角向里凹，留出放星星的位置。</summary>
    private static GraphicsPath ScoopedFrame(Size size, float inset, float scoop)
    {
        float left = inset, top = inset, right = size.Width - inset, bottom = size.Height - inset, diameter = scoop * 2;
        var path = new GraphicsPath();
        path.AddArc(left - scoop, top - scoop, diameter, diameter, 90, -90);
        path.AddLine(left + scoop, top, right - scoop, top);
        path.AddArc(right - scoop, top - scoop, diameter, diameter, 180, -90);
        path.AddLine(right, top + scoop, right, bottom - scoop);
        path.AddArc(right - scoop, bottom - scoop, diameter, diameter, 270, -90);
        path.AddLine(right - scoop, bottom, left + scoop, bottom);
        path.AddArc(left - scoop, bottom - scoop, diameter, diameter, 0, -90);
        path.CloseFigure();
        return path;
    }

    /// <summary>四芒星，四条边向中心收成弧线，和日历上的星一样。</summary>
    private static void FillSparkle(Graphics graphics, Brush brush, PointF center, float radius)
    {
        float k = radius * .1F;
        PointF top = new(center.X, center.Y - radius), right = new(center.X + radius, center.Y);
        PointF bottom = new(center.X, center.Y + radius), left = new(center.X - radius, center.Y);
        using var path = new GraphicsPath();
        void Curve(PointF from, PointF control, PointF to) => path.AddBezier(
            from,
            new PointF(from.X + (control.X - from.X) * 2 / 3, from.Y + (control.Y - from.Y) * 2 / 3),
            new PointF(to.X + (control.X - to.X) * 2 / 3, to.Y + (control.Y - to.Y) * 2 / 3),
            to);
        Curve(top, new PointF(center.X + k, center.Y - k), right);
        Curve(right, new PointF(center.X + k, center.Y + k), bottom);
        Curve(bottom, new PointF(center.X - k, center.Y + k), left);
        Curve(left, new PointF(center.X - k, center.Y - k), top);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    /// <summary>当天真实的月相，和日历用的是同一个算法。</summary>
    private void DrawMoon(Graphics graphics, float scale)
    {
        float radius = 9 * scale, cx = MoonCenter.X, cy = MoonCenter.Y;
        using (var glowPath = new GraphicsPath())
        {
            float glow = radius * 1.6F;
            glowPath.AddEllipse(cx - glow, cy - glow, glow * 2, glow * 2);
            using var halo = new PathGradientBrush(glowPath) { CenterColor = Color.FromArgb(46, 240, 226, 190), SurroundColors = [Color.FromArgb(0, 240, 226, 190)] };
            graphics.FillPath(halo, glowPath);
        }

        using (var dark = new SolidBrush(MoonDark)) graphics.FillEllipse(dark, cx - radius, cy - radius, radius * 2, radius * 2);
        double age = ((DateTime.UtcNow - NewMoon).TotalDays % SynodicMonth + SynodicMonth) % SynodicMonth;
        double phase = age / SynodicMonth;
        float wave = (float)Math.Cos(phase * Math.PI * 2);
        float minor = Math.Max(Math.Abs(wave) * radius, .01F);
        bool waxing = phase < .5;
        using var lit = new GraphicsPath();
        lit.AddArc(cx - radius, cy - radius, radius * 2, radius * 2, -90, waxing ? 180 : -180);
        // 从下往上收回去。凸月的这段弧鼓向暗面，娥眉月的鼓向亮面
        bool bulgesLeft = wave > 0 != waxing;
        lit.AddArc(cx - minor, cy - radius, minor * 2, radius * 2, 90, bulgesLeft ? 180 : -180);
        lit.CloseFigure();
        using var light = new SolidBrush(MoonLit);
        graphics.FillPath(light, lit);
    }
}
