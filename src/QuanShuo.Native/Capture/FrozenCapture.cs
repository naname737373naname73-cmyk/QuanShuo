using QuanShuo.Core.Models;
using SelectionMode = QuanShuo.Core.Models.SelectionMode;

namespace QuanShuo.Native.Capture;

public sealed class FrozenCapture : IDisposable
{
    public required Bitmap Image { get; init; }
    public required Rectangle VirtualBounds { get; init; }
    public required CaptureMetadata Metadata { get; init; }

    public void Dispose() => Image.Dispose();
}

public sealed class SelectionResult
{
    public required FrozenCapture Capture { get; init; }
    public required SelectionMode Mode { get; init; }
    public required Rectangle Bounds { get; init; }
    public IReadOnlyList<Point> FreeformPath { get; init; } = [];
    public bool Speak { get; init; }
}
