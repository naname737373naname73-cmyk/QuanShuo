using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QuanShuo.Native.History;

public static class ImageDataService
{
    public static Task<string> ThumbnailAsync(string path, int width, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new Bitmap(path);
            int targetWidth = Math.Clamp(width, 80, 480);
            int targetHeight = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
            using var output = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
            }

            using var memory = new MemoryStream();
            output.Save(memory, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(memory.ToArray());
        }, cancellationToken);
    }

    public static Task<string> FullImageAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }, cancellationToken);
    }
}
