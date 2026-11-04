using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SelectionMode = QuanShuo.Core.Models.SelectionMode;

namespace QuanShuo.Native.Capture;

public static class SelectionImageWriter
{
    public static Task SaveAsync(SelectionResult selection, string path, Color freeformFill, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rectangle imageBounds = new(Point.Empty, selection.Capture.Image.Size);
            Rectangle virtualSelection = selection.Bounds;
            virtualSelection.Offset(-selection.Capture.VirtualBounds.Left, -selection.Capture.VirtualBounds.Top);
            Rectangle crop = Rectangle.Intersect(imageBounds, virtualSelection);
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                throw new InvalidOperationException("选区不在可保存的屏幕范围内，请重选。");
            }

            using var output = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.Clear(selection.Mode == SelectionMode.Freeform ? freeformFill : Color.Black);
                if (selection.Mode == SelectionMode.Freeform)
                {
                    using var path = new GraphicsPath();
                    Point[] points = selection.FreeformPath.Select(point => new Point(
                        point.X - selection.Capture.VirtualBounds.Left - crop.X,
                        point.Y - selection.Capture.VirtualBounds.Top - crop.Y)).ToArray();
                    if (points.Length >= 3)
                    {
                        path.AddLines(points);
                        path.CloseFigure();
                        graphics.SetClip(path, CombineMode.Replace);
                    }
                }

                graphics.DrawImage(selection.Capture.Image, new Rectangle(Point.Empty, crop.Size), crop, GraphicsUnit.Pixel);
            }

            output.Save(path, ImageFormat.Png);
        }, cancellationToken);
    }
}
