using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Native.Capture;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.History;
using QuanShuo.Native.Input;
using QuanShuo.Native.Recording;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.Shell;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const int CaptureHideDelayMilliseconds = 80;
    private const string ModelSwitchBusyMessage = "正在切换本地模型，请等切换结束后再开始录音。";
    private readonly NotifyIcon trayIcon;
    private readonly TrayIconService trayIconService;
    private readonly ForegroundContextService foregroundContext;
    private readonly GlobalHotkeyService? hotkey;
    private GlobalHotkeyService? speechHotkey;
    private readonly CaptureController captureController;
    private readonly RecordingCoordinator recordingCoordinator;
    private readonly RecordStore store;
    private readonly RecordIndex historyIndex;
    private readonly RecycleBinService recycleBin;
    private readonly AppSettingsStore settingsStore;
    private readonly DpapiSecretStore secrets;
    private readonly TrayMenu trayMenu;
    private AppSettings settings;
    private RecordingCard? recordingCard;
    private SpeechWindow? speechWindow;
    private RecordingSurface? activeSurface;
    private HistoryWindow? historyWindow;
    private Rectangle? lastSpeechBounds;
    private bool openingSpeech;

    public TrayApplicationContext()
    {
        settingsStore = new AppSettingsStore();
        settings = settingsStore.LoadAsync().GetAwaiter().GetResult();
        NativeTheme.UseSkin(settings.Skin);
        NativeTheme.UseSceneDay(settings.FixedDayPeriod);
        StartupRegistration.Apply(settings.AutoStart);
        secrets = new DpapiSecretStore();
        store = new RecordStore(settings.DataDirectory);
        historyIndex = new RecordIndex(store);
        recycleBin = new RecycleBinService(store.DataDirectory);
        foregroundContext = new ForegroundContextService();
        recordingCoordinator = new RecordingCoordinator(store, secrets, settings);
        captureController = new CaptureController(new ScreenCaptureService(foregroundContext), store)
        {
            LastSelectionMode = settings.LastSelectionMode,
            FreeformFillColor = ColorTranslator.FromHtml(settings.FreeformFillColor),
            AutoSpeakAfterSelection = settings.AutoSpeakAfterSelection,
            QuietMode = settings.QuietMode,
        };
        captureController.CaptureFailed += (_, message) => ShowError(message);
        captureController.RecordingRequested += (_, record) => OpenRecordingCard(record);
        captureController.RecordCreated += async (_, record) =>
        {
            await historyIndex.SynchronizeAsync();
            historyWindow?.NotifyRecordCreated(record);
            await UpdateTrayIconAsync();
        };
        captureController.BeforeCaptureAsync = async () =>
        {
            if (recordingCoordinator.IsRecording) await recordingCoordinator.FinishAsync();
            // Finished 是投递到同一 UI 队列的通知，先让窗口收到它再保存手动修改。
            await Task.Yield();
            if (speechWindow is { IsDisposed: false } window)
            {
                if (window.IsListening) throw new InvalidOperationException("录音尚未结束，请结束后再截图。");
                await window.SaveEditedTextAsync();
                // 保存失败时始终保持窗口可见；Close 也可能因收尾期间的新编辑而取消。
                window.Close();
                if (!window.IsDisposed)
                    throw new IOException("窗口里的文字尚未保存完成，请处理后再截图。");
                await Task.Delay(CaptureHideDelayMilliseconds);
            }
        };

        recordingCoordinator.Started += (_, _) => _ = UpdateTrayIconAsync();
        recordingCoordinator.TranscriptChanged += (_, record) => historyWindow?.NotifyTranscript(record);
        recordingCoordinator.LevelChanged += (_, value) =>
        {
            activeSurface?.SetLevel(value);
            historyWindow?.NotifyLevel(value);
        };
        recordingCoordinator.PauseChanged += (_, state) => activeSurface?.SetPaused(state.Paused, state.Automatic);
        recordingCoordinator.Stopping += (_, id) =>
        {
            foreach (RecordingSurface? surface in new RecordingSurface?[] { recordingCard, speechWindow })
                if (surface is { IsDisposed: false } && surface.RecordId == id) surface.NotifyRecordingStopping();
        };
        recordingCoordinator.Finished += async (_, record) =>
        {
            QuanShuoRecord visibleRecord = record;
            // 按记录找窗口：这个通知是排队送达的，到的时候可能已经开始下一条录音了
            foreach (RecordingSurface? surface in new RecordingSurface?[] { recordingCard, speechWindow })
            {
                if (surface is { IsDisposed: false } && surface.RecordId == record.Id)
                {
                    // 用户仍可能在最终写盘的 await 期间输入。空记录删除必须检查此刻的全文。
                    visibleRecord = record with { Text = surface.CaptureFinalText() };
                    surface.SetFinished(record);
                }
            }

            if (!visibleRecord.HasImage) await FinishSpeechRecordAsync(visibleRecord);
            await UpdateTrayIconAsync();
        };
        recordingCoordinator.Failed += (_, message) => activeSurface?.SetError(message);

        string? startupError = null;
        try
        {
            hotkey = new GlobalHotkeyService(settings.Hotkey);
            hotkey.HotkeyPressed += (_, _) => captureController.BeginCapture();
        }
        catch (InvalidOperationException exception)
        {
            startupError = exception.Message;
        }

        try
        {
            RegisterSpeechHotkey(settings.SpeechHotkey);
        }
        catch (InvalidOperationException exception)
        {
            startupError ??= exception.Message;
        }

        trayMenu = new TrayMenu(settings.Hotkey, settings.SpeechHotkey,
            () => captureController.BeginCapture(), ToggleSpeech,
            () => OpenHistory(), () => OpenHistory("settings"), async () => await ExitAsync());

        trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "圈说 · " + settings.Hotkey + " 圈出想说的",
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        trayIcon.DoubleClick += (_, _) => OpenHistory();
        trayIconService = new TrayIconService(trayIcon) { QuietMode = settings.QuietMode };
        _ = UpdateTrayIconAsync();
        if (startupError is not null)
        {
            ShowError(startupError);
            // 快捷键被占用时直接打开设置页，让用户当场改一个可用的组合
            SynchronizationContext.Current?.Post(_ => OpenHistory("settings"), null);
        }

        if (!settings.FirstRunCompleted) SynchronizationContext.Current?.Post(_ => OpenHistory("onboarding"), null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (historyWindow is not null && !historyWindow.IsDisposed) historyWindow.Close();
            captureController.Dispose();
            recordingCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            recordingCard?.Dispose();
            speechWindow?.Dispose();
            hotkey?.Dispose();
            speechHotkey?.Dispose();
            trayIconService.Dispose();
            trayIcon.Visible = false;
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    public void ShowHistory(string state = "normal") => OpenHistory(state);

    private void OpenHistory(string state = "normal")
    {
        Diagnostics.AppTrace.Log("open-history-" + state);
        if (historyWindow is null || historyWindow.IsDisposed)
        {
            historyWindow = new HistoryWindow(store, historyIndex, recycleBin, settings, settingsStore, secrets, recordingCoordinator, captureController);
            historyWindow.HistoryClosed += (_, _) =>
            {
                historyWindow = null;
                // 手动拖过的时段只在主窗口开着时算数
                NativeTheme.UseSceneDay(settings.FixedDayPeriod);
            };
            historyWindow.SettingsChanged += (_, updated) => ApplySettings(updated);
            historyWindow.SpeechRequested += (_, _) => ToggleSpeech();
        }

        historyWindow.Open(state);
    }

    private void ApplySettings(AppSettings updated)
    {
        settings = updated;
        NativeTheme.UseSkin(updated.Skin);
        NativeTheme.UseSceneDay(updated.FixedDayPeriod);
        hotkey?.Update(updated.Hotkey);
        if (speechHotkey is null) RegisterSpeechHotkey(updated.SpeechHotkey);
        else speechHotkey.Update(updated.SpeechHotkey);
        trayMenu.UpdateHotkeys(updated.Hotkey, updated.SpeechHotkey);
        trayIcon.Text = "圈说 · " + updated.Hotkey + " 圈出想说的";
        StartupRegistration.Apply(updated.AutoStart);
        captureController.LastSelectionMode = updated.LastSelectionMode;
        captureController.FreeformFillColor = ColorTranslator.FromHtml(updated.FreeformFillColor);
        captureController.AutoSpeakAfterSelection = updated.AutoSpeakAfterSelection;
        captureController.QuietMode = updated.QuietMode;
        trayIconService.QuietMode = updated.QuietMode;
        if (recordingCard is not null && !recordingCard.IsDisposed) recordingCard.QuietMode = updated.QuietMode;
        if (speechWindow is not null && !speechWindow.IsDisposed) speechWindow.QuietMode = updated.QuietMode;
        recordingCoordinator.UpdateSettings(updated);
    }

    private async void OpenRecordingCard(QuanShuoRecord record)
    {
        try
        {
            if (await RejectRecordingDuringModelSwitchAsync(record)) return;
            if (recordingCoordinator.IsRecording) await recordingCoordinator.FinishAsync();
            // 上一张卡片可能还有防抖中的修改，销毁前写完
            if (recordingCard is not null && !recordingCard.IsDisposed) await recordingCard.SaveEditedTextAsync();
            if (await RejectRecordingDuringModelSwitchAsync(record)) return;
            recordingCard?.Dispose();
            recordingCard = new RecordingCard(record, BuildRecordingActions()) { QuietMode = settings.QuietMode };
            await StartSurfaceAsync(recordingCard, record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            recordingCard?.SetError("上一条文字尚未保存：" + exception.Message);
            ShowError("上一条文字尚未保存：" + exception.Message);
        }
    }

    /// <summary>快捷键：没开就开始说，正在说就结束并贴回去。</summary>
    private async void ToggleSpeech()
    {
        if (speechWindow is { IsDisposed: false, Visible: true } open)
        {
            if (open.IsListening) open.Deliver();
            else open.Activate();
            return;
        }

        if (openingSpeech) return;
        if (recordingCoordinator.IsModelSwitching)
        {
            ShowError(ModelSwitchBusyMessage);
            return;
        }
        openingSpeech = true;
        try
        {
            // 先记下目标窗口：之后的异步操作里前台可能已经变了
            PasteTarget? target = PasteService.ForegroundTarget();
            var metadata = foregroundContext.CaptureMetadata();
            if (recordingCoordinator.IsRecording) await recordingCoordinator.FinishAsync();
            if (recordingCoordinator.IsModelSwitching)
            {
                ShowError(ModelSwitchBusyMessage);
                return;
            }
            speechWindow?.Dispose();
            QuanShuoRecord record = await store.CreateTextAsync(metadata, TranscriptionStatus.Recording);
            // 写入记录会让出 UI 线程；这期间可能刚好开始切换，不能再打开录音窗口。
            if (await RejectRecordingDuringModelSwitchAsync(record)) return;
            var window = new SpeechWindow(record, BuildRecordingActions(), target, lastSpeechBounds) { QuietMode = settings.QuietMode };
            window.FormClosing += (_, _) => lastSpeechBounds = window.Bounds;
            window.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(speechWindow, window)) speechWindow = null;
            };
            speechWindow = window;
            await StartSurfaceAsync(window, record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError("说话窗口没有打开：" + exception.Message);
        }
        finally
        {
            openingSpeech = false;
        }
    }

    private async Task<bool> RejectRecordingDuringModelSwitchAsync(QuanShuoRecord record)
    {
        if (!recordingCoordinator.IsModelSwitching) return false;
        await SaveUnstartedRecordAsync(record);
        historyWindow?.NotifyRecordsChanged();
        ShowError(ModelSwitchBusyMessage);
        return true;
    }

    private async Task SaveUnstartedRecordAsync(QuanShuoRecord record)
    {
        if (!await store.SaveTextAsync(record, record.Text, TranscriptionStatus.Incomplete))
            throw new IOException("记录文件已被移动或删除，未能保存录音未开始的状态。");
    }

    private async Task StartSurfaceAsync(RecordingSurface surface, QuanShuoRecord record)
    {
        activeSurface = surface;
        recordingCoordinator.TextMerger = surface.MergeRecognized;
        recordingCoordinator.FinalTextSnapshot = surface.CaptureFinalText;
        surface.Show();
        try
        {
            await recordingCoordinator.StartAsync(record);
        }
        catch (Exception exception)
        {
            string message = TranscriptionErrors.FriendlyMessage(exception);
            if (recordingCoordinator.Current?.Id != record.Id)
            {
                message = "录音没有开始：" + exception.Message;
                // Gate 或配置校验可能在建立 current 前拒绝启动，因此不会收到 Finished。
                // 手动文字以窗口为准，先保存为未完成，再释放窗口的结束等待和关闭保护。
                var incomplete = record with { Text = surface.CaptureFinalText(), Status = TranscriptionStatus.Incomplete };
                string? saveError = null;
                try { await SaveUnstartedRecordAsync(incomplete); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveError = error.Message; }
                if (ReferenceEquals(recordingCoordinator.TextMerger?.Target, surface)) recordingCoordinator.TextMerger = null;
                if (ReferenceEquals(recordingCoordinator.FinalTextSnapshot?.Target, surface)) recordingCoordinator.FinalTextSnapshot = null;
                if (ReferenceEquals(activeSurface, surface)) activeSurface = null;
                // 保存失败时仍将手动修改保留为待保存内容，不把旧 record.Text 当作当前全文。
                surface.SetFinished(saveError is null ? incomplete : incomplete with { Text = record.Text });
                if (saveError is null && !surface.IsDisposed)
                {
                    try { await surface.SaveEditedTextAsync(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveError = error.Message; }
                }
                if (saveError is not null) message += " 文字尚未保存：" + saveError;
                historyWindow?.NotifyRecordsChanged();
            }
            if (!surface.IsDisposed) surface.SetError(message);
        }
    }

    private RecordingActions BuildRecordingActions() => new(
        async () => await recordingCoordinator.FinishAsync(),
        recordingCoordinator.TogglePause,
        async (updated, text) =>
        {
            if (!await store.SaveTextAsync(updated, text))
                throw new IOException("记录文件已被移动或删除。请恢复文件后重试；窗口里的文字仍保留。");
        },
        async updated => await recordingCoordinator.RetryAsync(updated),
        recordingCoordinator.NoteUserText);

    /// <summary>什么都没说的不留记录，说了的才算一颗星。</summary>
    private async Task FinishSpeechRecordAsync(QuanShuoRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Text) && record.Status == TranscriptionStatus.Completed) store.Discard(record);
        await historyIndex.SynchronizeAsync();
        historyWindow?.NotifyRecordsChanged();
    }

    private void RegisterSpeechHotkey(string combination)
    {
        var service = new GlobalHotkeyService(combination);
        service.HotkeyPressed += (_, _) => ToggleSpeech();
        speechHotkey = service;
    }

    // 退出要等录音收尾：文字合并依赖消息循环，消息循环结束后就只能卡住或丢字
    private async Task ExitAsync()
    {
        try
        {
            if (recordingCoordinator.IsRecording) await recordingCoordinator.FinishAsync();
            await Task.Yield();
            foreach (RecordingSurface? surface in new RecordingSurface?[] { recordingCard, speechWindow })
            {
                if (surface is { IsDisposed: false }) await surface.SaveEditedTextAsync();
            }
            ExitThreadCore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            activeSurface?.SetError("文字尚未保存，暂不退出：" + exception.Message);
            ShowError("文字尚未保存，暂不退出：" + exception.Message);
        }
    }

    private async Task UpdateTrayIconAsync()
    {
        var records = await store.ReadAllAsync();
        int count = records.Count(record => record.Timestamp.LocalDateTime.Date == DateTime.Today);
        trayIconService.Update(count, recordingCoordinator.IsRecording);
    }

    private void ShowError(string message) => trayIcon.ShowBalloonTip(5000, "圈说需要处理一个问题", message, ToolTipIcon.Warning);

    private static Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "quanshuo.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}
