using System.Net.WebSockets;
using System.Text.Json;
using QuanShuo.Core.Models;

namespace QuanShuo.Core.Transcription;

/// <summary>与模型无关的本机音频流协议。规范和安装提示词见 docs/local-asr-agent-prompt.md。</summary>
public sealed class LocalTranscriptionProvider : ITranscriptionProvider
{
    public const string Protocol = "quanshuo-asr-v1";
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiveTask;
    private Exception? failure;
    private string committed = string.Empty;
    private bool hasUncommittedText;
    private bool finishSent;
    private bool disposed;

    public event EventHandler<TranscriptUpdate>? TranscriptReceived;
    public LocalModelInfo? ModelInfo { get; private set; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task StartAsync(TranscriptionOptions options, CancellationToken cancellationToken)
    {
        if (receiveTask is not null) throw new InvalidOperationException("一个本地识别连接只能用于一次录音。");
        Uri endpoint = SpeechProviders.LocalEndpoint(options.Endpoint);
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            connect.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);
            receiveTask = ReceiveAsync();
            await SendJsonAsync(new
            {
                type = "start", protocol = Protocol, model = options.Model, language = options.Language,
                audio = new { format = "pcm_s16le", sampleRate = 16000, channels = 1 },
            }, cancellationToken).ConfigureAwait(false);
            await ready.Task.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ArgumentException)
        {
            var localError = Wrap(error, "本地服务未就绪。请确认服务已启动、模型已加载，并使用圈说本地识别协议。");
            throw Fail(localError);
        }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        ThrowIfFailed();
        if (!ready.Task.IsCompletedSuccessfully || finishSent) throw ProtocolError("当前连接不能接收音频。");
        if (pcm16Mono16Khz.Length % 2 != 0) throw new ArgumentException("PCM16 音频必须包含完整采样。");
        if (!pcm16Mono16Khz.IsEmpty)
            await SendAsync(pcm16Mono16Khz, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
    }

    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailed();
        if (!ready.Task.IsCompletedSuccessfully) throw ProtocolError("本地服务尚未准备好。");
        try
        {
            if (!finishSent)
            {
                finishSent = true;
                await SendJsonAsync(new { type = "finish" }, cancellationToken).ConfigureAwait(false);
            }
            await finished.Task.WaitAsync(FinishTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            var localError = Wrap(error, "本地服务未完成识别。请检查服务状态或使用较轻的模型，再重新转写保留的 WAV。");
            throw Fail(localError);
        }
    }

    private Task SendJsonAsync(object message, CancellationToken token) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, token);

    private async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        limit.CancelAfter(SendTimeout);
        try
        {
            await sendLock.WaitAsync(limit.Token).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, type, true, limit.Token).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }
        catch (Exception error) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            var localError = Wrap(error, "本地服务无法接收音频，可能已停止或处理速度不足。录音仍保存在本机。");
            throw Fail(localError);
        }
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) throw ProtocolError("本地服务在确认完成前关闭了连接。");
                    if (part.MessageType != WebSocketMessageType.Text) throw ProtocolError("本地服务应返回 JSON 文本消息。");
                    if (message.Length + part.Count > MaxMessageBytes) throw ProtocolError("本地服务返回的单条消息过大。");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                JsonElement root = json.RootElement;
                string? type = root.GetProperty("type").GetString();
                switch (type)
                {
                    case "ready":
                        if (ready.Task.IsCompleted || root.GetProperty("protocol").GetString() != Protocol)
                            throw ProtocolError("本地服务的协议版本不匹配，或重复发送 ready。");
                        ModelInfo = LocalModelInfo.FromReady(root);
                        ready.TrySetResult();
                        break;
                    case "transcript":
                        if (!ready.Task.IsCompletedSuccessfully) throw ProtocolError("本地服务尚未确认 ready 就返回了文字。");
                        string text = root.GetProperty("text").GetString() ?? throw ProtocolError("识别文字不能为空值。");
                        bool isFinal = root.GetProperty("isFinal").GetBoolean();
                        if (!text.StartsWith(committed, StringComparison.Ordinal))
                            throw ProtocolError("本地服务修改了已经确认的句子。请让安装 agent 检查全文累计协议。");
                        hasUncommittedText = !isFinal && text.Length > committed.Length;
                        if (isFinal) committed = text;
                        TranscriptReceived?.Invoke(this, new TranscriptUpdate(text, isFinal));
                        break;
                    case "finished":
                        if (!finishSent || !ready.Task.IsCompletedSuccessfully || hasUncommittedText)
                            throw ProtocolError("本地服务未确认最后一句，或提前结束了识别。");
                        finished.TrySetResult();
                        return;
                    case "error":
                        string detail = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "服务错误" : "服务错误";
                        throw new TranscriptionServiceException("LocalService", detail[..Math.Min(detail.Length, 1000)]);
                    default:
                        throw ProtocolError("本地服务返回了不支持的消息类型。请检查协议适配器。");
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(Wrap(error, "本地服务的连接或消息格式异常。请检查服务及协议适配器。")); }
    }

    private static TranscriptionServiceException ProtocolError(string detail) => new("LocalProtocol", detail);
    private static Exception Wrap(Exception error, string detail) => error is TranscriptionServiceException
        ? error : new TranscriptionServiceException("LocalConnection", detail, error);

    private Exception Fail(Exception error)
    {
        // Abort 同时可能使正在发送的任务报错，始终保留最先发生的具体原因。
        Exception first = Interlocked.CompareExchange(ref failure, error, null) ?? error;
        socket.Abort();
        // 唤醒等待者，由 ThrowIfFailed 传播错误，避免无人等待的 faulted Task。
        ready.TrySetResult();
        finished.TrySetResult();
        return first;
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref failure) is { } error) throw error;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        socket.Abort();
        if (receiveTask is not null) await receiveTask.ConfigureAwait(false);
        socket.Dispose();
        lifetime.Dispose();
        sendLock.Dispose();
    }
}
