using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using QuanShuo.Core.Models;
using QuanShuo.Native.Capture;
using QuanShuo.Native.Recording;
using QuanShuo.Native.Shell;

namespace QuanShuo.Native.Diagnostics;

/// <summary>
/// 两套皮肤各画一遍录音卡片、说话窗口和圈选层，存成图片给人看，再查换皮肤时开着的窗口有没有跟上。
/// 窗口是全透明打开的，看不见，但会短暂抢一下焦点。不截屏：圈选层用的是一张假图。
/// </summary>
public static class SmokeSkins
{
    public static int Run(string outputPath, string shotDirectory)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Directory.CreateDirectory(shotDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var lines = new List<string>();
        bool ok = true;
        void Check(string name, bool passed, string detail = "")
        {
            ok &= passed;
            lines.Add($"{(passed ? "PASS" : "FAIL")} {name} {detail}");
        }

        string image = Path.Combine(shotDirectory, "fake-shot.png");
        using (var bitmap = new Bitmap(640, 400))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.SteelBlue);
            graphics.FillRectangle(Brushes.WhiteSmoke, 40, 40, 300, 200);
            bitmap.Save(image, ImageFormat.Png);
        }

        var record = new QuanShuoRecord
        {
            Id = "skin-test",
            Timestamp = DateTimeOffset.Now,
            AppName = "test",
            WindowTitle = "test",
            ImagePath = image,
            MarkdownPath = Path.Combine(shotDirectory, "fake.md"),
            Text = "这里的按钮颜色太重了，我想换成更浅的蓝，和背景拉开层次。",
            Status = TranscriptionStatus.Recording,
        };
        var actions = new RecordingActions(() => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });

        foreach (string skin in new[] { "starlake", "crabapple" })
        {
            NativeTheme.UseSkin(skin);
            using (var card = new RecordingCard(record, actions)) Save(card, $"{skin}-card-recording.png");
            using (var card = new RecordingCard(record with { Status = TranscriptionStatus.Completed }, actions))
            {
                card.SetFinished(record with { Status = TranscriptionStatus.Completed });
                Save(card, $"{skin}-card-finished.png");
            }

            using (var window = new SpeechWindow(record, actions, null, null)) Save(window, $"{skin}-speech.png");
            using (var window = new SpeechWindow(record, actions, null, null))
            {
                window.SetError("识别连接中断了。请检查网络后点“重新转写”；已有文字和 WAV 都保留在本机。");
                Save(window, $"{skin}-speech-error.png");
            }

            var fake = new Bitmap(960, 600);
            using (var graphics = Graphics.FromImage(fake))
            {
                graphics.Clear(Color.FromArgb(230, 234, 240));
                graphics.FillRectangle(Brushes.SteelBlue, 120, 90, 500, 300);
            }

            var capture = new FrozenCapture
            {
                Image = fake,
                VirtualBounds = new Rectangle(0, 0, fake.Width, fake.Height),
                Metadata = new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "test", WindowTitle = "test" },
            };
            using (var overlay = new OverlayForm(capture, QuanShuo.Core.Models.SelectionMode.Rectangle))
            {
                PretendSelected(overlay);
                SaveHidden(overlay, $"{skin}-overlay.png");
            }

            capture.Dispose();

            using var menu = GlassMenuRenderer.CreateMenu();
            menu.Items.Add("圈说 · 历史");
            menu.Items.Add("设置");
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出");
            menu.CreateControl();
            Check($"{skin} 菜单底色", menu.BackColor == NativeTheme.SurfaceRaised);

            // 独立菜单只使用空回调，不注册热键、不读写设置，也不启动录音。
            using var tray = new TrayMenu("Alt+Q", "Alt+Space", () => { }, () => { }, () => { }, () => { }, () => { });
            tray.Opacity = 0;
            tray.Show(new Point(100, 100));
            Application.DoEvents();
            foreach (float scale in new[] { 1F, 1.5F, 2F })
            {
                tray.RefreshAppearance(scale);
                tray.PerformLayout();
                using var preview = new Bitmap(tray.Width, tray.Height);
                PaintMenu(tray, preview);
                preview.Save(Path.Combine(shotDirectory, $"{skin}-tray-{scale * 100:0}.png"), ImageFormat.Png);
            }
            tray.UpdateHotkeys("Ctrl+Alt+Shift+Win+PrintScreen", "Ctrl+Alt+Shift+Win+Backspace");
            tray.RefreshAppearance(1);
            tray.SpeechItem.Select();
            using (var preview = new Bitmap(tray.Width, tray.Height))
            {
                PaintMenu(tray, preview);
                preview.Save(Path.Combine(shotDirectory, $"{skin}-tray-long-keys.png"), ImageFormat.Png);
            }
            tray.Close();
        }

        // 换皮肤时开着的窗口要当场跟上
        NativeTheme.UseSkin("starlake");
        using (var open = new RecordingCard(record, actions))
        {
            LiveTextBox box = open.Controls.OfType<LiveTextBox>().First();
            Check("星湖文字框是深色", box.BackColor == NativeTheme.Midnight);
            Size size = open.Size;
            NativeTheme.UseSkin("crabapple");
            Check("开着的卡片换了底色", box.BackColor == NativeTheme.Slip);
            // 海棠的卡片要放下截图和题签，比星湖高
            Check("换皮肤后宽度不变、高度换成海棠的比例", open.Width == size.Width && Math.Abs(open.Width * 373F / (open.Height * 540F) - 1) < .02F, $"{open.Size}");
            Check("换到海棠后文字框不再压图", !box.HasBackdrop);
            Save(open, "live-switch.png");
        }

        // 上面的窗口已经释放。如果还挂在事件上，这一下会碰到已释放的窗口，程序走不到后面
        NativeTheme.UseSkin("starlake");
        Check("窗口关掉后再换皮肤不出错", true);

        // 和前端 query.ts 的 CLOCK_KEYS 是同一条规则，两边算出来要一样
        foreach ((int hour, int minute, bool dusk) in new[] { (5, 29, false), (5, 30, true), (6, 0, true), (12, 0, true), (18, 0, true), (18, 29, true), (18, 30, false), (19, 0, false), (23, 0, false), (0, 0, false) })
        {
            Check($"{hour}:{minute:00} 是{(dusk ? "傍晚" : "夜晚")}", NativeTheme.IsDuskAt(new DateTime(2026, 11, 2, hour, minute, 0)) == dusk);
        }

        NativeTheme.UseSceneDay(3);
        Check("滑块拖到夜晚，语音框跟着夜晚", !NativeTheme.IsDusk);
        NativeTheme.UseSceneDay(2);
        Check("滑块拖到傍晚，语音框跟着傍晚", NativeTheme.IsDusk);
        NativeTheme.UseSceneDay(null);

        NativeTheme.UseSkin("unknown");
        Check("不认识的皮肤退回星湖", !NativeTheme.IsCrabapple);
        lines.Add(ok ? "ALL PASSED" : "FAILED");
        File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));
        return ok ? 0 : 1;

        static void PaintMenu(TrayMenu tray, Bitmap preview)
        {
            using var graphics = Graphics.FromImage(preview);
            IntPtr dc = graphics.GetHdc();
            PrintWindow(tray.Handle, dc, PwClientOnly | PwRenderFullContent);
            graphics.ReleaseHdc(dc);
        }

        // 圈选层一打开就会抓鼠标，不能真的显示，直接把内部状态设成“选好了”再画
        void PretendSelected(OverlayForm overlay)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Type type = typeof(OverlayForm);
            type.GetField("selectionVirtual", flags)!.SetValue(overlay, new Rectangle(160, 110, 400, 240));
            FieldInfo phase = type.GetField("phase", flags)!;
            phase.SetValue(overlay, Enum.Parse(phase.FieldType, "Selected"));
            type.GetMethod("LayoutChrome", flags)!.Invoke(overlay, null);
        }

        void SaveHidden(Form form, string name)
        {
            form.CreateControl();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(shotDirectory, name), ImageFormat.Png);
            Check(name, bitmap.Width > 0, $"{form.Width}x{form.Height}");
        }

        void Save(Form form, string name)
        {
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            Thread.Sleep(60);
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                PrintWindow(form.Handle, dc, PwClientOnly | PwRenderFullContent);
                graphics.ReleaseHdc(dc);
            }

            bitmap.Save(Path.Combine(shotDirectory, name), ImageFormat.Png);
            Check(name, bitmap.Width > 0, $"{form.Width}x{form.Height}");
        }
    }

    private const uint PwClientOnly = 1;
    private const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
