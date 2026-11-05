using System.Threading.Channels;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Audio;
using QuanShuo.Native.Configuration;

namespace QuanShuo.Native.Transcription;

public sealed record ConnectionTestResult(string Message, LocalModelInfo? LocalModel = null);
public sealed record LocalModelSwitchResult(AppSettings Settings, LocalAsrModelStatus ModelStatus);

public sealed class RecordingCoordinator : IAsyncDisposable
{
    private const int WavHeaderBytes = 44;
    private const int PcmBytesPerSecond = 32000;
    private const int FileChunkBytes = 6400;
    private readonly RecordStore store;
    private readonly DpapiSecretStore secrets;
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private readonly SemaphoreSlim finishLock = new(1, 1);
    private AppSettings settings;
    private AppSettings? recordingSettings;
    private QuanShuoRecord? current;
    private WavRecorder? recorder;
    private ITranscriptionProvider? provider;
    private Channel<byte[]>? audioChannel;
    private Channel<string>? transcriptChannel;
    private Task? sendTask;
    private CancellationTokenSource? sendCancellation;
    private Task? persistenceTask;
    private Exception? providerFailure;
    private string latestText = string.Empty;
    private volatile bool streamingAudio;
    private bool transcribeAfterRecording;
    private TaskCompletionSource? startup;
    private int connectionTesting;
    private readonly ModelActivityGate modelActivity = new();

    public RecordingCoordinator(RecordStore store, DpapiSecretStore secrets, AppSettings settings)
    {
        this.store = store;
        this.secrets = secrets;
        this.settings = settings;
    }

    public event EventHandler<QuanShuoRecord>? Started;
    public event EventHandler<string>? Stopping;
    public event EventHandler<QuanShuoRecord>? Finished;
    public event EventHandler<QuanShuoRecord>? TranscriptChanged;
    public event EventHandler<float>? LevelChanged;
    public event EventHandler<RecordingPauseState>? PauseChanged;
    public event EventHandler<string>? Failed;

    public bool IsRecording => current is not null;
    public bool IsModelSwitching => modelActivity.IsSwitching;
    public AppSettings Settings => settings;
    public QuanShuoRecord? Current => current;

    /// <summary>
    /// 录音时用户可以改文字，所以识别结果不能直接当成全文。
    /// 有界面时由它把识别结果并进文字框并返回合并后的全文；只在 UI 线程调用。
    /// </summary>
    public Func<string, bool, string>? TextMerger { get; set; }

    /// <summary>在 UI 队列处理完识别消息后，合并输入法/拖选暂存内容并取得最终全文。</summary>
    public Func<string>? FinalTextSnapshot { get; set; }

    /// <summary>诊断用：换掉识别服务，真实的录音和保存流程不变。</summary>
    internal Func<ITranscriptionProvider>? ProviderFactory { get; init; }

    internal TimeSpan AudioDrainTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void UpdateSettings(AppSettings updated)
    {
        if (modelActivity.IsSwitching) throw new InvalidOperationException("正在切换本地模型，请稍后保存设置。");
        settings = updated;
    }

    public async Task StartAsync(QuanShuoRecord record, CancellationToken cancellationToken = default)
    {
        using var modelUse = modelActivity.EnterUse();
        if (current is not null) await FinishAsync(cancellationToken);
        AppSettings configuration = settings;
        SpeechProviders.Validate(configuration);
        recordingSettings = configuration;
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startup = starting;
        providerFailure = null;
        latestText = record.Text;
        string audioPath = store.GetAudioPath(record);
        current = record with { AudioPath = audioPath, Status = TranscriptionStatus.Recording };
        // 100ms 一帧，最多保留约 100 秒待发送音频；完整 WAV 始终由录音器落盘。
        audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1000) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        transcriptChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        persistenceTask = PersistTranscriptsAsync(transcriptChannel.Reader, current, cancellationToken);
        bool local = SpeechProviders.IsLocal(configuration);
        string apiKey = local ? string.Empty : secrets.LoadApiKey();
        bool hasKey = local || !string.IsNullOrWhiteSpace(apiKey);
        SpeechModelKind kind = local ? SpeechModelKind.Streaming : SpeechModels.KindOf(configuration.SpeechModel);
        streamingAudio = hasKey && kind == SpeechModelKind.Streaming;
        transcribeAfterRecording = hasKey && kind == SpeechModelKind.WholeFile;
        recorder = new WavRecorder();
        recorder.AudioAvailable += OnAudioAvailable;
        recorder.SilenceAvailable += OnSilenceAvailable;
        recorder.RecordingFailed += OnRecorderFailed;
        recorder.PauseChanged += OnRecorderPauseChanged;
        try
        {
            recorder.Start(audioPath, ResolveMicrophone(configuration.MicrophoneDeviceId));
            Post(() => Started?.Invoke(this, current));
            if (!hasKey)
            {
                Post(() => Failed?.Invoke(this, "未填写 API Key。录音会继续保存，你可以手打说明，或稍后配置后重新转写。"));
            }
            else if (kind == SpeechModelKind.UrlOnly)
            {
                var exception = UrlOnlyFailure();
                providerFailure = exception;
                ReportFailure(exception);
            }
            else if (streamingAudio)
            {
                provider = CreateProvider(configuration);
                provider.TranscriptReceived += OnTranscriptReceived;
                try
                {
                    await provider.StartAsync(BuildOptions(configuration, apiKey, configuration.SpeechModel), cancellationToken);
                    sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendTask = SendAudioAsync(provider, audioChannel.Reader, sendCancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    streamingAudio = false;
                    audioChannel.Writer.TryComplete();
                    while (audioChannel.Reader.TryRead(out _)) { }
                    providerFailure = exception;
                    provider.TranscriptReceived -= OnTranscriptReceived;
                    await provider.DisposeAsync();
                    provider = null;
                    ReportFailure(exception);
                }
            }
        }
        catch
        {
            recorder.Dispose();
            recorder = null;
            if (provider is not null) await provider.DisposeAsync();
            provider = null;
            await store.SaveTextAsync(current, latestText, TranscriptionStatus.Incomplete, cancellationToken);
            current = current with { Status = TranscriptionStatus.Incomplete, AudioPath = audioPath };
            throw;
        }
        finally
        {
            starting.TrySetResult();
        }
    }

    public void TogglePause()
    {
        if (recorder is null) return;
        recorder.Paused = !recorder.Paused;
    }

    private void OnRecorderPauseChanged(object? sender, RecordingPauseState state)
    {
        var source = sender as WavRecorder;
        string? recordId = current?.Id;
        Post(() =>
        {
            // 结束、换录音或已经手动继续后，旧回调不能再把界面变成暂停。
            if (source is null || !ReferenceEquals(source, recorder) || current?.Id != recordId || source.PauseState != state) return;
            PauseChanged?.Invoke(this, state);
            if (state.Paused) LevelChanged?.Invoke(this, 0);
        });
    }

    /// <summary>用户在录音中改了文字。之后的保存都以这份为准。</summary>
    public void NoteUserText(string text)
    {
        if (current is null) return;
        latestText = text;
        transcriptChannel?.Writer.TryWrite(text);
    }

    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        // 用户结束录音时立即关闭麦克风，即使本地模型还在加载。
        recorder?.Stop();
        if (current is { } stopping)
        {
            void Notify() => Stopping?.Invoke(this, stopping.Id);
            if (uiContext is null || ReferenceEquals(SynchronizationContext.Current, uiContext)) Notify();
            else Post(Notify);
        }
        // 刚打开就结束时连接还在建立，要等它有结果，否则两边同时操作同一条连接
        if (startup is { } starting) await starting.Task.WaitAsync(cancellationToken);
        await finishLock.WaitAsync(cancellationToken);
        try
        {
            if (current is null) return;
            AppSettings configuration = recordingSettings ?? settings;
            QuanShuoRecord finishing = current;
            recorder?.Stop();
            recorder?.Dispose();
            recorder = null;
            if (audioChannel is not null) audioChannel.Writer.TryComplete();
            if (sendTask is not null)
            {
                try { await sendTask.WaitAsync(AudioDrainTimeout, cancellationToken); }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
                {
                    streamingAudio = false;
                    // 取消并等待 SendAudio 真正退出，再允许下方 Dispose 连接。
                    sendCancellation?.Cancel();
                    try { await sendTask; }
                    catch (OperationCanceledException) { }
                    catch (Exception sendError) { providerFailure ??= sendError; }
                    providerFailure ??= exception is TimeoutException
                        ? new TranscriptionServiceException("AudioBacklog", "识别服务未能及时接收剩余音频，已停止本次转写。请换较轻的模型后，用保留的录音重新转写。")
                        : exception;
                    ReportFailure(providerFailure);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    providerFailure ??= exception;
                    ReportFailure(exception);
                }
                finally
                {
                    sendCancellation?.Dispose();
                    sendCancellation = null;
                }
            }

            bool success = provider is not null && providerFailure is null;
            if (success && provider is not null)
            {
                try { await provider.FinishAsync(cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    success = false;
                    providerFailure = exception;
                    ReportFailure(exception);
                }
            }

            if (provider is not null) await provider.DisposeAsync();
            provider = null;

            string? audioPath = store.GetAudioPath(finishing);
            if (transcribeAfterRecording)
            {
                try
                {
                    string transcript = await TranscribeFileAsync(configuration, audioPath, secrets.LoadApiKey(), null, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(transcript)) ApplyRecognized(transcript, true);
                    success = providerFailure is null;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    providerFailure = exception;
                    ReportFailure(exception);
                }
            }

            // WebSocket 完成不等于 UI 完成。这个投递同时等待先前已排队的识别回调，
            // 并在关闭保存队列前将输入法/拖选暂存的最终文字交还协调器。
            await CaptureFinalTextAsync();
            if (transcriptChannel is not null) transcriptChannel.Writer.TryComplete();
            if (persistenceTask is not null)
            {
                try { await persistenceTask; }
                catch (Exception exception) when (exception is not OperationCanceledException) { ReportFailure(exception); }
            }

            TranscriptionStatus status = success ? TranscriptionStatus.Completed : TranscriptionStatus.Incomplete;
            var completed = finishing with { Text = latestText, Status = status, AudioPath = audioPath };
            await SaveTextOrThrowAsync(completed, latestText, status, cancellationToken);
            if (success && configuration.DeleteAudioAfterSuccess)
            {
                if (File.Exists(audioPath)) File.Delete(audioPath);
                completed = completed with { AudioPath = null };
            }

            current = null;
            recordingSettings = null;
            latestText = string.Empty;
            TextMerger = null;
            FinalTextSnapshot = null;
            sendTask = null;
            persistenceTask = null;
            audioChannel = null;
            transcriptChannel = null;
            Post(() => Finished?.Invoke(this, completed));
        }
        finally
        {
            finishLock.Release();
        }
    }

    public async Task<QuanShuoRecord> RetryAsync(QuanShuoRecord record, CancellationToken cancellationToken = default)
    {
        using var modelUse = modelActivity.EnterUse();
        string? audioPath = record.AudioPath;
        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) throw new FileNotFoundException("找不到这条记录的 WAV，无法重新转写。", audioPath);
        AppSettings configuration = settings;
        SpeechProviders.Validate(configuration);
        string apiKey = SpeechProviders.IsLocal(configuration) ? string.Empty : secrets.LoadApiKey();
        if (!SpeechProviders.IsLocal(configuration) && string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("请先在设置中填写阿里云 API Key。原录音仍保留。");
        await SaveTextOrThrowAsync(record, record.Text, TranscriptionStatus.Recording, cancellationToken);
        string text = record.Text;
        try
        {
            // 失败时保留用户原文，只有完整识别成功才替换，避免中间结果覆盖编辑。
            string transcript = await TranscribeFileAsync(configuration, audioPath, apiKey, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcript)) text = transcript;
            var completed = record with { Text = text, Status = TranscriptionStatus.Completed };
            await SaveTextOrThrowAsync(completed, text, TranscriptionStatus.Completed, cancellationToken);
            if (configuration.DeleteAudioAfterSuccess)
            {
                File.Delete(audioPath);
                completed = completed with { AudioPath = null };
            }

            return completed;
        }
        catch
        {
            // 失败不应用服务端文字。即使取消，也应尽力结束临时的 Recording 状态。
            try { await store.SaveTextAsync(record, record.Text, TranscriptionStatus.Incomplete, CancellationToken.None); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { ReportFailure(exception); }
            throw;
        }
    }

    private async Task SaveTextOrThrowAsync(QuanShuoRecord record, string text, TranscriptionStatus status, CancellationToken cancellationToken)
    {
        if (!await store.SaveTextAsync(record, text, status, cancellationToken))
            throw new IOException("记录文件已被移动或删除，文字尚未保存。录音 WAV 仍保留，请恢复记录文件后重试。");
    }

    private Task CaptureFinalTextAsync()
    {
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                if (current is not null && FinalTextSnapshot is { } snapshot) NoteUserText(snapshot());
                captured.TrySetResult();
            }
            catch (Exception exception) { captured.TrySetException(exception); }
        });
        return captured.Task;
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default, AppSettings? requestedSettings = null) =>
        (await TestConnectionDetailsAsync(cancellationToken, requestedSettings)).Message;

    public async Task<ConnectionTestResult> TestConnectionDetailsAsync(CancellationToken cancellationToken = default, AppSettings? requestedSettings = null)
    {
        using var modelUse = modelActivity.EnterUse();
        if (IsRecording) throw new InvalidOperationException("请先结束当前录音，再测试识别连接。");
        if (Interlocked.CompareExchange(ref connectionTesting, 1, 0) != 0)
            throw new InvalidOperationException("已有连接测试正在进行，请等待它结束。");
        try { return await TestConnectionCoreAsync(requestedSettings ?? settings, cancellationToken); }
        finally { Interlocked.Exchange(ref connectionTesting, 0); }
    }

    public async Task<LocalModelSwitchResult> SwitchLocalModelAsync(LocalAsrProfile profile, AppSettings expectedCurrent,
        Func<AppSettings, Task> persist, CancellationToken cancellationToken = default)
    {
        using var switching = modelActivity.EnterSwitch();
        if (IsRecording) throw new InvalidOperationException("请先结束当前录音，再切换本地模型。");
        AppSettings original = settings;
        if (original.SettingsRevision != expectedCurrent.SettingsRevision || original.SpeechProvider != expectedCurrent.SpeechProvider || original.LocalSpeechEndpoint != expectedCurrent.LocalSpeechEndpoint ||
            original.LocalSpeechModel != expectedCurrent.LocalSpeechModel || original.SpeechLanguage != expectedCurrent.SpeechLanguage)
            throw new InvalidOperationException("识别配置已变化，请重新打开设置后切换。");
        profile.Validate(profile.Id);
        AppSettings target = profile.Apply(original) with { SettingsRevision = original.SettingsRevision + 1 };
        try
        {
            var test = await TestConnectionCoreAsync(target, cancellationToken);
            if (!profile.Matches(test.LocalModel))
                throw new InvalidOperationException("服务返回的模型身份与所选模型不一致。请让安装 agent 检查 catalogId 和实际模型名称。");
            await persist(target);
            settings = target;
            return new(target, new(target.LocalSpeechEndpoint, target.LocalSpeechModel, true, DateTimeOffset.UtcNow, test.LocalModel));
        }
        catch (Exception error)
        {
            // 同一网关可能已卸载旧模型；恢复请求使用原来的 ID，不触发下载或改写设置。
            string recovery = string.Empty;
            if (SpeechProviders.IsLocal(original) && original.LocalSpeechEndpoint == target.LocalSpeechEndpoint && original.LocalSpeechModel != target.LocalSpeechModel)
            {
                try
                {
                    using var recoveryLimit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await TestConnectionCoreAsync(original, recoveryLimit.Token);
                    recovery = " 已请求原配置，模型身份仍需重新测试确认。";
                }
                catch { recovery = " 原服务也未恢复，请按安装说明启动后再试。"; }
            }
            throw new InvalidOperationException("未切换，原配置已保留：" + error.Message + recovery, error);
        }
    }

    private async Task<ConnectionTestResult> TestConnectionCoreAsync(AppSettings configuration, CancellationToken cancellationToken)
    {
        SpeechProviders.Validate(configuration);
        if (SpeechProviders.IsLocal(configuration))
        {
            try
            {
                await using var local = CreateProvider(configuration);
                await local.StartAsync(BuildOptions(configuration, string.Empty, configuration.LocalSpeechModel), cancellationToken);
                // 静音检验真实的音频接收、结果及完成消息；不打开麦克风。
                await local.SendAudioAsync(new byte[PcmBytesPerSecond / 2], cancellationToken);
                await local.FinishAsync(cancellationToken);
                var info = (local as LocalTranscriptionProvider)?.ModelInfo;
                return new($"本地连接正常：{info?.Name ?? configuration.LocalSpeechModel} 已完成静音协议测试。未打开麦克风；识别准确率与速度还需用实际语音验证。", info);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(TranscriptionErrors.FriendlyMessage(exception), exception);
            }
        }
        string testPath = Path.Combine(store.DataDirectory, ".connection-test.wav");
        var recorder = new WavRecorder();
        try
        {
            recorder.Start(testPath, ResolveMicrophone(configuration.MicrophoneDeviceId));
            await Task.Delay(250, cancellationToken);
        }
        finally
        {
            recorder.Dispose();
            if (File.Exists(testPath)) File.Delete(testPath);
        }

        string apiKey = secrets.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("麦克风可以打开，但还没有填写阿里云 API Key。");
        try
        {
            switch (SpeechModels.KindOf(configuration.SpeechModel))
            {
                case SpeechModelKind.UrlOnly:
                    throw UrlOnlyFailure();
                case SpeechModelKind.WholeFile:
                    await DashScopeFileTranscriber.TranscribeAsync(SilentWav(), BuildOptions(configuration, apiKey, configuration.SpeechModel), cancellationToken);
                    break;
                default:
                    // 收到 task-started 就说明 Key、地址和模型都是通的，不需要真的发音频。
                    await using (var provider = new DashScopeTranscriptionProvider())
                    {
                        await provider.StartAsync(BuildOptions(configuration, apiKey, configuration.SpeechModel), cancellationToken);
                    }

                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(TranscriptionErrors.FriendlyMessage(exception), exception);
        }

        return new($"连接正常：麦克风可以打开，阿里云已接受 {configuration.SpeechModel}。");
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync();
        finishLock.Dispose();
    }

    private async Task SendAudioAsync(ITranscriptionProvider target, ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] pcm in reader.ReadAllAsync(cancellationToken))
            {
                if (providerFailure is not null) break;
                await target.SendAudioAsync(pcm, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            streamingAudio = false;
            while (reader.TryRead(out _)) { }
            providerFailure ??= exception;
            ReportFailure(exception);
        }
    }

    private async Task PersistTranscriptsAsync(ChannelReader<string> reader, QuanShuoRecord record, CancellationToken cancellationToken)
    {
        await foreach (string first in reader.ReadAllAsync(cancellationToken))
        {
            // 打字和识别都会写到这里，积压时只存最新的一份
            string text = first;
            while (reader.TryRead(out string? newer)) text = newer;
            await store.SaveTextAsync(record with { Text = text }, text, TranscriptionStatus.Recording, cancellationToken);
            string saved = text;
            Post(() => TranscriptChanged?.Invoke(this, record with { Text = saved, Status = TranscriptionStatus.Recording }));
        }
    }

    /// <summary>转写一个已存在的 WAV。同步模型整段发送；流式模型或超过同步时长的录音走流式，且不用按说话速度发。</summary>
    private async Task<string> TranscribeFileAsync(AppSettings configuration, string audioPath, string apiKey, Action<string>? onPartial, CancellationToken cancellationToken)
    {
        bool local = SpeechProviders.IsLocal(configuration);
        SpeechModelKind kind = local ? SpeechModelKind.Streaming : SpeechModels.KindOf(configuration.SpeechModel);
        if (kind == SpeechModelKind.UrlOnly) throw UrlOnlyFailure();
        byte[] wav = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        ReadOnlyMemory<byte> pcm = PcmWaveAudio.Read(wav);
        double seconds = pcm.Length / (double)PcmBytesPerSecond;
        if (kind == SpeechModelKind.WholeFile && seconds <= SpeechModels.WholeFileLimitSeconds)
        {
            return await DashScopeFileTranscriber.TranscribeAsync(wav, BuildOptions(configuration, apiKey, configuration.SpeechModel), cancellationToken);
        }

        string model = kind == SpeechModelKind.Streaming ? configuration.SpeechModel : SpeechModels.Default;
        string text = string.Empty;
        await using var streaming = CreateProvider(configuration, TimeSpan.FromSeconds(Math.Max(60, 20 + seconds)));
        streaming.TranscriptReceived += (_, update) =>
        {
            text = update.Text;
            onPartial?.Invoke(text);
        };
        await streaming.StartAsync(BuildOptions(configuration, apiKey, model), cancellationToken);
        for (int offset = 0; offset < pcm.Length; offset += FileChunkBytes)
        {
            await streaming.SendAudioAsync(pcm.Slice(offset, Math.Min(FileChunkBytes, pcm.Length - offset)), cancellationToken);
        }

        await streaming.FinishAsync(cancellationToken);
        return text;
    }

    private ITranscriptionProvider CreateProvider(AppSettings configuration, TimeSpan? finishTimeout = null) =>
        ProviderFactory?.Invoke() ?? (SpeechProviders.IsLocal(configuration)
            ? new LocalTranscriptionProvider { FinishTimeout = finishTimeout ?? TimeSpan.FromSeconds(60) }
            : new DashScopeTranscriptionProvider { FinishTimeout = finishTimeout ?? TimeSpan.FromSeconds(20) });

    private static TranscriptionOptions BuildOptions(AppSettings configuration, string apiKey, string model) => new()
    {
        ApiKey = apiKey,
        Model = SpeechProviders.IsLocal(configuration) ? configuration.LocalSpeechModel : model,
        Endpoint = SpeechProviders.IsLocal(configuration) ? configuration.LocalSpeechEndpoint : configuration.SpeechEndpoint,
        Language = configuration.SpeechLanguage,
        VocabularyId = configuration.VocabularyId,
    };

    private static TranscriptionServiceException UrlOnlyFailure() => new(
        "UrlOnlyModel",
        "这个模型只接受公网文件地址，圈说的录音保存在本机，所以用不了。请在设置里换一个模型；截图、已有文字和 WAV 都没有丢失。");

    /// <summary>0.5 秒静音，用来验证同步接口的 Key 和模型。</summary>
    private static byte[] SilentWav()
    {
        int dataBytes = PcmBytesPerSecond / 2;
        using var stream = new MemoryStream(WavHeaderBytes + dataBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(PcmBytesPerSecond);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return stream.ToArray();
    }

    private void OnAudioAvailable(object? sender, AudioDataEventArgs e)
    {
        QueueAudio(e.Pcm);
        float level = e.Level;
        Post(() => LevelChanged?.Invoke(this, level));
    }

    // 暂停时没有音频，服务端约 20 秒后会断开连接。发静音保活，WAV 里不写。
    private void OnSilenceAvailable(object? sender, byte[] silence)
    {
        QueueAudio(silence);
    }

    private void QueueAudio(byte[] pcm)
    {
        if (!streamingAudio || audioChannel is null || audioChannel.Writer.TryWrite(pcm)) return;
        streamingAudio = false;
        providerFailure = new TranscriptionServiceException("AudioBacklog", "识别服务接收音频太慢，已停止实时发送。录音会继续保存，结束后可换一个较轻的模型重新转写。");
        ReportFailure(providerFailure);
    }

    // 回到 UI 线程再合并：文字框只有那里能碰，用户的修改也发生在那里，两者才不会交错。
    // FinishAsync 另行等待 UI 队列并取得最终快照，不能将网络完成当作文字已保存。
    private void OnTranscriptReceived(object? sender, TranscriptUpdate update) => Post(() => ApplyRecognized(update.Text, update.IsFinal));

    private void ApplyRecognized(string recognized, bool isFinal)
    {
        if (current is null) return;
        latestText = TextMerger?.Invoke(recognized, isFinal) ?? recognized;
        transcriptChannel?.Writer.TryWrite(latestText);
    }

    private void OnRecorderFailed(object? sender, string message)
    {
        streamingAudio = false;
        providerFailure ??= new IOException(message);
        Post(() => Failed?.Invoke(this, message));
    }

    private void ReportFailure(Exception exception)
    {
        string message = TranscriptionErrors.FriendlyMessage(exception);
        Post(() => Failed?.Invoke(this, message));
    }

    private static int ResolveMicrophone(string? deviceId) => int.TryParse(deviceId, out int index) ? index : 0;

    private void Post(Action action)
    {
        if (uiContext is null) action();
        else uiContext.Post(_ => action(), null);
    }
}
