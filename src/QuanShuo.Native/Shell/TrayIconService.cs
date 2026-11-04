using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuanShuo.Native.Shell;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer recordingTimer = new() { Interval = 100 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private bool hasRecords;
    private bool recording;
    private bool quietMode;
    private bool reduceMotion;
    private bool disposed;
    private Icon? currentIcon;
    private IntPtr currentHandle;

    public TrayIconService(NotifyIcon trayIcon)
    {
        this.trayIcon = trayIcon;
        reduceMotion = NativeTheme.ReduceMotionEnabled;
        recordingTimer.Tick += (_, _) => Render();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
        NativeTheme.SkinChanged += Render;
        Render();
    }

    public bool QuietMode
    {
        get => quietMode;
        set { quietMode = value; SyncAnimation(); Render(); }
    }

    public void Update(int todayRecords, bool isRecording)
    {
        hasRecords = todayRecords > 0;
        recording = isRecording;
        SyncAnimation();
        Render();
    }

    private void SyncAnimation() => recordingTimer.Enabled = recording && !quietMode && !reduceMotion;
    private void OnDisplayChanged(object? sender, EventArgs e) => RefreshAppearance();
    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => RefreshAppearance();
    private void RefreshAppearance()
    {
        void Refresh()
        {
            if (disposed) return;
            reduceMotion = NativeTheme.ReduceMotionEnabled;
            SyncAnimation();
            Render();
        }
        if (uiContext is not null) uiContext.Post(_ => Refresh(), null);
    }

    public void Dispose()
    {
        disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
        NativeTheme.SkinChanged -= Render;
        recordingTimer.Dispose();
        currentIcon?.Dispose();
        if (currentHandle != IntPtr.Zero) DestroyIcon(currentHandle);
        currentHandle = IntPtr.Zero;
        currentIcon = null;
    }

    private void Render()
    {
        if (disposed) return;
        Point taskbarPoint = Screen.PrimaryScreen?.Bounds.Location ?? Point.Empty;
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out Rect rect))
            taskbarPoint = new Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        int size = Math.Max(16, NativeTheme.SmallIconSize(taskbarPoint));
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            NativeTheme.Prepare(graphics);
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(size / 64F, size / 64F);
            using var darkOutline = new Pen(NativeTheme.Alpha(NativeTheme.Midnight, 220), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var ring = new Pen(NativeTheme.Moon, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(darkOutline, 10, 10, 44, 44, -15, 315);
            graphics.DrawArc(ring, 10, 10, 44, 44, -15, 315);
            float breath = recordingTimer.Enabled ? (float)(.5 - .5 * Math.Cos(clock.Elapsed.TotalSeconds * Math.PI * 2 / 3.6)) : 0;
            Color idle = NativeTheme.IsCrabapple ? NativeTheme.Petal : NativeTheme.Moon;
            Color listening = NativeTheme.IsCrabapple ? NativeTheme.Wine : NativeTheme.Ice;
            Color starColor = recording ? NativeTheme.Mix(listening, NativeTheme.Peach, breath) : idle;
            using var star = NativeTheme.Mark(new PointF(49.5F, 18.6F), 11, 2.2F);
            using var starOutline = new Pen(NativeTheme.Alpha(NativeTheme.Midnight, 230), 1.6F) { LineJoin = LineJoin.Round };
            using var light = new SolidBrush(starColor);
            graphics.DrawPath(starOutline, star);
            graphics.FillPath(light, star);
            if (hasRecords)
            {
                using var dot = new SolidBrush(NativeTheme.Moon);
                using var dotEdge = new Pen(NativeTheme.Midnight, 1.6F);
                graphics.FillEllipse(dot, 29, 29, 6, 6);
                graphics.DrawEllipse(dotEdge, 29, 29, 6, 6);
            }
        }
        IntPtr handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        Icon? previousManaged = currentIcon ?? trayIcon.Icon;
        IntPtr previousHandle = currentHandle;
        trayIcon.Icon = icon;
        currentIcon = icon;
        currentHandle = handle;
        previousManaged?.Dispose();
        if (previousHandle != IntPtr.Zero) DestroyIcon(previousHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
