using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Capture;

internal sealed class StarTrail
{
    private readonly List<Particle> particles = [];
    private int sequence;
    private double lastAdded = double.NegativeInfinity;
    public bool Active => particles.Count > 0;
    public void Clear() { particles.Clear(); lastAdded = double.NegativeInfinity; }

    public bool Add(Point point, float scale, double now)
    {
        // 限制生成频率而不是提前淘汰已画粒子，避免高采样率鼠标留下未擦除的星点。
        if (particles.Count >= 32 || now - lastAdded < .02) return false;
        lastAdded = now;
        int n = sequence++;
        particles.Add(new Particle(new PointF(point.X + (n % 3 - 1) * 4 * scale, point.Y + (n % 5 - 2) * 2 * scale),
            (n % 3 == 0 ? 3.5F : 1.2F) * scale, n % 3 == 0, now));
        return true;
    }

    public IEnumerable<Rectangle> DirtyRectangles() => particles.Select(p => Rectangle.Ceiling(
        new RectangleF(p.Point.X - p.Radius - 2, p.Point.Y - p.Radius - 2, p.Radius * 2 + 4, p.Radius * 2 + 4)));

    public void Advance(double now) => particles.RemoveAll(p => now - p.Born >= .6);

    public void Draw(Graphics graphics, double now)
    {
        foreach (var p in particles)
        {
            float life = Math.Clamp(1 - (float)((now - p.Born) / .6), 0, 1);
            using var brush = new SolidBrush(NativeTheme.Alpha(NativeTheme.IsCrabapple ? NativeTheme.Petal : NativeTheme.Ice, (int)(180 * life)));
            if (p.Star)
            {
                using var path = NativeTheme.Mark(p.Point, p.Radius, p.Radius * .22F);
                graphics.FillPath(brush, path);
            }
            else graphics.FillEllipse(brush, p.Point.X - p.Radius, p.Point.Y - p.Radius, p.Radius * 2, p.Radius * 2);
        }
    }

    private sealed record Particle(PointF Point, float Radius, bool Star, double Born);
}
