using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuanShuo.Native.Shell;
using SelectionMode = QuanShuo.Core.Models.SelectionMode;

namespace QuanShuo.Native.Capture;

public sealed class OverlayForm : Form
{
    private readonly FrozenCapture capture;
    private readonly Bitmap night;
    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 33 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly StarTrail trail = new();
    private readonly List<Point> freeformPoints = [];
    private readonly List<ToolButton> toolButtons = [];
    private readonly (Rectangle Bounds, float Scale)[] monitors;
    private SelectionMode mode;
    private SelectionPhase phase = SelectionPhase.WaitingForSelection;
    private Point anchorVirtual;
    private Point oppositeVirtual;
    private Point mouseClient;
    private Rectangle selectionVirtual;
    private Rectangle activeMonitor;
    private Rectangle toolbar;
    private Rectangle hintBounds;
    private Rectangle errorBounds;
    private float scale = 1;
    private int hoveredButton = -1;
    private bool operationInProgress;
    private bool reduceMotion;
    private string? inlineError;
    private double hintFadeStarted = -1;
    private float hintOpacity = 1;
    private GraphicsPath? rawPath;
    private GraphicsPath? smoothPath;

    public OverlayForm(FrozenCapture capture, SelectionMode initialMode)
    {
        this.capture = capture;
        mode = initialMode;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = capture.VirtualBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Opaque, true);
        BackColor = NativeTheme.CaptureDim;
        Cursor = Cursors.Cross;
        monitors = Screen.AllScreens.Select(screen => (screen.Bounds, NativeTheme.ScaleAt(screen.Bounds.Location))).ToArray();
        mouseClient = ToClientPoint(Cursor.Position);
        UpdateDisplay();
        // 冻结画面保持只读，压暗与星点合成到另一张图，保存路径永远接触不到装饰。
        night = new Bitmap(capture.Image.Width, capture.Image.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(night))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(capture.Image, Point.Empty);
            graphics.CompositingMode = CompositingMode.SourceOver;
            using var dim = new SolidBrush(NativeTheme.Alpha(NativeTheme.CaptureDim, NativeTheme.DimAlpha));
            graphics.FillRectangle(dim, new Rectangle(Point.Empty, night.Size));
            var random = new Random(7419);
            // 星点是星湖的，海棠的压暗层就是一层暖褐
            foreach (var monitor in NativeTheme.IsCrabapple ? [] : monitors)
            {
                Rectangle bounds = ToClient(monitor.Bounds);
                int count = Math.Clamp((int)(bounds.Width * (long)bounds.Height / (monitor.Scale * monitor.Scale * 24000)), 30, 120);
                for (int i = 0; i < count; i++)
                {
                    float size = (i % 5 == 0 ? 1.5F : 1F) * monitor.Scale;
                    using var star = new SolidBrush(NativeTheme.Alpha(NativeTheme.Moon, random.Next(30, 105)));
                    graphics.FillEllipse(star, random.Next(bounds.Left, bounds.Right), random.Next(bounds.Top, bounds.Bottom), size, size);
                }
            }
        }
        animationTimer.Tick += (_, _) => Animate();
    }

    public bool QuietMode;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<SelectionResult, Task<bool>>? ConfirmAsync { get; set; }

    private bool MotionAllowed => !QuietMode && !reduceMotion;
    private int D(float dp) => NativeTheme.Px(dp, scale);
    private bool HasSelection => selectionVirtual.Width > 0 && selectionVirtual.Height > 0;
    private bool CornersAnimate => mode == SelectionMode.Rectangle && HasSelection && !operationInProgress;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        anchorVirtual = Cursor.Position;
        reduceMotion = NativeTheme.ReduceMotionEnabled;
        // 覆盖层没有文字输入。中文输入法开着时会吃掉 Space 和 Tab，这里直接解除输入法关联。
        ImmAssociateContext(Handle, IntPtr.Zero);
        Activate();
        SetCapture(Handle);
        SyncAnimation();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animationTimer.Dispose();
            rawPath?.Dispose();
            smoothPath?.Dispose();
            night.Dispose();
            capture.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        animationTimer.Stop();
        if (Capture) ReleaseCapture();
        base.OnFormClosed(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // 覆盖层必须与冻结图共用物理坐标，不能让 WinForms 随单块显示器缩放整个窗口。
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x001A && IsHandleCreated)
        {
            reduceMotion = NativeTheme.ReduceMotionEnabled;
            SyncAnimation();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (operationInProgress || e.Button != MouseButtons.Left) return;
        Point virtualPoint = ToVirtual(e.Location);
        if (phase == SelectionPhase.Selected)
        {
            ToolButton? button = toolButtons.FirstOrDefault(candidate => candidate.Bounds.Contains(e.Location));
            if (button is not null) { button.Action(); return; }
            int corner = mode == SelectionMode.Rectangle ? HitCorner(e.Location) : -1;
            if (corner >= 0)
            {
                DamageChrome();
                oppositeVirtual = CornerVirtual((corner + 2) % 4);
                phase = SelectionPhase.Resizing;
                LayoutChrome();
                DamageChrome();
                return;
            }
        }
        if (phase is SelectionPhase.WaitingForSelection or SelectionPhase.LeftSelecting)
        {
            DamageCrosshair();
            DamageChrome();
            phase = SelectionPhase.LeftSelecting;
            anchorVirtual = virtualPoint;
            selectionVirtual = new Rectangle(virtualPoint, Size.Empty);
            freeformPoints.Clear();
            freeformPoints.Add(virtualPoint);
            inlineError = null;
            hintFadeStarted = clock.Elapsed.TotalSeconds;
            if (!MotionAllowed) hintOpacity = 0;
            LayoutChrome();
            DamageChrome();
            SyncAnimation();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (operationInProgress) return;
        if (phase == SelectionPhase.WaitingForSelection) DamageCrosshair();
        mouseClient = e.Location;
        if (phase is SelectionPhase.LeftSelecting or SelectionPhase.Resizing)
        {
            using var changed = SelectionRegion();
            DamageOutline();
            DamageChrome();
            UpdateSelection(ToVirtual(e.Location));
            RebuildPaths();
            using var current = SelectionRegion();
            changed.Xor(current);
            Invalidate(changed);
            UpdateDisplay();
            DamageOutline();
            DamageChrome();
            if (mode == SelectionMode.Freeform && MotionAllowed && trail.Add(e.Location, scale, clock.Elapsed.TotalSeconds))
            {
                DamageTrail();
            }
            SyncAnimation();
        }
        else if (phase == SelectionPhase.Selected)
        {
            int next = toolButtons.FindIndex(button => button.Bounds.Contains(e.Location));
            if (next != hoveredButton)
            {
                if (hoveredButton >= 0) Damage(toolButtons[hoveredButton].Bounds);
                hoveredButton = next;
                if (hoveredButton >= 0) Damage(toolButtons[hoveredButton].Bounds);
            }
            int corner = mode == SelectionMode.Rectangle ? HitCorner(e.Location) : -1;
            Cursor = corner >= 0 ? corner % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW
                : hoveredButton >= 0 ? Cursors.Hand : Cursors.Cross;
        }
        else
        {
            Rectangle previous = activeMonitor;
            UpdateDisplay();
            if (previous != activeMonitor) DamageChrome();
            DamageCrosshair();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || operationInProgress) return;
        DamageChrome();
        if (phase == SelectionPhase.LeftSelecting)
        {
            if (HasMeaningfulSelection()) { phase = SelectionPhase.Selected; Cursor = Cursors.Default; }
            else { ResetSelection(); return; }
        }
        else if (phase == SelectionPhase.Resizing) phase = SelectionPhase.Selected;
        LayoutChrome();
        DamageChrome();
        SyncAnimation();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Diagnostics.AppTrace.Log("overlay-key-" + e.KeyCode + "-phase-" + phase);
        if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); return; }
        if (operationInProgress) return;
        if (e.KeyCode == Keys.Tab)
        {
            ResetSelection();
            mode = mode == SelectionMode.Rectangle ? SelectionMode.Freeform : SelectionMode.Rectangle;
            DamageCrosshair();
            Damage(hintBounds);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space && phase == SelectionPhase.Selected) { e.Handled = true; BeginConfirm(true); }
        else if (e.KeyCode == Keys.Enter && phase == SelectionPhase.Selected) { e.Handled = true; BeginConfirm(false); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Rectangle clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, night.Size));
        if (clip.Width <= 0 || clip.Height <= 0) return;
        e.Graphics.DrawImage(night, clip, clip, GraphicsUnit.Pixel);
        NativeTheme.Prepare(e.Graphics);
        if (HasSelection)
        {
            var state = e.Graphics.Save();
            if (rawPath is not null) e.Graphics.SetClip(rawPath, CombineMode.Intersect);
            else e.Graphics.SetClip(ToClient(selectionVirtual), CombineMode.Intersect);
            Rectangle source = Rectangle.Intersect(clip, ToClient(selectionVirtual));
            if (source.Width > 0 && source.Height > 0) e.Graphics.DrawImage(capture.Image, source, source, GraphicsUnit.Pixel);
            e.Graphics.Restore(state);
            DrawOutline(e.Graphics);
            Rectangle label = SizeLabelBounds();
            if (clip.IntersectsWith(label)) DrawPill(e.Graphics, label, $"{selectionVirtual.Width} × {selectionVirtual.Height}", NativeTheme.Text, 1, true);
        }
        if (MotionAllowed) trail.Draw(e.Graphics, clock.Elapsed.TotalSeconds);
        DrawCrosshair(e.Graphics);
        if (phase == SelectionPhase.Selected && clip.IntersectsWith(toolbar)) DrawToolbar(e.Graphics);
        if (hintOpacity > 0 && clip.IntersectsWith(hintBounds))
            DrawPill(e.Graphics, hintBounds, mode == SelectionMode.Rectangle ? "矩形圈选  ·  Tab 切换自由圈选" : "自由圈选  ·  Tab 切换矩形", NativeTheme.TextMuted, hintOpacity);
        if (!string.IsNullOrEmpty(inlineError) && clip.IntersectsWith(errorBounds)) DrawPill(e.Graphics, errorBounds, inlineError, NativeTheme.Danger);
    }

    private void DrawOutline(Graphics graphics)
    {
        using var glow = new Pen(NativeTheme.Alpha(NativeTheme.CaptureLine, 32), D(5));
        using var outline = new Pen(NativeTheme.CaptureLine, NativeTheme.SelectionStroke * scale);
        if (smoothPath is not null)
        {
            graphics.DrawPath(glow, smoothPath);
            graphics.DrawPath(outline, smoothPath);
            return;
        }
        Rectangle rect = ToClient(selectionVirtual);
        graphics.DrawRectangle(glow, rect);
        graphics.DrawRectangle(outline, rect);
        float pulse = MotionAllowed ? .78F + .22F * (float)Math.Sin(clock.Elapsed.TotalSeconds * 1.8) : 1;
        using var starBrush = new SolidBrush(NativeTheme.Alpha(NativeTheme.CaptureMark, (int)(255 * pulse)));
        using var handleOutline = new Pen(NativeTheme.Alpha(NativeTheme.CaptureLine, 90), scale);
        for (int i = 0; i < 4; i++)
        {
            Point center = ToClientPoint(CornerVirtual(i));
            using var star = NativeTheme.Mark(center, (NativeTheme.IsCrabapple ? 7.5F : 5.5F) * scale, 1.25F * scale);
            graphics.FillPath(starBrush, star);
            graphics.DrawEllipse(handleOutline, center.X - D(8), center.Y - D(8), D(16), D(16));
        }
    }

    private void DrawToolbar(Graphics graphics)
    {
        using var panel = NativeTheme.RoundedRectangle(toolbar, toolbar.Height / 2F);
        using var background = new SolidBrush(NativeTheme.Alpha(NativeTheme.Surface, 242));
        using var border = new Pen(NativeTheme.Stroke);
        graphics.FillPath(background, panel);
        graphics.DrawPath(border, panel);
        using var font = NativeTheme.TextFont(13, scale);
        using var shortcutFont = NativeTheme.MonoFont(11, scale);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < toolButtons.Count; i++)
        {
            ToolButton button = toolButtons[i];
            using var path = NativeTheme.RoundedRectangle(button.Bounds, button.Bounds.Height / 2F);
            Color fill = i == 0 ? (i == hoveredButton ? NativeTheme.AccentHover : NativeTheme.Accent) : NativeTheme.SurfaceHover;
            if (i == 0 || i == hoveredButton)
            {
                using var brush = new SolidBrush(fill);
                graphics.FillPath(brush, path);
            }
            using var text = new SolidBrush(i == 0 ? NativeTheme.AccentInk : NativeTheme.Text);
            using var secondary = new SolidBrush(i == 0 ? NativeTheme.AccentInk : NativeTheme.TextMuted);
            Rectangle label = button.Bounds;
            if (button.Shortcut.Length > 0) label.Width = D(53);
            graphics.DrawString(button.Text, font, text, label, format);
            if (button.Shortcut.Length > 0)
            {
                Rectangle key = new(button.Bounds.Left + D(51), button.Bounds.Top, button.Bounds.Width - D(56), button.Bounds.Height);
                graphics.DrawString(button.Shortcut, shortcutFont, secondary, key, format);
            }
        }
    }

    private void DrawPill(Graphics graphics, Rectangle bounds, string text, Color color, float opacity = 1, bool mono = false)
    {
        using var path = NativeTheme.RoundedRectangle(bounds, bounds.Height / 2F);
        using var background = new SolidBrush(NativeTheme.Alpha(NativeTheme.Surface, (int)(225 * opacity)));
        using var border = new Pen(NativeTheme.Alpha(NativeTheme.Stroke, (int)(NativeTheme.Stroke.A * opacity)));
        using var brush = new SolidBrush(NativeTheme.Alpha(color, (int)(255 * opacity)));
        using var font = mono ? NativeTheme.MonoFont(12, scale) : NativeTheme.TextFont(13, scale);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);
        Rectangle textBounds = Rectangle.Inflate(bounds, -D(10), 0);
        graphics.DrawString(text, font, brush, textBounds, format);
    }

    private void DrawCrosshair(Graphics graphics)
    {
        if (phase != SelectionPhase.WaitingForSelection || mode != SelectionMode.Rectangle) return;
        using var line = new Pen(NativeTheme.Alpha(NativeTheme.CaptureLine, 55), Math.Max(1, scale * .6F));
        graphics.DrawLine(line, activeMonitor.Left, mouseClient.Y, activeMonitor.Right, mouseClient.Y);
        graphics.DrawLine(line, mouseClient.X, activeMonitor.Top, mouseClient.X, activeMonitor.Bottom);
    }

    private void UpdateDisplay()
    {
        Point point = HasSelection ? new Point(selectionVirtual.Left + selectionVirtual.Width / 2, selectionVirtual.Top + selectionVirtual.Height / 2) : ToVirtual(mouseClient);
        var monitor = monitors.FirstOrDefault(item => item.Bounds.Contains(point));
        if (monitor.Bounds.IsEmpty) monitor = monitors[0];
        if (activeMonitor != ToClient(monitor.Bounds)) DamageChrome();
        activeMonitor = ToClient(monitor.Bounds);
        scale = monitor.Scale;
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        hintBounds = new Rectangle(activeMonitor.Left + (activeMonitor.Width - D(360)) / 2, activeMonitor.Top + D(24), D(360), D(36));
        errorBounds = new Rectangle(activeMonitor.Left + D(24), activeMonitor.Bottom - D(84), activeMonitor.Width - D(48), D(52));
        toolbar = Rectangle.Empty;
        if (phase != SelectionPhase.Selected) return;
        if (toolButtons.Count == 0)
            toolButtons.AddRange([new("说话", "Space", () => BeginConfirm(true)), new("保存", "Enter", () => BeginConfirm(false)), new("重选", "", ResetSelection), new("取消", "Esc", Close)]);
        Rectangle selected = ToClient(selectionVirtual);
        int width = D(426), height = D(48);
        int x = Math.Clamp(selected.Right - width, activeMonitor.Left + D(12), Math.Max(activeMonitor.Left + D(12), activeMonitor.Right - width - D(12)));
        int y = selected.Bottom + D(14);
        if (y + height > activeMonitor.Bottom - D(12)) y = Math.Max(activeMonitor.Top + D(12), selected.Top - height - D(14));
        toolbar = new Rectangle(x, y, width, height);
        for (int i = 0; i < toolButtons.Count; i++)
            toolButtons[i].Bounds = new Rectangle(x + D(6 + i * 104), y + D(6), D(102), D(36));
    }

    private Rectangle SizeLabelBounds()
    {
        Rectangle selected = ToClient(selectionVirtual);
        int width = D(158), height = D(28);
        return new Rectangle(Math.Clamp(selected.Left, activeMonitor.Left + D(6), Math.Max(activeMonitor.Left + D(6), activeMonitor.Right - width - D(6))),
            Math.Max(activeMonitor.Top + D(6), selected.Top - height - D(10)), width, height);
    }

    private void UpdateSelection(Point current)
    {
        if (phase == SelectionPhase.Resizing) { anchorVirtual = current; selectionVirtual = Normalize(oppositeVirtual, current); return; }
        if (mode == SelectionMode.Freeform)
        {
            // 采样阈值属于保存路径的行为，不随视觉 DPI 缩放。
            if (freeformPoints.Count == 0 || Distance(freeformPoints[^1], current) >= 4) freeformPoints.Add(current);
            selectionVirtual = Rectangle.FromLTRB(freeformPoints.Min(p => p.X), freeformPoints.Min(p => p.Y), freeformPoints.Max(p => p.X), freeformPoints.Max(p => p.Y));
        }
        else selectionVirtual = Normalize(anchorVirtual, current);
    }

    private void RebuildPaths()
    {
        rawPath?.Dispose(); rawPath = null;
        smoothPath?.Dispose(); smoothPath = null;
        if (mode != SelectionMode.Freeform || freeformPoints.Count < 2) return;
        Point[] points = freeformPoints.Select(ToClientPoint).ToArray();
        // 原始多边形负责还原画面，样条仅用于描边，避免视觉平滑改变实际截图范围。
        rawPath = new GraphicsPath();
        rawPath.AddLines(points);
        rawPath.CloseFigure();
        smoothPath = new GraphicsPath();
        if (points.Length >= 3) smoothPath.AddClosedCurve(points, .35F);
        else smoothPath.AddLines(points);
    }

    private Region SelectionRegion()
    {
        if (rawPath is not null) return new Region(rawPath);
        return new Region(ToClient(selectionVirtual));
    }

    private bool HasMeaningfulSelection() => mode == SelectionMode.Rectangle
        ? selectionVirtual.Width >= 4 && selectionVirtual.Height >= 4
        : freeformPoints.Count >= 4 && selectionVirtual.Width >= 12 && selectionVirtual.Height >= 12;

    private void ResetSelection()
    {
        using (var region = SelectionRegion()) Invalidate(region);
        DamageOutline(); DamageChrome(); DamageCrosshair(); DamageTrail();
        phase = SelectionPhase.WaitingForSelection;
        selectionVirtual = Rectangle.Empty;
        freeformPoints.Clear(); toolButtons.Clear(); trail.Clear();
        RebuildPaths();
        hoveredButton = -1;
        hintOpacity = 1;
        hintFadeStarted = -1;
        Cursor = Cursors.Cross;
        mouseClient = ToClientPoint(Cursor.Position);
        UpdateDisplay();
        DamageChrome(); DamageCrosshair();
        SyncAnimation();
    }

    private async void BeginConfirm(bool speak)
    {
        if (ConfirmAsync is null || phase != SelectionPhase.Selected || operationInProgress) return;
        operationInProgress = true;
        Damage(errorBounds);
        inlineError = null;
        SyncAnimation();
        try
        {
            var result = new SelectionResult { Capture = capture, Mode = mode, Bounds = selectionVirtual, FreeformPath = freeformPoints.ToArray(), Speak = speak };
            bool saved = await ConfirmAsync(result);
            if (saved) Close();
            else inlineError = "截图还没有保存。请检查数据目录是否可写，然后再试一次。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExternalException) { inlineError = exception.Message; }
        finally
        {
            operationInProgress = false;
            if (!IsDisposed) { Damage(errorBounds); SyncAnimation(); }
        }
    }

    private void SyncAnimation()
    {
        if (!MotionAllowed)
        {
            DamageTrail(); trail.Clear();
            if (hintFadeStarted >= 0 && hintOpacity > 0) { hintOpacity = 0; Damage(hintBounds); }
        }
        animationTimer.Enabled = MotionAllowed && (CornersAnimate || trail.Active || hintFadeStarted >= 0 && hintOpacity > 0);
    }

    private void Animate()
    {
        if (!MotionAllowed) { SyncAnimation(); return; }
        double now = clock.Elapsed.TotalSeconds;
        if (CornersAnimate) DamageCorners();
        if (trail.Active) { DamageTrail(); trail.Advance(now); }
        if (hintFadeStarted >= 0 && hintOpacity > 0)
        {
            hintOpacity = Math.Clamp(1 - (float)((now - hintFadeStarted) / .22), 0, 1);
            Damage(hintBounds);
        }
        SyncAnimation();
    }

    private void Damage(Rectangle rectangle)
    {
        rectangle = Rectangle.Intersect(ClientRectangle, rectangle);
        if (rectangle.Width > 0 && rectangle.Height > 0) Invalidate(rectangle);
    }
    private void DamageChrome()
    {
        if (!toolbar.IsEmpty) Damage(Rectangle.Inflate(toolbar, 2, 2));
        if (HasSelection) Damage(Rectangle.Inflate(SizeLabelBounds(), 2, 2));
        if (hintOpacity > 0) Damage(Rectangle.Inflate(hintBounds, 2, 2));
        if (inlineError is not null) Damage(Rectangle.Inflate(errorBounds, 2, 2));
    }
    private void DamageCorners()
    {
        for (int i = 0; i < 4; i++)
        {
            Point point = ToClientPoint(CornerVirtual(i));
            Damage(new Rectangle(point.X - D(11), point.Y - D(11), D(22), D(22)));
        }
    }
    private void DamageOutline()
    {
        if (!HasSelection) return;
        if (smoothPath is not null)
        {
            using var stroke = (GraphicsPath)smoothPath.Clone();
            using var pen = new Pen(NativeTheme.CaptureLine, D(10));
            stroke.Widen(pen);
            using var region = new Region(stroke);
            Invalidate(region);
        }
        else
        {
            Rectangle rect = ToClient(selectionVirtual);
            int pad = D(12);
            Damage(new Rectangle(rect.Left - pad, rect.Top - pad, rect.Width + 2 * pad, 2 * pad));
            Damage(new Rectangle(rect.Left - pad, rect.Bottom - pad, rect.Width + 2 * pad, 2 * pad));
            Damage(new Rectangle(rect.Left - pad, rect.Top, 2 * pad, rect.Height));
            Damage(new Rectangle(rect.Right - pad, rect.Top, 2 * pad, rect.Height));
        }
    }
    private void DamageTrail() { foreach (var rectangle in trail.DirtyRectangles()) Damage(rectangle); }
    private void DamageCrosshair()
    {
        if (phase != SelectionPhase.WaitingForSelection || mode != SelectionMode.Rectangle) return;
        Damage(new Rectangle(activeMonitor.Left, mouseClient.Y - D(2), activeMonitor.Width, D(4)));
        Damage(new Rectangle(mouseClient.X - D(2), activeMonitor.Top, D(4), activeMonitor.Height));
    }
    private Point ToVirtual(Point client) => new(client.X + capture.VirtualBounds.Left, client.Y + capture.VirtualBounds.Top);
    private Point ToClientPoint(Point point) => new(point.X - capture.VirtualBounds.Left, point.Y - capture.VirtualBounds.Top);
    private Rectangle ToClient(Rectangle rectangle) => new(ToClientPoint(rectangle.Location), rectangle.Size);
    private Point CornerVirtual(int corner) => corner switch
    {
        0 => new(selectionVirtual.Left, selectionVirtual.Top), 1 => new(selectionVirtual.Right, selectionVirtual.Top),
        2 => new(selectionVirtual.Right, selectionVirtual.Bottom), _ => new(selectionVirtual.Left, selectionVirtual.Bottom),
    };
    private int HitCorner(Point point)
    {
        for (int i = 0; i < 4; i++)
        {
            Point corner = ToClientPoint(CornerVirtual(i));
            if (Math.Abs(corner.X - point.X) <= D(10) && Math.Abs(corner.Y - point.Y) <= D(10)) return i;
        }
        return -1;
    }
    private static Rectangle Normalize(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    private static float Distance(Point a, Point b) => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2));
    private enum SelectionPhase { WaitingForSelection, LeftSelecting, Selected, Resizing }
    private sealed class ToolButton(string text, string shortcut, Action action)
    {
        public string Text { get; } = text;
        public string Shortcut { get; } = shortcut;
        public Action Action { get; } = action;
        public Rectangle Bounds { get; set; }
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr window, IntPtr context);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}
