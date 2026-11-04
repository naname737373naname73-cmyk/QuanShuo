using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace QuanShuo.Native.Shell;

/// <summary>两套皮肤。圈选、录音、菜单和托盘都跟着它走，安装器永远是星湖。</summary>
internal enum SkinKind
{
    Starlake,
    Crabapple,
}

internal static class NativeTheme
{
    public static readonly Color Midnight = Color.FromArgb(6, 11, 31);
    public static readonly Color Sapphire = Color.FromArgb(17, 26, 69);
    public static readonly Color Indigo = Color.FromArgb(30, 42, 99);
    public static readonly Color Cobalt = Color.FromArgb(47, 75, 184);
    public static readonly Color Ice = Color.FromArgb(169, 234, 242);
    public static readonly Color Moon = Color.FromArgb(238, 243, 255);
    public static readonly Color Mist = Color.FromArgb(169, 182, 218);
    public static readonly Color Peach = Color.FromArgb(255, 201, 163);
    public static readonly Color Gold = Color.FromArgb(217, 184, 114);
    public static readonly Color Warning = Color.FromArgb(255, 156, 147);
    public static readonly Color Hairline = Color.FromArgb(46, 190, 210, 255);
    // 海棠的配色，取自那幅画
    public static readonly Color Cream = Color.FromArgb(247, 239, 220);
    public static readonly Color Ink = Color.FromArgb(58, 35, 20);
    public static readonly Color Umber = Color.FromArgb(106, 72, 38);
    public static readonly Color Brass = Color.FromArgb(200, 154, 60);
    public static readonly Color Paper = Color.FromArgb(236, 224, 200);
    public static readonly Color Wine = Color.FromArgb(149, 56, 76);
    public static readonly Color Blush = Color.FromArgb(238, 208, 194);
    public static readonly Color Petal = Color.FromArgb(243, 211, 200);
    public static readonly Color Vermilion = Color.FromArgb(168, 46, 34);
    // 海棠的语音框：圆画里那棵树用开出来的花的颜色淡淡印满一层，中间贴一张题签写字。和前端 crabapple.css 的 --print-* 一致
    public static readonly Color PrintGround = Color.FromArgb(245, 238, 226);
    public static readonly Color Slip = Color.FromArgb(252, 248, 239);
    public static readonly Color Bloom = Color.FromArgb(165, 85, 79);
    public static readonly Color SlipInk = Color.FromArgb(46, 35, 33);
    public static readonly Color SlipMuted = Color.FromArgb(122, 92, 85);
    public const float ControlRadius = 8;
    public const float CardRadius = 14;
    public const float PanelRadius = 20;
    public const float SelectionStroke = 1.25F;
    public const float IconStroke = 1.5F;
    public const int DimAlpha = 158;
    private static readonly Lazy<string> titleFamily = new(() => ResolveFont("Noto Serif SC", "Microsoft YaHei UI Light"));
    private static readonly Lazy<string> monoFamily = new(() => ResolveFont("Cascadia Mono", "Consolas"));
    private static readonly Lazy<string> textFamily = new(() => ResolveFont("Segoe UI Variable Text", "Microsoft YaHei UI"));
    private static double? sceneDay;

    public static SkinKind Skin { get; private set; }
    public static bool IsCrabapple => Skin == SkinKind.Crabapple;
    public static event Action? SkinChanged;

    /// <summary>窗口底色，WebView2 加载出第一帧之前看到的就是它。</summary>
    public static Color Canvas => IsCrabapple ? Paper : Midnight;
    public static Color Surface => IsCrabapple ? Cream : Midnight;
    public static Color SurfaceRaised => IsCrabapple ? Cream : Sapphire;
    public static Color SurfaceHover => IsCrabapple ? Blush : Indigo;
    public static Color Text => IsCrabapple ? Ink : Moon;
    public static Color TextMuted => IsCrabapple ? Umber : Mist;
    public static Color Accent => IsCrabapple ? Wine : Ice;
    public static Color AccentHover => IsCrabapple ? Mix(Wine, Color.White, .14F) : Moon;
    public static Color AccentInk => IsCrabapple ? Cream : Midnight;
    public static Color Stroke => IsCrabapple ? Alpha(Brass, 170) : Hairline;
    public static Color Danger => IsCrabapple ? Vermilion : Warning;
    /// <summary>圈选时屏幕压暗的颜色。海棠压成暖褐，线和把手在上面都要看得清。</summary>
    public static Color CaptureDim => IsCrabapple ? Color.FromArgb(46, 30, 16) : Midnight;
    public static Color CaptureLine => IsCrabapple ? Color.FromArgb(232, 190, 92) : Ice;
    public static Color CaptureMark => IsCrabapple ? Petal : Moon;

    /// <summary>
    /// 星湖现在是不是傍晚。主窗口开着时以湖面为准（滑块可以拖），没开时按系统时间，
    /// 规则和前端 query.ts 的 CLOCK_KEYS 一样：过渡的一小时过半才算换了时段。
    /// </summary>
    public static bool IsDusk => sceneDay is { } day ? day < 2.5 : IsDuskAt(DateTime.Now);

    public static bool IsDuskAt(DateTime time)
    {
        double hour = time.Hour + time.Minute / 60D;
        return hour is >= 5.5 and < 18.5;
    }

    /// <summary>湖面当前的时段值，2 是傍晚，3 是夜晚。设置里固定了时段时，主窗口没开也用这个值。</summary>
    public static void UseSceneDay(double? day) => sceneDay = day;

    public static void UseSkin(string? name)
    {
        SkinKind next = name == "crabapple" ? SkinKind.Crabapple : SkinKind.Starlake;
        if (next == Skin) return;
        Skin = next;
        SkinChanged?.Invoke();
    }

    public static Font TitleFont(float dp, float scale = 1) => new(titleFamily.Value, dp * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font MonoFont(float dp, float scale = 1) => new(monoFamily.Value, dp * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font TextFont(float dp, float scale = 1) => new(textFamily.Value, dp * scale, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font StrongFont(float dp, float scale = 1) => new(textFamily.Value, dp * scale, FontStyle.Bold, GraphicsUnit.Pixel);
    public static int Px(float dp, float scale) => (int)MathF.Round(dp * scale);
    public static Color Alpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color);
    public static Color Mix(Color first, Color second, float amount) => Color.FromArgb(
        (int)(first.R + (second.R - first.R) * amount),
        (int)(first.G + (second.G - first.G) * amount),
        (int)(first.B + (second.B - first.B) * amount));

    private static string ResolveFont(string preferred, string fallback)
    {
        using var font = new Font(preferred, 12);
        return string.Equals(font.FontFamily.Name, preferred, StringComparison.OrdinalIgnoreCase) ? preferred : fallback;
    }

    public static float ScaleAt(Point screenPoint)
    {
        IntPtr monitor = MonitorFromPoint(screenPoint, 2);
        return GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x / 96F : 1F;
    }

    public static bool ReduceMotionEnabled
    {
        get
        {
            bool enabled = true;
            return SystemParametersInfo(0x1042, 0, ref enabled, 0) && !enabled;
        }
    }

    public static int SmallIconSize(Point screenPoint) => GetSystemMetricsForDpi(49, (uint)MathF.Round(96 * ScaleAt(screenPoint)));
    public static int ResizeBorder(int dpi) => GetSystemMetricsForDpi(33, (uint)dpi) + GetSystemMetricsForDpi(92, (uint)dpi);

    public static void Prepare(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return path;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath Star(PointF center, float outer, float inner)
    {
        var path = new GraphicsPath();
        var points = new PointF[8];
        for (int i = 0; i < points.Length; i++)
        {
            float angle = i * MathF.PI / 4 - MathF.PI / 2;
            float radius = i % 2 == 0 ? outer : inner;
            points[i] = new PointF(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius);
        }
        path.AddPolygon(points);
        return path;
    }

    /// <summary>一条记录的记号：星湖是四角星，海棠是五瓣小花。</summary>
    public static GraphicsPath Mark(PointF center, float outer, float inner)
    {
        if (!IsCrabapple) return Star(center, outer, inner);
        var path = new GraphicsPath(FillMode.Winding);
        float petal = outer * .42F, reach = outer * .62F;
        for (int i = 0; i < 5; i++)
        {
            float angle = i * MathF.PI * 2 / 5 - MathF.PI / 2;
            path.AddEllipse(center.X + MathF.Cos(angle) * reach - petal, center.Y + MathF.Sin(angle) * reach - petal, petal * 2, petal * 2);
        }

        return path;
    }

    /// <param name="small">圆角用系统的小圆角（4 像素），纸张那种只是去掉尖角。</param>
    public static bool ApplyWindowFrame(IntPtr handle, bool rounded = true, bool small = false)
    {
        int round = rounded ? small ? 3 : 2 : 1;
        int dark = IsCrabapple ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        // 一像素的 DWM 框架保留系统阴影，客户区仍由应用绘制。
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        return DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)) == 0;
    }

    public static void RefreshFrame(IntPtr handle) => SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
    public static void DragWindow(IntPtr handle)
    {
        ReleaseCapture();
        SendMessage(handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint update);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
