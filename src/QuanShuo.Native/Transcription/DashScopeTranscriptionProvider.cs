using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QuanShuo.Core.Models;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Transcription;

public sealed class DashScopeTranscriptionProvider : ITranscriptionProvider
{
    // 同一句话的中间结果会被修订，必须按句子 ID 覆盖，不能拼接。
    private readonly SortedDictionary<int, string> sentences = [];
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ClientWebSocket? socket;
    private Task? receiveTask;
    private string taskId = string.Empty;
    private int nextSentenceId = 1;
    private long lastResultAt;
    private Exception? failure;
    private TaskCompletionSource started = NewSignal();
    private TaskCompletionSource finished = NewSignal();

    public event EventHandler<TranscriptUpdate>? TranscriptReceived;

    /// <summary>发完音频后，多久没有新结果就算断了。一次性发送长录音时要调大。</summary>
    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>直连的握手时限，超过就改走系统代理。</summary>
    internal TimeSpan DirectConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task StartAsync(TranscriptionOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("还没有填写阿里云 API Key。", nameof(options));
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != "wss")
        {
            throw new ArgumentException("识别地址必须是有效的 wss:// 地址。", nameof(options));
        }

        taskId = Guid.NewGuid().ToString();
        started = NewSignal();
        finished = NewSignal();
        failure = null;
        sentences.Clear();
        nextSentenceId = 1;

        await ConnectAsync(endpoint, options.ApiKey.Trim(), cancellationToken);
        receiveTask = ReceiveLoopAsync(lifetime.Token);
        string startMessage = JsonSerializer.Serialize(new
        {
            header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
            payload = new
            {
                task_group = "audio",
                task = "asr",
                function = "recognition",
                model = options.Model,
                input = new { },
                parameters = BuildParameters(options),
            },
        });
        await SendTextAsync(startMessage, cancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        if (pcm16Mono16Khz.IsEmpty) return;
        ThrowIfFailed();
        if (socket is null || !started.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("识别链路尚未准备好，音频仍会继续保存在本机。");
        }

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(pcm16Mono16Khz, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        ThrowIfFailed();
        if (socket is null || socket.State != WebSocketState.Open) throw new InvalidOperationException("识别连接已经断开。");
        string finishMessage = JsonSerializer.Serialize(new
        {
            header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
            payload = new { input = new { } },
        });
        await SendTextAsync(finishMessage, cancellationToken);
        // 线路慢时结果还在路上：服务端还在回结果就接着等，真的没动静了才放弃。
        Volatile.Write(ref lastResultAt, Environment.TickCount64);
        while (!finished.Task.IsCompleted)
        {
            TimeSpan quiet = TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref lastResultAt));
            if (quiet >= FinishTimeout) throw new TimeoutException("识别服务长时间没有返回结果。");
            await Task.WhenAny(finished.Task, Task.Delay(FinishTimeout - quiet, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        await finished.Task;
        ThrowIfFailed();
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (socket is not null)
        {
            socket.Abort();
            socket.Dispose();
        }

        if (receiveTask is not null)
        {
            try { await receiveTask; }
            catch (OperationCanceledException) { }
        }

        lifetime.Dispose();
        sendLock.Dispose();
    }

    /// <summary>
    /// 先直连，连不上才走系统代理。阿里云在国内，开着代理时语音要绕一圈：
    /// 实测 8 秒语音直连 10 秒出完，走代理要 30 秒；一分钟的录音走代理时服务端等不到音频，
    /// 以 request timeout 断开，后面的话就没了。
    /// </summary>
    private async Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DirectConnectTimeout);
            socket = NewSocket(apiKey);
            socket.Options.Proxy = null;
            await HandshakeAsync(socket, endpoint, limit.Token);
            return;
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            socket?.Dispose();
        }

        socket = NewSocket(apiKey);
        await HandshakeAsync(socket, endpoint, cancellationToken);
    }

    private static ClientWebSocket NewSocket(string apiKey)
    {
        var created = new ClientWebSocket();
        created.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        created.Options.SetRequestHeader("User-Agent", "QuanShuo/0.1");
        created.Options.CollectHttpResponseDetails = true;
        return created;
    }

    private static async Task HandshakeAsync(ClientWebSocket target, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await target.ConnectAsync(endpoint, cancellationToken);
        }
        catch (WebSocketException exception) when (target.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Key 无效时握手阶段就被拒绝，不会有 task-failed 事件
            throw new TranscriptionServiceException("InvalidApiKey", "握手被拒绝", exception);
        }
    }

    private static Dictionary<string, object?> BuildParameters(TranscriptionOptions options)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["format"] = "pcm",
            ["sample_rate"] = 16000,
            ["language_hints"] = new[] { options.Language },
            ["disfluency_removal_enabled"] = false,
            ["heartbeat"] = true,
        };
        if (SpeechModels.IsQwenAudio(options.Model))
        {
            // 默认只在一句话说完时返回结果，不开这项就看不到边说边出字。
            parameters["intermediate_result_enabled"] = true;
        }
        else
        {
            parameters["punctuation_prediction_enabled"] = true;
            parameters["inverse_text_normalization_enabled"] = true;
            parameters["semantic_punctuation_enabled"] = false;
        }

        if (!string.IsNullOrWhiteSpace(options.VocabularyId)) parameters["vocabulary_id"] = options.VocabularyId;
        return parameters;
    }

    private async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (socket is null) throw new InvalidOperationException("识别连接尚未建立。");
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open or WebSocketState.CloseSent })
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (!finished.Task.IsCompleted) Fail(new IOException("识别服务提前关闭了连接。已识别文字和录音仍保留在本机。"));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleServerMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or JsonException)
        {
            Fail(exception);
        }
    }

    private void HandleServerMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("header", out JsonElement header)) return;
        string? eventName = header.GetProperty("event").GetString();
        switch (eventName)
        {
            case "task-started":
                started.TrySetResult();
                break;
            case "result-generated":
                HandleTranscript(root);
                break;
            case "task-finished":
                finished.TrySetResult();
                break;
            case "task-failed":
                string code = header.TryGetProperty("error_code", out JsonElement errorCode) ? errorCode.GetString() ?? "未知错误" : "未知错误";
                string message = header.TryGetProperty("error_message", out JsonElement errorMessage) ? errorMessage.GetString() ?? string.Empty : string.Empty;
                Fail(new TranscriptionServiceException(code, message));
                break;
        }
    }

    private void HandleTranscript(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out JsonElement payload) ||
            !payload.TryGetProperty("output", out JsonElement output) ||
            !output.TryGetProperty("sentence", out JsonElement sentence)) return;
        if (sentence.TryGetProperty("heartbeat", out JsonElement heartbeat) && heartbeat.ValueKind == JsonValueKind.True) return;
        Volatile.Write(ref lastResultAt, Environment.TickCount64);
        string text = sentence.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        bool sentenceEnd = sentence.TryGetProperty("sentence_end", out JsonElement endElement) && endElement.ValueKind == JsonValueKind.True;
        int sentenceId = sentence.TryGetProperty("sentence_id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number
            ? idElement.GetInt32()
            : nextSentenceId;
        // 句子开始事件的文字是空的，不能拿它覆盖已有内容
        if (!sentenceEnd && text.Length == 0) return;
        if (string.IsNullOrWhiteSpace(text)) sentences.Remove(sentenceId);
        else sentences[sentenceId] = text;
        if (sentenceEnd) nextSentenceId = sentenceId + 1;
        TranscriptReceived?.Invoke(this, new TranscriptUpdate(string.Concat(sentences.Values), sentenceEnd));
    }

    private void Fail(Exception exception)
    {
        // 服务端先发 task-failed 再断开连接。只保留第一个错误，否则真实原因会被“连接断开”盖住。
        failure ??= exception;
        started.TrySetException(exception);
        finished.TrySetException(exception);
    }

    private void ThrowIfFailed()
    {
        if (failure is not null) throw new InvalidOperationException("语音识别没有完成。已有文字和 WAV 都保留，可以稍后重新转写。", failure);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
