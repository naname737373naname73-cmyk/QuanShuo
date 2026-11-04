using QuanShuo.Core.Copying;
using QuanShuo.Core.Models;
using SelectionMode = QuanShuo.Core.Models.SelectionMode;
using QuanShuo.Core.Storage;
using QuanShuo.Native.Capture;
using QuanShuo.Native.History;
using QuanShuo.Native.Input;

namespace QuanShuo.Native.Diagnostics;

public static class SmokeCapture
{
    public static async Task<int> RunAsync(string outputRoot)
    {
        string runDirectory = Path.Combine(outputRoot, "smoke data", DateTime.Now.Ticks.ToString());
        Directory.CreateDirectory(runDirectory);
        var store = new RecordStore(runDirectory);
        var foreground = new ForegroundContextService();
        var captureService = new ScreenCaptureService(foreground);
        using var first = captureService.CaptureNow();
        using var second = captureService.CaptureNow();
        int width = Math.Min(480, first.VirtualBounds.Width / 3);
        int height = Math.Min(320, first.VirtualBounds.Height / 3);
        var bounds = new Rectangle(
            first.VirtualBounds.Left + Math.Max(0, (first.VirtualBounds.Width - width) / 2),
            first.VirtualBounds.Top + Math.Max(0, (first.VirtualBounds.Height - height) / 2),
            width,
            height);

        var rectangle = new SelectionResult
        {
            Capture = first,
            Mode = SelectionMode.Rectangle,
            Bounds = bounds,
            Speak = false,
        };
        QuanShuoRecord rectangleRecord = await store.CreateAsync(
            first.Metadata,
            (path, token) => SelectionImageWriter.SaveAsync(rectangle, path, Color.White, token),
            "quanshuo-smoke 矩形抓帧",
            TranscriptionStatus.NotRequested);

        Point center = bounds.Location + new Size(bounds.Width / 2, bounds.Height / 2);
        var freeform = new SelectionResult
        {
            Capture = second,
            Mode = SelectionMode.Freeform,
            Bounds = bounds,
            FreeformPath = [
                new Point(center.X, bounds.Top),
                new Point(bounds.Right, center.Y),
                new Point(center.X, bounds.Bottom),
                new Point(bounds.Left, center.Y),
            ],
            Speak = false,
        };
        QuanShuoRecord freeformRecord = await store.CreateAsync(
            second.Metadata,
            (path, token) => SelectionImageWriter.SaveAsync(freeform, path, Color.White, token),
            "quanshuo-smoke 自由圈选",
            TranscriptionStatus.NotRequested);

        var index = new RecordIndex(store);
        await index.SynchronizeAsync();
        IReadOnlyList<QuanShuoRecord> found = await index.QueryAsync("quanshuo-smoke");
        using var png = new Bitmap(rectangleRecord.ImagePath);
        using var freeformPng = new Bitmap(freeformRecord.ImagePath);
        Color outside = freeformPng.GetPixel(0, 0);
        string copyText = AiCopyFormatter.FormatMany([rectangleRecord, freeformRecord]);
        bool valid = found.Count == 2 && png.Width == width && png.Height == height &&
                     outside is { R: 255, G: 255, B: 255, A: 255 } &&
                     copyText.Contains("[1]", StringComparison.Ordinal) &&
                     copyText.Contains("[截图] \"", StringComparison.Ordinal) &&
                     rectangleRecord.Id != freeformRecord.Id;
        string result = string.Join(Environment.NewLine,
            $"valid={valid}",
            $"records={found.Count}",
            $"png={png.Width}x{png.Height}",
            $"freeformOutside={outside.R},{outside.G},{outside.B},A{outside.A}",
            $"ids={rectangleRecord.Id},{freeformRecord.Id}",
            $"image={rectangleRecord.ImagePath}",
            $"freeform={freeformRecord.ImagePath}",
            copyText);
        string resultPath = Path.Combine(outputRoot, "smoke-result.txt");
        await File.WriteAllTextAsync(resultPath, result);
        return valid ? 0 : 1;
    }
}
