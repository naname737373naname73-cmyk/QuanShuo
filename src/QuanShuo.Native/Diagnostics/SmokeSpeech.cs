using System.Runtime.InteropServices;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Recording;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.Diagnostics;

internal delegate void Reporter(string name, bool passed, string detail = "");

/// <summary>
/// 真窗口走一遍边说边改：真实的协调器、文件和窗口，识别服务换成脚本。
/// 麦克风会打开，但音频只写进临时 WAV，随后删除，不会发到任何地方。
/// 只有传入 realAsrWav 时才联网，发送的只是那个文件。
/// 按键只在窗口确实处于前台时才发，避免打进用户正在用的程序。
/// </summary>
public static class SmokeSpeech
{
    private const int WmChar = 0x0102;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmClose = 0x0010;
    private const int VkBack = 0x08;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkShift = 0x10;
    private const int VkA = 0x41;
    private const int WmMouseWheel = 0x020A;
    private const uint GwHwndNext = 2;
    private const int CaretMargin = 4;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const string LongError = "识别连接中断了。请检查网络后点“重新转写”；已有文字和 WAV 都保留在本机。";

    public static int Run(string outputPath, IntPtr target, string targetFile, string shotDirectory, string? realAsrWav)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var log = new List<string>();
        int failures = 0;

        void Check(string name, bool passed, string detail = "")
        {
            log.Add((passed ? "ok   " : "FAIL ") + name + (detail.Length == 0 ? string.Empty : "  => " + detail));
            if (!passed) failures++;
        }

        var context = new ApplicationContext();
        // 界面线程的异常默认弹对话框，测试会一直等它被关掉。记下来并结束
        Application.ThreadException += (_, e) =>
        {
            Check("界面线程没有异常", false, e.Exception.ToString());
            context.ExitThread();
        };

        async Task Script()
        {
            string root = Path.Combine(Path.GetTempPath(), "quanshuo-smoke-speech-" + Environment.ProcessId);
            try
            {
                await SpeechScenario(root, target, targetFile, shotDirectory, Check);
                await CardScenario(root, shotDirectory, Check);
                await EmptyScenario(root, Check);
                await GlassScenario(shotDirectory, Check);
                if (realAsrWav is not null)
                {
                    await RealAsrScenario(realAsrWav, Check);
                    await RealCoordinatorScenario(root, realAsrWav, Check);
                }
            }
            catch (Exception exception)
            {
                Check("脚本异常", false, exception.ToString());
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                context.ExitThread();
            }
        }

        var starter = new System.Windows.Forms.Timer { Interval = 50 };
        starter.Tick += (_, _) =>
        {
            starter.Stop();
            _ = Script();
        };
        starter.Start();
        Application.Run(context);
        log.Add(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        File.WriteAllLines(outputPath, log);
        return failures == 0 ? 0 : 1;
    }

    private static async Task SpeechScenario(string root, IntPtr target, string targetFile, string shotDirectory, Reporter check)
    {
        var store = new RecordStore(Path.Combine(root, "speech"));
        var fake = new ScriptedProvider();
        var coordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory, DeleteAudioAfterSuccess = true }) { ProviderFactory = () => fake };
        ForceForeground(target);
        await Task.Delay(250);
        PasteTarget? captured = PasteService.ForegroundTarget();
        check("记下前台窗口作为目标", captured?.Window == target, captured?.AppName ?? "null");
        QuanShuoRecord record = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "target", WindowTitle = "t" }, TranscriptionStatus.Recording);
        SpeechWindow? window = null;
        var actions = new RecordingActions(
            async () => await coordinator.FinishAsync(),
            coordinator.TogglePause,
            async (updated, text) => await store.SaveTextAsync(updated, text),
            async updated => await coordinator.RetryAsync(updated),
            coordinator.NoteUserText);
        window = new SpeechWindow(record, actions, captured, null);
        coordinator.PauseChanged += (_, paused) => window.SetPaused(paused);
        coordinator.Finished += (_, completed) => window.SetFinished(completed);
        coordinator.Failed += (_, message) => window.SetError(message);
        coordinator.TextMerger = window.MergeRecognized;
        window.Show();
        ForceForeground(window.Handle);
        await coordinator.StartAsync(record);
        await Task.Delay(200);
        LiveTextBox box = FindBox(window);
        check("窗口拿到前台", GetForegroundWindow() == window.Handle);
        check("悬浮窗字号跟缩放一致", MatchesScale(box, window.DeviceDpi), $"{box.Font.Size} dpi={window.DeviceDpi}");
        check("打开时光标在文字框里", box.Focused);

        await fake.EmitAsync("今天天气", false);
        Post(box, WmChar, VkBack);
        Post(box, WmChar, VkBack);
        await Task.Delay(120);
        check("说话时退格", box.Text == "今天", box.Text);
        await fake.EmitAsync("今天天气不错", false);
        check("新话接在删改处", box.Text == "今天不错", box.Text);
        await fake.EmitAsync("今天天气不错。", true);
        Post(box, WmChar, '！');
        await Task.Delay(120);
        await Task.Delay(700);
        check("打字进了文件", MarkdownRecordFile.Parse(record.MarkdownPath, await File.ReadAllTextAsync(record.MarkdownPath)).Text.Contains('！'));
        await fake.EmitAsync("今天天气不错。我们出去走走。", true);
        check("下一句跟上", box.Text == "今天不错。！我们出去走走。" || box.Text == "今天不错！。我们出去走走。", box.Text);
        Shot(window, Path.Combine(shotDirectory, "speech-listening.png"));

        // 错误信息最长，窗口能拖到多大都要看一眼：花饰不能变形，文字不能压到花上
        Size normal = window.Size;
        window.SetError(LongError);
        await Task.Delay(120);
        Shot(window, Path.Combine(shotDirectory, "speech-error.png"));
        window.Size = new Size(normal.Width * 3 / 2, normal.Height * 3 / 2);
        await Task.Delay(150);
        Shot(window, Path.Combine(shotDirectory, "speech-large.png"));
        window.Size = window.MinimumSize;
        await Task.Delay(150);
        Shot(window, Path.Combine(shotDirectory, "speech-small.png"));
        window.Size = normal;
        await Task.Delay(100);
        await ShotOver(window, Color.White, Path.Combine(shotDirectory, "speech-over-white.png"));
        await ShotOver(window, Color.FromArgb(24, 26, 32), Path.Combine(shotDirectory, "speech-over-dark.png"));
        ForceForeground(window.Handle);
        await Task.Delay(100);

        coordinator.TogglePause();
        await Task.Delay(150);
        Shot(window, Path.Combine(shotDirectory, "speech-paused.png"));
        coordinator.TogglePause();
        await Task.Delay(100);

        fake.FinalText = "今天天气不错。我们出去走走。最后一句。";
        await Task.Delay(100);
        string beforeFinish = box.Text;
        PostKey(box, VkReturn);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!window.IsDisposed && window.Visible && waited.ElapsedMilliseconds < 6000) await Task.Delay(30);
        await Task.Delay(300);
        check("Enter 后窗口关闭", window.IsDisposed || !window.Visible, window.IsDisposed ? string.Empty : "仍可见");
        string expected = beforeFinish + "最后一句。";
        string pasted = File.Exists(targetFile) ? await File.ReadAllTextAsync(targetFile) : "(目标没有内容)";
        check("文字贴进目标窗口", pasted == expected, pasted);
        string clipboard = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        check("剪贴板里也有一份", clipboard == expected, clipboard);
        QuanShuoRecord saved = (await store.ReadAllAsync()).Single();
        check("历史记录是改过的文字", saved.Text == expected, saved.Text);
        check("状态是已完成", saved.Status == TranscriptionStatus.Completed, saved.Status.ToString());
        check("历史记录没有图片", !saved.HasImage);
        check("转写成功后录音已删", !File.Exists(store.GetAudioPath(saved)));
        await coordinator.DisposeAsync();
    }

    private static async Task CardScenario(string root, string shotDirectory, Reporter check)
    {
        var store = new RecordStore(Path.Combine(root, "card"));
        var fake = new ScriptedProvider();
        var coordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory, DeleteAudioAfterSuccess = true }) { ProviderFactory = () => fake };
        QuanShuoRecord record = await store.CreateAsync(
            new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "card", WindowTitle = "t" },
            (path, _) =>
            {
                using var bitmap = new Bitmap(320, 200);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.SteelBlue);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return Task.CompletedTask;
            },
            string.Empty,
            TranscriptionStatus.Recording);
        var actions = new RecordingActions(
            async () => await coordinator.FinishAsync(),
            coordinator.TogglePause,
            async (updated, text) => await store.SaveTextAsync(updated, text),
            async updated => await coordinator.RetryAsync(updated),
            coordinator.NoteUserText);
        var card = new RecordingCard(record, actions);
        coordinator.Finished += (_, completed) => card.SetFinished(completed);
        coordinator.TextMerger = card.MergeRecognized;
        card.Show();
        ForceForeground(card.Handle);
        LiveTextBox box = FindBox(card);
        Post(box, WmChar, ' ');
        await Task.Delay(60);
        check("Space 连发不变成空格", box.TextLength == 0, "[" + box.Text + "]");
        await coordinator.StartAsync(record);
        await Task.Delay(200);
        check("卡片打开后可以直接打字", box.Focused && GetForegroundWindow() == card.Handle);
        check("卡片字号跟缩放一致", MatchesScale(box, card.DeviceDpi), $"{box.Font.Size} dpi={card.DeviceDpi}");
        float cardHeight = Shell.NativeTheme.IsCrabapple ? 373F : 296F;
        check("卡片比例没变", Math.Abs(card.Width * cardHeight / (card.Height * 540F) - 1) < .02F, $"{card.Width}x{card.Height}");
        await fake.EmitAsync("这个按钮的间距太大了。", true);
        await fake.EmitAsync("这个按钮的间距太大了。我想", false);
        box.SelectionStart = 2;
        box.SelectionLength = 2;
        Post(box, WmChar, '框');
        await Task.Delay(120);
        check("录音中改了前面的词", box.Text.StartsWith("这个框的间距", StringComparison.Ordinal), box.Text);
        await fake.EmitAsync("这个按钮的间距太大了。我想和上面对齐。", true);
        check("后面的话不受影响", box.Text == "这个框的间距太大了。我想和上面对齐。", box.Text);
        box.SelectionStart = box.TextLength;
        bool sent = Press(card.Handle, VkShift, VkReturn);
        await Task.Delay(120);
        check("Shift+Enter 换行不结束", sent && box.Text.EndsWith("\r\n", StringComparison.Ordinal), sent ? box.Text : "窗口不在前台，没有发键");
        Press(card.Handle, VkA);
        await Task.Delay(80);
        Press(card.Handle, VkBack);
        await Task.Delay(120);
        Shot(card, Path.Combine(shotDirectory, "card-editing.png"));
        card.SetError(LongError);
        await Task.Delay(120);
        Shot(card, Path.Combine(shotDirectory, "card-error.png"));
        Press(card.Handle, VkBack);
        await Task.Delay(120);
        check("真实按键退格", box.Text == "这个框的间距太大了。我想和上面对齐。", box.Text);
        Press(card.Handle, VkReturn);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (coordinator.IsRecording && waited.ElapsedMilliseconds < 6000) await Task.Delay(30);
        await Task.Delay(300);
        QuanShuoRecord saved = (await store.ReadAllAsync()).Single();
        check("Enter 结束后文件是改过的文字", saved.Text == "这个框的间距太大了。我想和上面对齐。", saved.Text);
        check("结束后留在卡片上", card.Visible);
        Post(box, WmChar, '！');
        await Task.Delay(900);
        saved = (await store.ReadAllAsync()).Single();
        check("结束后继续改也保存", saved.Text.EndsWith('！'), saved.Text);
        Shot(card, Path.Combine(shotDirectory, "card-done.png"));
        card.Close();
        await coordinator.DisposeAsync();
    }

    /// <summary>星湖的文字直接写在玻璃上：什么操作都不能在玻璃上留下痕迹，圆角那层也要一直贴着窗口。</summary>
    private static async Task GlassScenario(string shotDirectory, Reporter check)
    {
        if (Shell.NativeTheme.IsCrabapple) return;
        var record = new QuanShuoRecord
        {
            Id = "glass-test",
            Timestamp = DateTimeOffset.Now,
            AppName = "test",
            WindowTitle = "test",
            ImagePath = string.Empty,
            MarkdownPath = Path.Combine(shotDirectory, "glass.md"),
            Text = string.Empty,
            Status = TranscriptionStatus.Recording,
        };
        var actions = new RecordingActions(() => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask, Task.FromResult, _ => { });
        var window = new SpeechWindow(record, actions, null, null);
        window.Show();
        ForceForeground(window.Handle);
        await Task.Delay(300);
        LiveTextBox box = FindBox(window);
        check("文字框压在玻璃上", box.HasBackdrop);
        Form? halo = window.Halo;
        check("圆角层跟着窗口打开", halo is { Visible: true });
        check("圆角层紧贴窗口下面", halo is not null && GetWindow(window.Handle, GwHwndNext) == halo.Handle);

        using (Bitmap clean = Grab(box))
        {
            box.Merge("这里的按钮颜色太重了，我想换成更浅的蓝。", true);
            await Task.Delay(60);
            foreach (char letter in "一二三四五") Post(box, WmChar, letter);
            await Task.Delay(80);
            box.Merge("这里的按钮颜色太重了，我想换成更浅的蓝。" + string.Concat(Enumerable.Repeat("顺便把标题往上挪一点，左边那一列也有点挤。", 12)), false);
            await Task.Delay(100);
            box.SelectionStart = 6;
            box.SelectionLength = 30;
            await Task.Delay(60);
            for (int turn = 0; turn < 6; turn++)
            {
                SendMessage(box.Handle, WmMouseWheel, new IntPtr(unchecked((short)(turn < 3 ? 120 : -120)) << 16), IntPtr.Zero);
                await Task.Delay(30);
            }

            Shot(window, Path.Combine(shotDirectory, "glass-long.png"));
            box.ReplaceAll(string.Empty);
            await Task.Delay(150);
            using Bitmap after = Grab(box);
            int changed = CountChanged(clean, after);
            check("写字、滚动、选中、清空后玻璃上没有残影", changed == 0, $"{changed} 像素");
        }

        if (halo is not null)
        {
            int margin = Shell.NativeTheme.Px(56, window.DeviceDpi / 96F);
            check("圆角层比窗口大一圈", RealBounds(halo).Size == new Size(window.Width + margin * 2, window.Height + margin * 2), $"{RealBounds(halo).Size} 窗口 {window.Size}");
            Point moved = new(window.Left - 37, window.Top - 23);
            window.Location = moved;
            await Task.Delay(80);
            check("移动窗口后圆角层跟着走", RealBounds(halo).Location == new Point(moved.X - margin, moved.Y - margin), $"{RealBounds(halo)} 窗口 {moved}");
            Size size = window.Size;
            window.Size = new Size(size.Width + 90, size.Height + 60);
            await Task.Delay(120);
            Rectangle grown = RealBounds(halo);
            check("改大小后圆角层跟着变", grown.Width - window.Width == margin * 2 && grown.Height - window.Height == margin * 2, $"{grown.Size} 窗口 {window.Size}");
            check("改大小后文字框还在玻璃上", box.HasBackdrop);
            window.Size = size;
            await Task.Delay(120);

            Shell.NativeTheme.UseSkin("crabapple");
            await Task.Delay(120);
            check("换到海棠时圆角层收起", !halo.Visible);
            check("换到海棠时文字框不压图", !box.HasBackdrop);
            Shell.NativeTheme.UseSkin("starlake");
            await Task.Delay(120);
            check("换回星湖时圆角层回来", halo.Visible && box.HasBackdrop);

            window.Hide();
            await Task.Delay(80);
            check("窗口藏起来时圆角层一起藏", !halo.Visible);
            window.Show();
            await Task.Delay(120);
            check("窗口再出现时圆角层回来", halo.Visible);
        }

        window.SetFinished(record with { Status = TranscriptionStatus.Completed });
        await Task.Delay(100);
        if (!window.IsDisposed) window.Close();
        await Task.Delay(100);
        check("窗口关掉后圆角层也没了", halo is null || halo.IsDisposed);
    }

    /// <summary>UpdateLayeredWindow 改大小不一定通知 WinForms，要问系统。</summary>
    private static Rectangle RealBounds(Form form)
    {
        GetWindowRect(form.Handle, out NativeRect rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static Bitmap Grab(Control control)
    {
        Application.DoEvents();
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bitmap;
    }

    /// <summary>光标在左上角闪，那一小块不算。</summary>
    private static int CountChanged(Bitmap first, Bitmap second)
    {
        int changed = 0;
        for (int y = 0; y < first.Height; y++)
        {
            for (int x = CaretMargin; x < first.Width; x++)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) changed++;
            }
        }

        return changed;
    }

    private static async Task EmptyScenario(string root, Reporter check)
    {
        var store = new RecordStore(Path.Combine(root, "empty"));
        var fake = new ScriptedProvider();
        var coordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory }) { ProviderFactory = () => fake };
        QuanShuoRecord record = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "t", WindowTitle = "t" }, TranscriptionStatus.Recording);
        var actions = new RecordingActions(
            async () => await coordinator.FinishAsync(),
            coordinator.TogglePause,
            async (updated, text) => await store.SaveTextAsync(updated, text),
            async updated => await coordinator.RetryAsync(updated),
            coordinator.NoteUserText);
        var window = new SpeechWindow(record, actions, null, null);
        // 顺序和托盘里一样：先通知窗口，再删空记录
        coordinator.Finished += (_, completed) =>
        {
            window.SetFinished(completed);
            if (string.IsNullOrWhiteSpace(completed.Text)) store.Discard(completed);
        };
        coordinator.TextMerger = window.MergeRecognized;
        window.Show();
        await coordinator.StartAsync(record);
        await Task.Delay(150);
        PostKey(FindBox(window), VkEscape);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!window.IsDisposed && window.Visible && waited.ElapsedMilliseconds < 4000) await Task.Delay(30);
        await Task.Delay(300);
        check("没说话按 Esc 窗口关闭", window.IsDisposed || !window.Visible);
        check("没有文字，协调器已结束", !coordinator.IsRecording);
        check("按 Esc 后不留记录", Directory.GetFiles(store.DataDirectory, "*.md", SearchOption.AllDirectories).Length == 0);
        await coordinator.DisposeAsync();

        await Task.Delay(1100);
        var enterProvider = new ScriptedProvider();
        var enterCoordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory }) { ProviderFactory = () => enterProvider };
        QuanShuoRecord enterRecord = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "t", WindowTitle = "t" }, TranscriptionStatus.Recording);
        var enterActions = new RecordingActions(
            async () => await enterCoordinator.FinishAsync(),
            enterCoordinator.TogglePause,
            async (updated, text) => await store.SaveTextAsync(updated, text),
            async updated => await enterCoordinator.RetryAsync(updated),
            enterCoordinator.NoteUserText);
        var enterWindow = new SpeechWindow(enterRecord, enterActions, null, null);
        enterCoordinator.Finished += (_, completed) =>
        {
            enterWindow.SetFinished(completed);
            if (string.IsNullOrWhiteSpace(completed.Text)) store.Discard(completed);
        };
        enterCoordinator.TextMerger = enterWindow.MergeRecognized;
        enterWindow.Show();
        await enterCoordinator.StartAsync(enterRecord);
        await Task.Delay(150);
        PostKey(FindBox(enterWindow), VkReturn);
        waited.Restart();
        while (!enterWindow.IsDisposed && enterWindow.Visible && waited.ElapsedMilliseconds < 4000) await Task.Delay(30);
        await Task.Delay(300);
        check("没说话按 Enter 窗口关闭", enterWindow.IsDisposed || !enterWindow.Visible);
        string[] left = Directory.GetFiles(store.DataDirectory, "*.md", SearchOption.AllDirectories);
        check("按 Enter 后不留记录", left.Length == 0, string.Join(" ", left.Select(Path.GetFileName)));
        await enterCoordinator.DisposeAsync();

        var slow = new ScriptedProvider { StartDelay = TimeSpan.FromMilliseconds(500) };
        var quick = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory }) { ProviderFactory = () => slow };
        QuanShuoRecord? done = null;
        var failures = new List<string>();
        quick.Finished += (_, completed) => done = completed;
        quick.Failed += (_, message) => failures.Add(message);
        await Task.Delay(1100);
        QuanShuoRecord second = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "t", WindowTitle = "t" }, TranscriptionStatus.Recording);
        Task starting = quick.StartAsync(second);
        await Task.WhenAll(starting, quick.FinishAsync());
        await Task.Delay(100);
        check("连接没建好就结束也能正常收尾", done?.Status == TranscriptionStatus.Completed && failures.Count == 0, done?.Status + " " + string.Join("|", failures));
        await quick.DisposeAsync();
    }

    /// <summary>服务端真实的修订节奏：按说话速度发一段合成语音，中途退格、挪光标打字。</summary>
    private static async Task RealAsrScenario(string wavPath, Reporter check)
    {
        string apiKey = new DpapiSecretStore().LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            check("真实识别", false, "没有已保存的 Key");
            return;
        }

        foreach (string name in new[] { "不改动", "边说边改" })
        {
            using var form = new Form { ShowInTaskbar = false, Width = 500, Height = 300, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
            using var box = new LiveTextBox { Dock = DockStyle.Fill };
            form.Controls.Add(box);
            form.Show();
            box.Focus();
            int updates = 0;
            string lastRecognized = string.Empty;
            await using var provider = new DashScopeTranscriptionProvider();
            var ui = SynchronizationContext.Current!;
            provider.TranscriptReceived += (_, update) => ui.Post(_ =>
            {
                lastRecognized = update.Text;
                box.Merge(update.Text, update.IsFinal);
                updates++;
                if (name == "边说边改" && updates == 6 && box.TextLength > 2)
                {
                    box.SelectionStart = box.TextLength;
                    SendMessage(box.Handle, WmChar, new IntPtr(VkBack), IntPtr.Zero);
                }

                if (name == "边说边改" && updates == 12)
                {
                    box.SelectionStart = 0;
                    foreach (char c in "【批注】") SendMessage(box.Handle, WmChar, new IntPtr(c), IntPtr.Zero);
                    box.SelectionStart = box.TextLength;
                }
            }, null);
            await provider.StartAsync(new TranscriptionOptions { ApiKey = apiKey, Endpoint = "wss://dashscope.aliyuncs.com/api-ws/v1/inference" }, CancellationToken.None);
            byte[] wav = await File.ReadAllBytesAsync(wavPath);
            const int chunk = 3200;
            for (int offset = 44; offset < wav.Length; offset += chunk)
            {
                await provider.SendAudioAsync(wav.AsMemory(offset, Math.Min(chunk, wav.Length - offset)), CancellationToken.None);
                await Task.Delay(100);
            }

            await provider.FinishAsync(CancellationToken.None);
            await Task.Delay(200);
            check($"真实识别·{name}（{updates} 次更新）", box.TextLength > 0, $"识别：{lastRecognized}  文字框：{box.Text}");
            if (name == "不改动") check("不改动时和识别结果完全一致", box.Text == lastRecognized, box.Text);
            else check("边说边改时批注保留、末尾没丢", box.Text.StartsWith("【批注】", StringComparison.Ordinal) && box.Text.EndsWith(lastRecognized[^4..], StringComparison.Ordinal), box.Text);
            form.Close();
        }
    }

    /// <summary>
    /// 用户说话时走的完整路径：真实识别服务、协调器和悬浮窗。
    /// 只有音频来源换成了文件，麦克风的声音被丢弃，不会发出去。
    /// </summary>
    private static async Task RealCoordinatorScenario(string root, string wavPath, Reporter check)
    {
        byte[] wav = await File.ReadAllBytesAsync(wavPath);
        var store = new RecordStore(Path.Combine(root, "real"));
        WavFedProvider? fed = null;
        var coordinator = new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings { DataDirectory = store.DataDirectory, DeleteAudioAfterSuccess = false })
        {
            ProviderFactory = () => fed = new WavFedProvider(wav),
        };
        var failures = new List<string>();
        coordinator.Failed += (_, message) => failures.Add(message);
        QuanShuoRecord record = await store.CreateTextAsync(new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "t", WindowTitle = "t" }, TranscriptionStatus.Recording);
        var actions = new RecordingActions(
            async () => await coordinator.FinishAsync(),
            coordinator.TogglePause,
            async (updated, text) => await store.SaveTextAsync(updated, text),
            async updated => await coordinator.RetryAsync(updated),
            coordinator.NoteUserText);
        var window = new SpeechWindow(record, actions, null, null);
        coordinator.Finished += (_, completed) => window.SetFinished(completed);
        coordinator.TextMerger = window.MergeRecognized;
        window.Show();
        await coordinator.StartAsync(record);
        await fed!.Drained;
        LiveTextBox box = FindBox(window);
        PostKey(box, VkEscape);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (coordinator.IsRecording && waited.Elapsed.TotalSeconds < 40) await Task.Delay(50);
        await Task.Delay(300);
        QuanShuoRecord saved = (await store.ReadAllAsync()).Single();
        check("真实路径·没有报错", failures.Count == 0, string.Join(" | ", failures));
        check("真实路径·状态是已完成", saved.Status == TranscriptionStatus.Completed, saved.Status.ToString());
        check("真实路径·文字完整", saved.Text.Length > 0 && saved.Text == fed.Recognized, $"识别：{fed.Recognized}  保存：{saved.Text}");
        check("真实路径·窗口和文件一致", box.Text == saved.Text, box.Text);
        window.Close();
        await coordinator.DisposeAsync();
    }

    private static bool MatchesScale(LiveTextBox box, int dpi) => Math.Abs(box.Font.Size - 17 * dpi / 96F) < .6F;

    private static LiveTextBox FindBox(Form form) => form.Controls.OfType<LiveTextBox>().Single();

    private static void Post(Control control, int message, int code) => PostMessage(control.Handle, message, new IntPtr(code), IntPtr.Zero);

    private static void PostKey(Control control, int key)
    {
        PostMessage(control.Handle, WmKeyDown, new IntPtr(key), new IntPtr(1));
        PostMessage(control.Handle, WmKeyUp, new IntPtr(key), new IntPtr(unchecked((int)0xC0000001)));
    }

    /// <summary>发真实按键，依次按下、逆序抬起。窗口不在前台时什么也不发。</summary>
    private static bool Press(IntPtr window, params int[] keys)
    {
        if (GetForegroundWindow() != window) return false;
        var inputs = new List<Input>();
        foreach (int key in keys) inputs.Add(Key(key, 0));
        foreach (int key in keys.Reverse()) inputs.Add(Key(key, KeyUp));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        return true;
    }

    private static Input Key(int key, uint flags) => new() { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = flags } } };

    private static void ForceForeground(IntPtr window)
    {
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint current = GetCurrentThreadId();
        AttachThreadInput(current, foreground, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        AttachThreadInput(current, foreground, false);
    }

    private static void Shot(Form form, string path)
    {
        Application.DoEvents();
        Rectangle bounds = form.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>窗口后面垫一块纯色再截屏，连窗口外面一圈一起，看圆角和阴影。</summary>
    private static async Task ShotOver(Form form, Color color, string path)
    {
        Rectangle area = Rectangle.Inflate(form.Bounds, 70, 70);
        using var backdrop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = area, BackColor = color, ShowInTaskbar = false };
        backdrop.Show();
        await Task.Delay(200);
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>真实识别服务，但音频按说话速度来自文件。</summary>
    private sealed class WavFedProvider : ITranscriptionProvider
    {
        private const int HeaderBytes = 44;
        private const int ChunkBytes = 3200;
        private readonly DashScopeTranscriptionProvider real = new();
        private readonly byte[] wav;

        public WavFedProvider(byte[] wav)
        {
            this.wav = wav;
            real.TranscriptReceived += (_, update) => Recognized = update.Text;
        }

        public event EventHandler<TranscriptUpdate>? TranscriptReceived
        {
            add => real.TranscriptReceived += value;
            remove => real.TranscriptReceived -= value;
        }

        public string Recognized { get; private set; } = string.Empty;

        public Task Drained { get; private set; } = Task.CompletedTask;

        public async Task StartAsync(TranscriptionOptions options, CancellationToken cancellationToken)
        {
            await real.StartAsync(options, cancellationToken);
            Drained = Task.Run(async () =>
            {
                for (int offset = HeaderBytes; offset < wav.Length; offset += ChunkBytes)
                {
                    await real.SendAudioAsync(wav.AsMemory(offset, Math.Min(ChunkBytes, wav.Length - offset)), cancellationToken);
                    await Task.Delay(100, cancellationToken);
                }
            }, cancellationToken);
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task FinishAsync(CancellationToken cancellationToken)
        {
            await Drained;
            await real.FinishAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => real.DisposeAsync();
    }

    /// <summary>按脚本发结果的识别服务。发送的音频全部丢弃。</summary>
    private sealed class ScriptedProvider : ITranscriptionProvider
    {
        public event EventHandler<TranscriptUpdate>? TranscriptReceived;

        public string? FinalText { get; set; }

        public TimeSpan StartDelay { get; init; }

        public async Task EmitAsync(string text, bool isFinal)
        {
            // 从线程池发出，和真实连接的接收线程一样
            await Task.Run(() => TranscriptReceived?.Invoke(this, new TranscriptUpdate(text, isFinal)));
            await Task.Delay(80);
        }

        public Task StartAsync(TranscriptionOptions options, CancellationToken cancellationToken) => Task.Delay(StartDelay, cancellationToken);

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task FinishAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (FinalText is not null) TranscriptReceived?.Invoke(this, new TranscriptUpdate(FinalText, true));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool enable);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
