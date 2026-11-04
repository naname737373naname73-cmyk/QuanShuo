using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using QuanShuo.Native.Shell;

namespace QuanShuo.Installer;

public sealed class InstallerForm : Form
{
    private const int DesignWidth = 640;
    private const int DesignHeight = 460;
    private const int EdgeInset = 36;
    // 从夜景图里裁出主峰和水岸线，比例与窗口一致
    private static readonly RectangleF HeroCrop = new(487, 170, 1433, 1030);

    private readonly Image hero;
    private readonly TextBox directoryText = new() { BorderStyle = BorderStyle.None, BackColor = NativeTheme.Sapphire, ForeColor = NativeTheme.Moon, AccessibleName = "安装位置" };
    private readonly GlassButton browseButton = new() { Text = "浏览…" };
    private readonly GlassCheck launchCheck = new() { Text = "安装后打开圈说", Checked = true };
    private readonly GlassCheck desktopCheck = new() { Text = "创建桌面快捷方式" };
    private readonly GlassButton cancelButton = new() { Text = "取消" };
    private readonly GlassButton installButton = new() { Text = "安装", Primary = true };
    private readonly GlassClose closeButton = new();
    private Bitmap? backdrop;
    private Rectangle fieldBounds;
    private Rectangle statusBounds;
    private Rectangle progressBounds;
    private float scale = 1;
    private bool roundedByDwm;
    private bool placing;
    private bool installing;
    private bool completed;
    private double? progress;
    private string statusText = "";
    private bool statusIsError;

    public InstallerForm()
    {
        Text = "安装圈说";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = NativeTheme.Midnight;
        ForeColor = NativeTheme.Moon;
        if (Environment.ProcessPath is { } executable) Icon = Icon.ExtractAssociatedIcon(executable);
        hero = LoadHero();

        directoryText.Text = InstallerLogic.DefaultDirectory;
        directoryText.GotFocus += (_, _) => Invalidate(Rectangle.Inflate(fieldBounds, 2, 2));
        directoryText.LostFocus += (_, _) => Invalidate(Rectangle.Inflate(fieldBounds, 2, 2));
        browseButton.Click += (_, _) => BrowseDirectory();
        cancelButton.Click += (_, _) => Close();
        closeButton.Click += (_, _) => Close();
        installButton.Click += async (_, _) =>
        {
            if (completed) Close();
            else await InstallAsync();
        };
        Controls.AddRange([directoryText, browseButton, launchCheck, desktopCheck, cancelButton, installButton, closeButton]);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // 保留 DWM 判断阴影与圆角所需的框架样式，非客户区在 WndProc 里收回。
            parameters.Style |= 0x00040000 | 0x00C00000;
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
        if (m.Msg == 0x0084) m.Result = new IntPtr(1);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        roundedByDwm = NativeTheme.ApplyWindowFrame(Handle);
        NativeTheme.RefreshFrame(Handle);
        ApplyLayout(DeviceDpi / 96F);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        placing = true;
        CenterOn(Screen.FromPoint(Cursor.Position));
        placing = false;
        ActiveControl = installButton;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLayout(e.DeviceDpiNew / 96F);
        // 移到另一块缩放不同的屏幕时窗口大小变了，首次摆放要按新大小重新居中
        if (placing) CenterOn(Screen.FromPoint(Cursor.Position));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) NativeTheme.DragWindow(Handle);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        if (keyData == Keys.Enter && ActiveControl is TextBox or GlassCheck)
        {
            if (completed) Close();
            else _ = InstallAsync();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 写到一半关窗会留下残缺的安装
        if (installing && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            backdrop?.Dispose();
            hero.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (backdrop is null) base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (backdrop is not null) e.Graphics.DrawImage(backdrop, new Rectangle(0, 0, backdrop.Width, backdrop.Height));
        NativeTheme.Prepare(e.Graphics);

        using (var field = NativeTheme.RoundedRectangle(fieldBounds, NativeTheme.ControlRadius * scale))
        using (var fill = new SolidBrush(directoryText.BackColor))
        using (var border = new Pen(directoryText.Focused ? NativeTheme.Ice : NativeTheme.Hairline))
        {
            e.Graphics.FillPath(fill, field);
            e.Graphics.DrawPath(border, field);
        }

        if (progress is { } value)
        {
            using var track = new SolidBrush(NativeTheme.Hairline);
            using var bar = new SolidBrush(NativeTheme.Ice);
            e.Graphics.FillRectangle(track, progressBounds);
            e.Graphics.FillRectangle(bar, progressBounds.X, progressBounds.Y, (int)(progressBounds.Width * Math.Clamp(value, 0, 1)), progressBounds.Height);
        }

        if (statusText.Length > 0)
        {
            using var font = NativeTheme.TextFont(12.5F, scale);
            using var brush = new SolidBrush(statusIsError ? NativeTheme.Warning : NativeTheme.Mist);
            using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            e.Graphics.DrawString(statusText, font, brush, statusBounds, format);
        }
    }

    private int D(float value) => NativeTheme.Px(value, scale);

    private void CenterOn(Screen screen)
    {
        Rectangle area = screen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private void ApplyLayout(float newScale)
    {
        scale = newScale;
        // 非客户区已在 WndProc 收回，窗口大小就是客户区大小。
        // ClientSize 仍会按框架样式多算一圈边框，这里不能用。
        Size = new Size(D(DesignWidth), D(DesignHeight));
        int left = D(EdgeInset);
        int right = Width - D(EdgeInset);

        fieldBounds = new Rectangle(left, D(296), right - left - D(96), D(38));
        Font oldFont = directoryText.Font;
        directoryText.Font = NativeTheme.TextFont(13, scale);
        oldFont.Dispose();
        int textHeight = directoryText.PreferredHeight;
        directoryText.SetBounds(fieldBounds.X + D(12), fieldBounds.Y + (fieldBounds.Height - textHeight) / 2, fieldBounds.Width - D(24), textHeight);
        browseButton.SetBounds(right - D(84), D(296), D(84), D(38));
        launchCheck.SetBounds(left, D(347), D(160), D(24));
        desktopCheck.SetBounds(left + D(176), D(347), D(190), D(24));
        cancelButton.SetBounds(right - D(216), D(392), D(92), D(40));
        installButton.SetBounds(right - D(112), D(392), D(112), D(40));
        closeButton.SetBounds(Width - D(44), D(12), D(32), D(32));
        statusBounds = new Rectangle(left, D(392), right - left - D(232), D(40));
        progressBounds = new Rectangle(left, D(380), right - left, Math.Max(2, D(2)));

        RebuildBackdrop();
        if (!roundedByDwm)
        {
            using var path = NativeTheme.RoundedRectangle(new RectangleF(0, 0, Width, Height), NativeTheme.CardRadius * scale);
            Region = new Region(path);
        }

        Invalidate(true);
    }

    private void RebuildBackdrop()
    {
        int width = Width;
        int height = Height;
        var image = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(image))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(hero, new RectangleF(0, 0, width, height), HeroCrop, GraphicsUnit.Pixel);
            NativeTheme.Prepare(g);

            // 左上压暗一点，让标题从星空里浮出来
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(-D(170), -D(150), D(640), D(380));
                using var glow = new PathGradientBrush(glowPath)
                {
                    CenterPoint = new PointF(D(150), D(40)),
                    CenterColor = NativeTheme.Alpha(NativeTheme.Midnight, 165),
                    SurroundColors = [NativeTheme.Alpha(NativeTheme.Midnight, 0)],
                };
                g.FillPath(glow, glowPath);
            }

            // 水面往下沉入深夜色，控件都放在这一段
            int fadeTop = D(214);
            int fadeBottom = D(282);
            using (var fade = new LinearGradientBrush(new Rectangle(0, fadeTop - 1, width, fadeBottom - fadeTop + 2), NativeTheme.Alpha(NativeTheme.Midnight, 0), NativeTheme.Alpha(NativeTheme.Midnight, 242), LinearGradientMode.Vertical))
            {
                g.FillRectangle(fade, 0, fadeTop, width, fadeBottom - fadeTop);
            }

            using (var deep = new SolidBrush(NativeTheme.Alpha(NativeTheme.Midnight, 242)))
            {
                g.FillRectangle(deep, 0, fadeBottom, width, height - fadeBottom);
            }

            int left = D(EdgeInset);
            using var moon = new SolidBrush(NativeTheme.Moon);
            using var mist = new SolidBrush(NativeTheme.Mist);
            using var ice = new SolidBrush(NativeTheme.Ice);
            using var wordmark = NativeTheme.TitleFont(38, scale);
            using var tagline = NativeTheme.TitleFont(16, scale);
            using var small = NativeTheme.TextFont(12.5F, scale);
            g.DrawString("圈说", wordmark, moon, left - D(3), D(38));
            using (var star = NativeTheme.Star(new PointF(left + D(100), D(50)), D(7), D(1.8F))) g.FillPath(ice, star);
            g.DrawString("把每一次圈说，留在自己的电脑上", tagline, moon, left, D(100));
            g.DrawString("记录保存在本机，不需要管理员权限", small, mist, left, D(130));
            g.DrawString("安装位置", small, mist, left, D(270));
        }

        backdrop?.Dispose();
        backdrop = image;
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择圈说的安装位置",
            UseDescriptionForTitle = true,
            SelectedPath = InstallerLogic.NearestExistingDirectory(directoryText.Text.Trim()),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) directoryText.Text = InstallerLogic.WithAppFolder(dialog.SelectedPath);
    }

    private async Task InstallAsync()
    {
        if (installing) return;
        string directory = directoryText.Text.Trim();
        bool desktop = desktopCheck.Checked;
        SetBusy(true);
        SetStatus("正在安装…", false);
        progress = 0;
        Invalidate(progressBounds);
        var reporter = new Progress<double>(value =>
        {
            progress = value;
            Invalidate(progressBounds);
        });

        try
        {
            // 写 100 多 MB，不能放在界面线程上
            await Task.Run(() => InstallerLogic.InstallAsync(directory, true, desktop, reporter, CancellationToken.None));
        }
        catch (Exception exception)
        {
            InstallerLogic.LogFailure(exception);
            progress = null;
            Invalidate(progressBounds);
            SetBusy(false);
            ActiveControl = installButton;
            SetStatus(exception.Message, true);
            return;
        }

        progress = null;
        Invalidate(progressBounds);
        SetBusy(false);
        completed = true;
        directoryText.ReadOnly = true;
        browseButton.Enabled = false;
        launchCheck.Enabled = false;
        desktopCheck.Enabled = false;
        cancelButton.Visible = false;
        installButton.Text = "完成";
        ActiveControl = installButton;
        SetStatus("安装完成，可以从开始菜单打开圈说。", false);
        if (!launchCheck.Checked) return;

        try
        {
            InstallerLogic.Launch(directory);
            Close();
        }
        catch (Exception exception)
        {
            SetStatus("已经装好，但没能打开：" + exception.Message, true);
        }
    }

    private void SetBusy(bool busy)
    {
        installing = busy;
        directoryText.ReadOnly = busy;
        browseButton.Enabled = !busy;
        launchCheck.Enabled = !busy;
        desktopCheck.Enabled = !busy;
        cancelButton.Enabled = !busy;
        installButton.Enabled = !busy;
        closeButton.Enabled = !busy;
    }

    private void SetStatus(string text, bool isError)
    {
        statusText = text;
        statusIsError = isError;
        Invalidate(statusBounds);
    }

    private static Image LoadHero()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("hero.jpg")
            ?? throw new InvalidOperationException("安装包缺少资源：hero.jpg");
        return Image.FromStream(stream);
    }
}
