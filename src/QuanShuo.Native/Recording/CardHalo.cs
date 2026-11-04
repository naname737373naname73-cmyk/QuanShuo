using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Recording;

/// <summary>
/// 星湖语音框的大圆角和阴影。系统的圆角只有 8 像素，自己裁窗口又有锯齿，文字框这种系统控件也进不了逐像素透明的窗口。
/// 所以窗口本身比边缘小一圈，这层紧贴在它下面：画一遍同样的玻璃，边缘有抗锯齿，再加阴影。鼠标穿过这一层。
/// </summary>
internal sealed class CardHalo : Form
{
    private const float MarginDp = 56;
    private const float ShadowOffsetDp = 12;
    private const float ShadowSpreadDp = 44;
    private const float ShadowInsetDp = 12;
    private const int ShadowLayers = 30;
    private const int ShadowLayerAlpha = 5;
    private const int WmDpiChanged = 0x02E0;
    private const int ExLayered = 0x00080000;
    private const int ExTransparent = 0x00000020;
    private const int ExToolWindow = 0x00000080;
    private const int ExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint UlwAlpha = 2;
    private static readonly Color ShadowColor = Color.FromArgb(2, 6, 22);

    public CardHalo()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= ExLayered | ExTransparent | ExToolWindow | ExNoActivate;
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // 第一次出现在高分屏上时系统会让它按缩放改大小，改完只剩默认的 300×300。尺寸全由 Draw 决定
        if (m.Msg == WmDpiChanged)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>重画一遍。玻璃或窗口大小变了才需要，移动窗口走 <see cref="Follow"/>。</summary>
    public void Draw(Bitmap glass, Rectangle card, float scale)
    {
        int margin = NativeTheme.Px(MarginDp, scale);
        using var image = new Bitmap(card.Width + margin * 2, card.Height + margin * 2, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float radius = StarlakeGlass.CornerRadius * scale;
            DrawShadow(graphics, new RectangleF(margin, margin + ShadowOffsetDp * scale, card.Width, card.Height), radius, scale);
            using GraphicsPath outline = NativeTheme.RoundedRectangle(new RectangleF(margin, margin, card.Width, card.Height), radius);
            using var texture = new TextureBrush(glass, WrapMode.Clamp);
            texture.TranslateTransform(margin, margin);
            graphics.FillPath(texture, outline);
        }

        Push(image, new Point(card.X - margin, card.Y - margin));
    }

    /// <summary>贴到窗口正下方。两边都是最上层窗口，别的最上层窗口不能夹在中间。</summary>
    public void Follow(Rectangle card, IntPtr cardWindow, float scale)
    {
        int margin = NativeTheme.Px(MarginDp, scale);
        SetWindowPos(Handle, cardWindow, card.X - margin, card.Y - margin, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public void StayBelow(IntPtr cardWindow) => SetWindowPos(Handle, cardWindow, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);

    /// <summary>一层层放大的圆角矩形叠出柔和的边，比真做一次模糊快，拖动改窗口大小时也不卡。</summary>
    private static void DrawShadow(Graphics graphics, RectangleF card, float radius, float scale)
    {
        using var brush = new SolidBrush(Color.FromArgb(ShadowLayerAlpha, ShadowColor));
        float spread = ShadowSpreadDp * scale, inset = ShadowInsetDp * scale;
        for (int layer = 0; layer < ShadowLayers; layer++)
        {
            // 离窗口越远层越稀，外缘才会慢慢消失，不会有一圈看得见的边
            float position = layer / (ShadowLayers - 1F);
            float grow = spread * position * position - inset;
            var bounds = RectangleF.Inflate(card, grow, grow);
            using GraphicsPath path = NativeTheme.RoundedRectangle(bounds, Math.Max(radius + grow, 1));
            graphics.FillPath(brush, path);
        }
    }

    private void Push(Bitmap image, Point location)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = image.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memory, bitmap);
        var size = new NativeSize { Width = image.Width, Height = image.Height };
        var source = new NativePoint();
        var target = new NativePoint { X = location.X, Y = location.Y };
        var blend = new BlendFunction { Operation = 0, Flags = 0, SourceAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, screen, ref target, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte Operation, Flags, SourceAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr target, ref NativePoint position, ref NativeSize size, IntPtr source, ref NativePoint sourcePosition, int key, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
