using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using QuanShuo.Native.Input;

namespace QuanShuo.Native.Capture;

public sealed class ScreenCaptureService
{
    private readonly ForegroundContextService foregroundContext;

    public ScreenCaptureService(ForegroundContextService foregroundContext)
    {
        this.foregroundContext = foregroundContext;
    }

    public FrozenCapture CaptureNow()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("没有读到显示器画面。请检查远程桌面或显卡驱动状态后重试。");
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            return new FrozenCapture
            {
                Image = bitmap,
                VirtualBounds = bounds,
                Metadata = foregroundContext.CaptureMetadata(),
            };
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
