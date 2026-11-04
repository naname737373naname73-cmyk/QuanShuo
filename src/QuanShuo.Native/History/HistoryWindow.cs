using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Copying;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Audio;
using QuanShuo.Native.Capture;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Shell;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.History;

public sealed class HistoryWindow : Form
{
    private readonly RecordStore store;
    private readonly RecordIndex index;
    private readonly RecycleBinService recycleBin;
    private AppSettings settings;
    private readonly AppSettingsStore settingsStore;
    private readonly LocalAsrModelStatusStore localModelStatus = new();
    private readonly LocalAsrProfileStore localProfiles = new();
    private static readonly SemaphoreSlim settingsMutation = new(1, 1);
    private readonly DpapiSecretStore secrets;
    private readonly RecordingCoordinator recording;
    private readonly CaptureController capture;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly WebView2 webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = NativeTheme.Canvas };
    private RecordWatcher? watcher;
    private bool initialized;
    private bool frontendReady;
    private bool closeApproved;
    private readonly System.Windows.Forms.Timer closeGuard = new() { Interval = 2500 };

    public HistoryWindow(
        RecordStore store,
        RecordIndex index,
        RecycleBinService recycleBin,
        AppSettings settings,
        AppSettingsStore settingsStore,
        DpapiSecretStore secrets,
        RecordingCoordinator recording,
        CaptureController capture)
    {
        this.store = store;
        this.index = index;
        this.recycleBin = recycleBin;
        this.settings = settings;
        this.settingsStore = settingsStore;
        this.secrets = secrets;
        this.recording = recording;
        this.capture = capture;
        Text = "圈说";
        AutoScaleMode = AutoScaleMode.None;
        float scale = NativeTheme.ScaleAt(Cursor.Position);
        Size = new Size(NativeTheme.Px(1440, scale), NativeTheme.Px(920, scale));
        MinimumSize = new Size(NativeTheme.Px(1100, scale), NativeTheme.Px(720, scale));
        BackColor = NativeTheme.Canvas;
        ForeColor = NativeTheme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        Padding = new Padding(0, 1, 0, 0);
        StartPosition = FormStartPosition.CenterScreen;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "quanshuo.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        Controls.Add(webView);
        // 页面卡死时不能让窗口永远关不掉
        closeGuard.Tick += (_, _) =>
        {
            closeGuard.Stop();
            closeApproved = true;
            Close();
        };
    }

    public event EventHandler? HistoryClosed;
    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? SpeechRequested;

    public async void Open(string state = "normal")
    {
        Diagnostics.AppTrace.Log("history-open-start");
        Show();
        Diagnostics.AppTrace.Log("history-shown");
        if (initialized)
        {
            Activate();
            // 页面已经在了就不再导航：重新加载会丢掉刚输入还没落盘的文字、勾选和搜索。
            // 只有页面没加载成功时才重新导航。
            if (!frontendReady) webView.CoreWebView2?.Navigate(BuildUrl(state));
            else if (state != "normal") SendEvent("view.open", new { state });
            return;
        }
        initialized = true;
        try
        {
            Diagnostics.AppTrace.Log("history-index-start");
            await index.SynchronizeAsync();
            Diagnostics.AppTrace.Log("history-index-done");
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuanShuo", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            Diagnostics.AppTrace.Log("history-runtime-created");
            await webView.EnsureCoreWebView2Async(environment);
            Diagnostics.AppTrace.Log("history-runtime-ready");
            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            string frontend = Path.Combine(AppContext.BaseDirectory, "Frontend");
            if (!File.Exists(Path.Combine(frontend, "index.html")))
            {
                ShowInlineError("界面文件还没有构建。请先在 frontend 运行 npm run build。", null);
                return;
            }

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping("quanshuo.local", frontend, CoreWebView2HostResourceAccessKind.Allow);
            webView.CoreWebView2.Navigate(BuildUrl(state));
            watcher = new RecordWatcher(store.DataDirectory);
            watcher.Changed += OnRecordsChanged;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowInlineError("这台电脑还没有 WebView2 Runtime。安装后重新打开历史即可。", "https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowInlineError("历史窗口没有打开：" + exception.Message, null);
        }
        catch (Exception exception)
        {
            Diagnostics.AppTrace.Log("history-error-" + exception.GetType().Name);
            ShowInlineError("历史窗口没有打开：" + exception.Message, null);
        }
    }

    public void NotifyRecordCreated(QuanShuoRecord record) => SendEvent("records.created", record);

    public void NotifyRecordsChanged() => SendEvent("records.changed", new { });

    public void NotifyLevel(float level) => SendEvent("recording.level", level);

    public void NotifyTranscript(QuanShuoRecord record) => SendEvent("transcript.delta", new { id = record.Id, text = record.Text, isFinal = record.Status == TranscriptionStatus.Completed });

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheme.ApplyWindowFrame(Handle);
        NativeTheme.RefreshFrame(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083 && m.WParam != IntPtr.Zero)
        {
            NativeRect proposed = Marshal.PtrToStructure<NativeRect>(m.LParam);
            base.WndProc(ref m);
            NativeRect client = Marshal.PtrToStructure<NativeRect>(m.LParam);
            // 左右下沿使用系统计算值；最大化的外框在屏幕外，顶部也要补回同样厚度。
            client.Top = proposed.Top + (IsZoomed(Handle) ? NativeTheme.ResizeBorder(DeviceDpi) : 0);
            Marshal.StructureToPtr(client, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal)
        {
            long location = m.LParam.ToInt64();
            Point point = PointToClient(new Point((short)(location & 0xffff), (short)((location >> 16) & 0xffff)));
            if (point.Y >= 0 && point.Y < Padding.Top)
            {
                int border = NativeTheme.ResizeBorder(DeviceDpi);
                m.Result = new IntPtr(point.X < border ? 13 : point.X >= ClientSize.Width - border ? 14 : 12);
            }
            else if (m.Result == new IntPtr(2)) m.Result = new IntPtr(1);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 留一物理像素给系统上沿命中测试，避免 WebView 子窗口吞掉调整大小的鼠标消息。
        Padding = new Padding(0, WindowState == FormWindowState.Maximized ? 0 : 1, 0, 0);
        SendWindowState();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        MinimumSize = new Size(NativeTheme.Px(1100, e.DeviceDpiNew / 96F), NativeTheme.Px(720, e.DeviceDpiNew / 96F));
        base.OnDpiChanged(e);
        NativeTheme.RefreshFrame(Handle);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        frontendReady = e.IsSuccess;
        SendWindowState();
    }

    private void SendWindowState() => SendEvent("window.state", new { maximized = WindowState == FormWindowState.Maximized });

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 和任务栏关闭也要先让前端把防抖中的文字写完。关机时不等。
        if (!closeApproved && frontendReady && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            closeGuard.Stop();
            closeGuard.Start();
            SendEvent("window.closeRequested", new { });
            return;
        }

        closeGuard.Dispose();
        watcher?.Dispose();
        watcher = null;
        if (webView.CoreWebView2 is not null)
        {
            webView.CoreWebView2.WebMessageReceived -= OnWebMessage;
            webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
        webView.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        HistoryClosed?.Invoke(this, EventArgs.Empty);
        base.OnFormClosed(e);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string id = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = document.RootElement;
            id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            string type = root.GetProperty("type").GetString() ?? string.Empty;
            Diagnostics.AppTrace.Log("web-message-" + type);
            JsonElement payload = root.TryGetProperty("payload", out JsonElement payloadElement) ? payloadElement.Clone() : default;
            object? response = await HandleAsync(type, payload);
            Reply(id, response, null);
        }
        catch (Exception exception)
        {
            string message = exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                ? exception.Message
                : "操作没有完成。请检查文件和设置后重试。";
            Reply(id, null, message);
        }
    }

    private async Task<object?> HandleAsync(string type, JsonElement payload)
    {
        switch (type)
        {
            case "records.query":
            {
                string query = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("query", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
                return await index.QueryAsync(query);
            }
            case "record.thumbnail":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                return await ImageDataService.ThumbnailAsync(record.ImagePath, GetInt(payload, "width", 240));
            }
            case "record.image":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                return await ImageDataService.FullImageAsync(record.ImagePath);
            }
            case "record.drag":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                var data = new DataObject(DataFormats.FileDrop, new[] { record.ImagePath });
                webView.DoDragDrop(data, DragDropEffects.Copy);
                return null;
            }
            case "record.update":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                string text = payload.GetProperty("text").GetString() ?? string.Empty;
                if (!await store.SaveTextAsync(record, text))
                    throw new FileNotFoundException("这条记录已不在索引中。请刷新后检查文件是否被移动或删除。", record.MarkdownPath);
                await index.SynchronizeAsync();
                SendEvent("records.changed", new { id = record.Id });
                return null;
            }
            case "record.delete":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                await recycleBin.DeleteAsync(record);
                await index.SynchronizeAsync();
                SendEvent("records.changed", new { id = record.Id });
                return null;
            }
            case "records.delete":
            {
                var records = new List<QuanShuoRecord>();
                foreach (JsonElement item in payload.GetProperty("ids").EnumerateArray())
                {
                    string id = item.GetString() ?? throw new InvalidOperationException("消息缺少记录 ID。");
                    if (id == recording.Current?.Id) throw new InvalidOperationException("这条还在录音，结束后才能删。");
                    records.Add(await FindRecordAsync(id));
                }

                await recycleBin.DeleteAsync(records);
                await index.SynchronizeAsync();
                SendEvent("records.changed", new { });
                return null;
            }
            case "record.undo":
            {
                string id = RequiredId(payload);
                bool restored = await recycleBin.UndoAsync();
                await index.SynchronizeAsync();
                SendEvent("records.changed", new { id });
                return restored ? await FindRecordAsync(id) : null;
            }
            case "record.retry":
            {
                var record = await FindRecordAsync(RequiredId(payload));
                var updated = await recording.RetryAsync(record);
                await index.SynchronizeAsync();
                return updated;
            }
            case "clipboard.copy":
                return await Copy(payload);
            case "settings.get":
                await settingsMutation.WaitAsync();
                try { return recording.Settings; }
                finally { settingsMutation.Release(); }
            case "settings.update":
            {
                if (!settingsMutation.Wait(0)) throw new InvalidOperationException("正在保存设置或切换模型，请稍后再试。");
                try
                {
                var updated = JsonSerializer.Deserialize<AppSettings>(payload.GetRawText(), jsonOptions) ?? throw new InvalidOperationException("设置内容为空，未保存。");
                if (updated.SettingsRevision != recording.Settings.SettingsRevision)
                    throw new InvalidOperationException("设置已在另一窗口更新，请重新打开设置后保存。");
                updated = updated with { SettingsRevision = recording.Settings.SettingsRevision + 1 };
                if (string.IsNullOrWhiteSpace(updated.DataDirectory)) throw new InvalidOperationException("数据目录不能为空。");
                SpeechProviders.Validate(updated);
                if (updated.FixedDayPeriod is < 0 or > 3) throw new InvalidOperationException("固定时段必须是 0–3，或留空跟随系统时间。");
                if (updated.Skin is not ("starlake" or "crabapple")) throw new InvalidOperationException("没有这个皮肤。");
                QuanShuo.Native.Input.GlobalHotkeyService.ValidateHotkey(updated.Hotkey);
                QuanShuo.Native.Input.GlobalHotkeyService.ValidateHotkey(updated.SpeechHotkey);
                if (string.Equals(updated.Hotkey, updated.SpeechHotkey, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("圈选和说话不能用同一个快捷键。");
                ColorTranslator.FromHtml(updated.FreeformFillColor);
                await settingsStore.SaveAsync(updated);
                settings = updated;
                SettingsChanged?.Invoke(this, updated);
                // 页面换皮肤时会重新加载，这一下的底色要跟新皮肤一致，否则闪一下
                BackColor = NativeTheme.Canvas;
                webView.DefaultBackgroundColor = NativeTheme.Canvas;
                SendEvent("settings.changed", updated);
                return updated;
                }
                finally { settingsMutation.Release(); }
            }
            case "secret.set":
                await secrets.SaveApiKeyAsync(payload.GetProperty("apiKey").GetString() ?? string.Empty);
                return null;
            case "audio.devices":
                return WavRecorder.GetDevices();
            case "connection.test":
            {
                AppSettings? requestedSettings = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("settings", out var snapshot)
                    ? JsonSerializer.Deserialize<AppSettings>(snapshot.GetRawText(), jsonOptions)
                        ?? throw new InvalidOperationException("测试设置内容为空。")
                    : null;
                AppSettings tested = requestedSettings ?? recording.Settings;
                try
                {
                    var result = await recording.TestConnectionDetailsAsync(requestedSettings: tested);
                    LocalAsrModelStatus? modelStatus = null;
                    bool saved = true;
                    if (SpeechProviders.IsLocal(tested))
                    {
                        modelStatus = new(tested.LocalSpeechEndpoint.Trim(), tested.LocalSpeechModel.Trim(), true, DateTimeOffset.UtcNow, result.LocalModel);
                        saved = await localModelStatus.SaveAsync(modelStatus);
                    }
                    return new { ok = true, message = result.Message + (saved ? "" : " 模型资料未能保存，重新打开设置后需再测试。"), modelStatus };
                }
                catch
                {
                    if (SpeechProviders.IsLocal(tested))
                        await localModelStatus.SaveAsync(new(tested.LocalSpeechEndpoint.Trim(), tested.LocalSpeechModel.Trim(), false, DateTimeOffset.UtcNow, null));
                    throw;
                }
            }
            case "localAsr.modelInfo":
                await settingsMutation.WaitAsync();
                try { return await localModelStatus.ReadAsync(); }
                finally { settingsMutation.Release(); }
            case "localAsr.profiles":
                return await localProfiles.ReadAsync();
            case "localAsr.switchProfile":
            {
                if (!settingsMutation.Wait(0)) throw new InvalidOperationException("正在保存设置或切换模型，请稍后再试。");
                try
                {
                    string id = payload.GetProperty("id").GetString() ?? string.Empty;
                    var expected = JsonSerializer.Deserialize<LocalAsrProfile>(payload.GetProperty("profile").GetRawText(), jsonOptions)
                        ?? throw new InvalidOperationException("缺少模型登记资料，请刷新列表。");
                    var previous = JsonSerializer.Deserialize<AppSettings>(payload.GetProperty("expectedCurrent").GetRawText(), jsonOptions)
                        ?? throw new InvalidOperationException("缺少当前识别配置，请重新打开设置。");
                    var profile = await localProfiles.ResolveAsync(id, expected);
                    LocalModelSwitchResult result;
                    try
                    {
                        result = await recording.SwitchLocalModelAsync(profile, previous, async updated =>
                        {
                            await localProfiles.ResolveAsync(id, expected);
                            await settingsStore.SaveAsync(updated);
                        });
                    }
                    catch
                    {
                        var current = recording.Settings;
                        if (SpeechProviders.IsLocal(current))
                            await localModelStatus.SaveAsync(new(current.LocalSpeechEndpoint, current.LocalSpeechModel, false, DateTimeOffset.UtcNow, null));
                        throw;
                    }
                    settings = result.Settings;
                    SettingsChanged?.Invoke(this, settings);
                    SendEvent("settings.changed", settings);
                    bool cached = await localModelStatus.SaveAsync(result.ModelStatus);
                    return new { settings, modelStatus = result.ModelStatus, message = "已切换到 " + LocalAsrCatalog.Name(id) + (cached ? "。" : "。模型资料未能缓存，下次打开请重新测试。") };
                }
                finally { settingsMutation.Release(); }
            }
            case "localAsr.openModelDocs":
            {
                string url = payload.GetProperty("model").GetString() switch
                {
                    "sherpa" => "https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-paraformer/paraformer-models.html",
                    "qwen" => "https://github.com/QwenLM/Qwen3-ASR",
                    _ => throw new InvalidOperationException("没有这份模型资料。"),
                };
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            case "localAsr.copySetupPrompt":
                ClipboardService.CopyText(LocalAsrSetupPrompt.Read(PromptModelId(payload)));
                return null;
            case "localAsr.copyAssessmentPrompt":
                ClipboardService.CopyText(LocalAsrSetupPrompt.ReadAssessment(PromptModelId(payload)));
                return null;
            case "system.microphoneSettings":
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
                return null;
            case "capture.test":
                capture.BeginCapture();
                return null;
            case "speech.open":
                SpeechRequested?.Invoke(this, EventArgs.Empty);
                return null;
            case "onboarding.complete":
            {
                if (!settingsMutation.Wait(0)) throw new InvalidOperationException("正在保存设置或切换模型，请稍后再试。");
                try
                {
                    var updated = recording.Settings with { FirstRunCompleted = true, SettingsRevision = recording.Settings.SettingsRevision + 1 };
                    await settingsStore.SaveAsync(updated);
                    settings = updated;
                    SettingsChanged?.Invoke(this, settings);
                    return null;
                }
                finally { settingsMutation.Release(); }
            }
            case "scene.day":
                NativeTheme.UseSceneDay(payload.GetProperty("day").GetDouble());
                return null;
            case "window.keepOpen":
                closeGuard.Stop();
                return null;
            case "window.close":
                closeApproved = true;
                closeGuard.Stop();
                BeginInvoke(new Action(Close));
                return null;
            case "window.minimize":
                WindowState = FormWindowState.Minimized;
                return null;
            case "window.toggleMaximize":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                return null;
            default:
                throw new InvalidOperationException("原生层不认识消息：" + type);
        }
    }

    private async Task<object?> Copy(JsonElement payload)
    {
        string operation = payload.TryGetProperty("operation", out JsonElement operationElement) ? operationElement.GetString() ?? "ai" : "ai";
        string[] ids = payload.GetProperty("ids").EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(id => id.Length > 0).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("没有选中的记录，未改动剪贴板。");
        // 全选时可能有上千条，只查一次索引
        ILookup<string, QuanShuoRecord> known = (await index.QueryAsync(string.Empty)).ToLookup(record => record.Id);
        var records = ids.Select(id => known[id].FirstOrDefault() ?? throw new FileNotFoundException("这条记录已不在索引中。请刷新后检查文件是否被移动或删除。", id)).ToList();
        switch (operation)
        {
            case "ai": ClipboardService.CopyForAI(records); break;
            case "image-path": ClipboardService.CopyFilePath(records[0].ImagePath); break;
            case "markdown-path": ClipboardService.CopyFilePath(records[0].MarkdownPath); break;
            case "text":
            {
                string text = AiCopyFormatter.JoinTexts(records);
                if (text.Length == 0) throw new InvalidOperationException("选中的记录还没有文字，未改动剪贴板。");
                ClipboardService.CopyText(text);
                break;
            }
            case "image": ClipboardService.CopyImage(records[0].ImagePath); break;
            default: throw new InvalidOperationException("不支持的复制方式：" + operation);
        }

        return null;
    }

    private async Task<QuanShuoRecord> FindRecordAsync(string id)
    {
        var records = await index.QueryAsync(string.Empty);
        return records.FirstOrDefault(record => record.Id == id) ?? throw new FileNotFoundException("这条记录已不在索引中。请刷新后检查文件是否被移动或删除。", id);
    }

    private static string? PromptModelId(JsonElement payload) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("modelId", out var id) ? id.GetString() : null;

    private static string RequiredId(JsonElement payload) => payload.GetProperty("id").GetString() ?? throw new InvalidOperationException("消息缺少记录 ID。");

    private static int GetInt(JsonElement payload, string name, int fallback) => payload.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : fallback;

    private async void OnRecordsChanged(object? sender, EventArgs e)
    {
        try
        {
            await index.SynchronizeAsync();
            // 监听器回调在线程池线程，发 WebView 消息必须回到 UI 线程
            BeginInvoke(new Action(() => SendEvent("records.changed", new { })));
        }
        catch (IOException exception)
        {
            BeginInvoke(new Action(() => SendEvent("error", new { message = "文件变化暂时无法同步：" + exception.Message })));
        }
    }

    private void SendEvent(string type, object? payload)
    {
        if (IsDisposed || webView.IsDisposed || webView.CoreWebView2 is null) return;
        string json = JsonSerializer.Serialize(new { type, payload }, jsonOptions);
        webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void Reply(string id, object? payload, string? error)
    {
        if (string.IsNullOrEmpty(id) || webView.CoreWebView2 is null) return;
        string json = error is null
            ? JsonSerializer.Serialize(new { id, payload }, jsonOptions)
            : JsonSerializer.Serialize(new { id, error }, jsonOptions);
        webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private string BuildUrl(string state)
    {
        string url = $"https://quanshuo.local/index.html?state={Uri.EscapeDataString(state)}";
        if (settings.FixedDayPeriod is >= 0 and <= 3) url += $"&day={settings.FixedDayPeriod.Value}";
        if (settings.QuietMode) url += "&quiet=1";
        if (settings.Skin == "crabapple") url += "&skin=crabapple";
        return url;
    }

    private void ShowInlineError(string message, string? link)
    {
        webView.Visible = false;
        float scale = DeviceDpi / 96F;
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(NativeTheme.Px(32, scale)), BackColor = NativeTheme.Canvas, ForeColor = NativeTheme.Text };
        var font = NativeTheme.TextFont(15, scale);
        panel.Disposed += (_, _) => font.Dispose();
        panel.Font = font;
        var label = new Label { Dock = DockStyle.Top, Height = NativeTheme.Px(120, scale), Text = message };
        panel.Controls.Add(label);
        if (link is not null)
        {
            var linkLabel = new LinkLabel { Dock = DockStyle.Top, Text = "打开 WebView2 官方安装页", Height = NativeTheme.Px(48, scale), LinkColor = NativeTheme.Accent, ActiveLinkColor = NativeTheme.Text, VisitedLinkColor = NativeTheme.TextMuted };
            linkLabel.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            panel.Controls.Add(linkLabel);
            panel.Controls.SetChildIndex(linkLabel, 0);
        }

        Controls.Add(panel);
        panel.BringToFront();
        var titlebar = new Panel { Dock = DockStyle.Top, Height = NativeTheme.Px(48, scale), BackColor = NativeTheme.SurfaceRaised };
        titlebar.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) NativeTheme.DragWindow(Handle); };
        var close = new GlassButton { Text = "关闭", Dock = DockStyle.Right, Width = NativeTheme.Px(80, scale) };
        close.Click += (_, _) => Close();
        titlebar.Controls.Add(close);
        Controls.Add(titlebar);
        titlebar.BringToFront();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);
}
