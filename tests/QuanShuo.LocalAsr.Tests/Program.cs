using System.Net.WebSockets;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Diagnostics;
using QuanShuo.Native.Transcription;

// 实际 WebSocket 和录音协调器；不打开麦克风、不改用户配置、不加载模型。
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
app.UseWebSockets();
int leakedHeaders = 0;
var switchStarts = new System.Collections.Concurrent.ConcurrentQueue<string>();
app.Map("/asr", async (HttpContext context) =>
{
    if (context.Request.Headers.ContainsKey("Authorization")) Interlocked.Increment(ref leakedHeaders);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    string mode = context.Request.Query["case"].ToString();
    try
    {
        var start = await Receive(socket);
        using var json = JsonDocument.Parse(start.Bytes);
        Check(json.RootElement.GetProperty("protocol").GetString() == LocalTranscriptionProvider.Protocol, "start protocol");
        string requestedModel = json.RootElement.GetProperty("model").GetString()!;
        if (mode == "switch-router")
        {
            Check(requestedModel is "old-model" or "new-model" or "broken-model", "unexpected switch model");
            switchStarts.Enqueue(requestedModel);
            if (requestedModel == "broken-model") { await Send(socket, new { type = "error", message = "synthetic load failure" }); return; }
        }
        else Check(requestedModel == "test-config", "selected model not forwarded");
        Check(json.RootElement.GetProperty("audio").GetProperty("sampleRate").GetInt32() == 16000, "audio format");
        if (mode == "stall-ready") { await Task.Delay(300); return; }
        // 坏协议仍继续其余正常会话：测试必须因目标约束失败，不能靠服务随即断连假绿。
        if (mode == "switch-router")
            await Send(socket, new { type = "ready", protocol = LocalTranscriptionProvider.Protocol, modelInfo = new { name = requestedModel == "old-model" ? "Old actual model" : "New actual model", catalogId = requestedModel == "old-model" ? "paraformer-streaming-int8" : "qwen3-asr-0.6b" } });
        else if (mode == "model-info")
            await Send(socket, new { type = "ready", protocol = LocalTranscriptionProvider.Protocol, modelInfo = new { name = "Actual Paraformer INT8", catalogId = "paraformer-streaming-int8", runtime = "sherpa-onnx", device = "cpu", precision = "int8", estimatedVramMb = 0, estimatedRamMb = 1024 } });
        else if (mode == "bad-model-info")
            await Send(socket, new { type = "ready", protocol = LocalTranscriptionProvider.Protocol, modelInfo = new { name = "Actual custom model", runtime = 123, device = "cuda:0", estimatedVramMb = -10, estimatedRamMb = "1024" } });
        else await Send(socket, new { type = "ready", protocol = mode == "wrong-version" ? "wrong" : LocalTranscriptionProvider.Protocol });
        if (mode == "early-finished") await Send(socket, new { type = "finished" });
        if (mode == "unknown") await Send(socket, new { type = "something-else" });
        if (mode == "oom") await Send(socket, new { type = "error", message = "显存不足" });
        if (mode == "disconnect") { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None); return; }
        int bytes = 0;
        while (true)
        {
            var packet = await Receive(socket);
            if (packet.Type == WebSocketMessageType.Close) return;
            if (packet.Type == WebSocketMessageType.Binary)
            {
                bytes += packet.Bytes.Length;
                if (mode != "silence" && mode != "finish-only")
                {
                    // 分片 JSON 要能正确拼起来，当前句允许修订。
                    await Send(socket, new { type = "transcript", text = "今天的天", isFinal = false }, fragmented: true);
                    if (mode == "rewrite-final")
                    {
                        await Send(socket, new { type = "transcript", text = "今天。", isFinal = true });
                        await Send(socket, new { type = "transcript", text = "明天。", isFinal = true });
                    }
                }
            }
            else
            {
                using var finish = JsonDocument.Parse(packet.Bytes);
                Check(finish.RootElement.GetProperty("type").GetString() == "finish", "finish request");
                if (mode == "stall-finish") { await Task.Delay(300); return; }
                if (mode == "drop-after-partial") { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None); return; }
                if (mode != "silence" && mode != "unfinished")
                {
                    if (mode == "finish-only") await Send(socket, new { type = "transcript", text = "今天的天", isFinal = false });
                    await Send(socket, new { type = "transcript", text = $"今天的天气很好。收到{bytes}字节。", isFinal = true });
                    await Send(socket, new { type = "transcript", text = $"今天的天气很好。收到{bytes}字节。明天继续。", isFinal = true });
                }
                await Send(socket, new { type = "finished" });
                return;
            }
        }
    }
    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
});
await app.StartAsync();
string endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http:", "ws:") + "/asr";
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/local-asr-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int failures = 0;
var tests = new List<(string Name, Func<Task> Run)>
{
    ("model-info-comes-from-service-not-requested-alias", async () =>
    {
        await using var coordinator = Coordinator("model-info", out _);
        var result = await coordinator.TestConnectionDetailsAsync();
        Check(result.LocalModel is { Name: "Actual Paraformer INT8", Device: "cpu", EstimatedVramMb: 0, EstimatedRamMb: 1024 }, "lost actual model or CPU zero memory");
        Check(result.Message.Contains("Actual Paraformer INT8"), "connection message only reports requested alias");
    }),
    ("legacy-ready-does-not-invent-model-details", async () =>
    {
        await using var coordinator = Coordinator("silence", out _);
        var result = await coordinator.TestConnectionDetailsAsync();
        Check(result.LocalModel is null && result.Message.Contains("静音协议测试"), "legacy service broken or fake details");
    }),
    ("optional-malformed-metadata-does-not-break-transcription", async () =>
    {
        await using var provider = Provider();
        await provider.StartAsync(Options("bad-model-info"), CancellationToken.None);
        Check(provider.ModelInfo is { Name: "Actual custom model", Runtime: null, EstimatedVramMb: null, EstimatedRamMb: null }, "invalid estimates accepted");
        await provider.SendAudioAsync(new byte[3200], CancellationToken.None);
        await provider.FinishAsync(CancellationToken.None);
    }),
    ("default-and-invalid-optional-model-info-remain-unknown", () =>
    {
        foreach (string json in new[] { "{\"modelInfo\":null}", "{\"modelInfo\":42}", "{\"modelInfo\":{\"name\":\"default\"}}", "{\"modelInfo\":{\"name\":\" \"}}", "{\"modelInfo\":{\"name\":42}}" })
        {
            using var doc = JsonDocument.Parse(json);
            Check(LocalModelInfo.FromReady(doc.RootElement) is null, "invented model from invalid metadata");
        }
        using var huge = JsonDocument.Parse("{\"modelInfo\":{\"name\":\"actual\",\"estimatedVramMb\":1e300}}");
        Check(LocalModelInfo.FromReady(huge.RootElement)?.EstimatedVramMb is null, "unbounded estimate accepted");
        return Task.CompletedTask;
    }),
    ("model-cache-survives-reopen-and-does-not-write-settings", async () =>
    {
        string directory = Path.Combine(root, "model-status");
        Directory.CreateDirectory(directory);
        string settingsFile = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settingsFile, "user settings sentinel");
        var status = new LocalAsrModelStatus(endpoint, "test-config", true, DateTimeOffset.UtcNow, new("actual", "runtime", "cpu", "int8", 0, null));
        Check(await new LocalAsrModelStatusStore(directory).SaveAsync(status), "cache not saved");
        Check(await new LocalAsrModelStatusStore(directory).ReadAsync() == status, "cache did not roundtrip");
        Check(await File.ReadAllTextAsync(settingsFile) == "user settings sentinel", "cache overwrote user settings");
        await new LocalAsrModelStatusStore(directory).SaveAsync(status with { Connected = false, ModelInfo = null });
        Check((await new LocalAsrModelStatusStore(directory).ReadAsync()) is { Connected: false, ModelInfo: null }, "failed test retained current model");
        await File.WriteAllTextAsync(Path.Combine(directory, "local-asr-model.json"), "broken cache");
        Check(await new LocalAsrModelStatusStore(directory).ReadAsync() is null, "corrupt cache broke settings");
    }),
    ("stream-partial-final-fragmented-cumulative-no-key-leak", async () =>
    {
        var results = new List<TranscriptUpdate>();
        await using var provider = Provider();
        provider.TranscriptReceived += (_, update) => results.Add(update);
        await provider.StartAsync(Options(), CancellationToken.None);
        await provider.SendAudioAsync(new byte[3200], CancellationToken.None);
        await provider.FinishAsync(CancellationToken.None);
        Check(results.Count == 3 && results[0].Text == "今天的天" && !results[0].IsFinal, "partial missing");
        Check(results[^1].Text == "今天的天气很好。收到3200字节。明天继续。" && results[^1].IsFinal, "incorrect final");
        Check(leakedHeaders == 0, "API key leaked to local service");
    }),
    ("silent-session-completes-without-invented-text", async () =>
    {
        await using var provider = Provider();
        int updates = 0;
        provider.TranscriptReceived += (_, _) => updates++;
        await provider.StartAsync(Options("silence"), CancellationToken.None);
        await provider.SendAudioAsync(new byte[16000], CancellationToken.None);
        await provider.FinishAsync(CancellationToken.None);
        Check(updates == 0, "silence produced text");
    }),
    ("local-connection-test-does-not-need-cloud-key-or-microphone", async () =>
    {
        await using var coordinator = Coordinator("silence", out _);
        string result = await coordinator.TestConnectionAsync();
        Check(result.Contains("静音协议测试") && result.Contains("未打开麦克风"), result);
    }),
    ("connection-test-uses-requested-settings-snapshot", async () =>
    {
        await using var coordinator = Coordinator("silence", out _);
        var snapshot = new AppSettings { SpeechProvider = "local", LocalSpeechEndpoint = endpoint + "?case=silence", LocalSpeechModel = "test-config" };
        coordinator.UpdateSettings(snapshot with { LocalSpeechEndpoint = "ws://example.com/asr", LocalSpeechModel = "other-model" });
        string result = await coordinator.TestConnectionAsync(requestedSettings: snapshot);
        Check(result.Contains("test-config") && result.Contains("静音协议测试"), "tested current settings instead of the request snapshot");
    }),
    ("connection-test-rejects-overlapping-model-starts", async () =>
    {
        await using var coordinator = Coordinator("stall-ready", out _);
        Task<string> first = coordinator.TestConnectionAsync();
        try
        {
            await coordinator.TestConnectionAsync();
            throw new Exception("allowed a second connection test");
        }
        catch (InvalidOperationException error) { Check(error.Message.Contains("已有连接测试"), error.Message); }
        finally { try { await first; } catch (InvalidOperationException) { } }
    }),
    ("retry-uses-local-model-and-nonstandard-wav-header", async () =>
    {
        await using var coordinator = Coordinator("", out var store);
        var record = await NewRecord(store);
        string audio = store.GetAudioPath(record);
        await File.WriteAllBytesAsync(audio, Wave(extraChunk: true));
        var result = await coordinator.RetryAsync(record with { AudioPath = audio });
        Check(result.Status == TranscriptionStatus.Completed && result.Text.Contains("收到6400字节"), result.Text);
        Check(File.Exists(audio), "keep-audio setting ignored");
    }),
    ("retry-failure-preserves-edited-text-and-wav", async () =>
    {
        await using var coordinator = Coordinator("drop-after-partial", out var store);
        var record = await NewRecord(store);
        await store.SaveTextAsync(record, "用户手动修改的原文");
        string audio = store.GetAudioPath(record);
        await File.WriteAllBytesAsync(audio, Wave());
        await MustFail(() => coordinator.RetryAsync(record with { Text = "用户手动修改的原文", AudioPath = audio }), "LocalProtocol", "确认完成前关闭");
        var restored = MarkdownRecordFile.Parse(record.MarkdownPath, await File.ReadAllTextAsync(record.MarkdownPath));
        Check(restored.Text == "用户手动修改的原文" && restored.Status == TranscriptionStatus.Incomplete && File.Exists(audio), "lost edited text or audio");
    }),
    ("diagnostic-command-uses-real-client-without-changing-settings", async () =>
    {
        string wav = Path.Combine(root, "sample.wav"), output = Path.Combine(root, "result.json");
        await File.WriteAllBytesAsync(wav, Wave());
        int code = await SmokeLocalAsr.RunAsync(wav, endpoint, "test-config", output, 0);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Check(code == 0 && result.RootElement.GetProperty("ok").GetBoolean() && result.RootElement.GetProperty("partialUpdates").GetInt32() > 0, "diagnostic failed");
        Check(result.RootElement.GetProperty("streamingObserved").ValueKind == JsonValueKind.Null && result.RootElement.GetProperty("pacingMode").GetString() == "fast", "fast replay claimed live streaming");
    }),
    ("diagnostic-distinguishes-finish-only-from-live-output", async () =>
    {
        string wav = Path.Combine(root, "paced.wav");
        await File.WriteAllBytesAsync(wav, Wave());
        foreach (string mode in new[] { "finish-only", "live" })
        {
            string output = Path.Combine(root, "paced-" + mode + ".json");
            int code = await SmokeLocalAsr.RunAsync(wav, endpoint + "?case=" + mode, "test-config", output, 100);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var data = result.RootElement;
            Check(code == 0 && data.GetProperty("protocolOk").GetBoolean() && data.GetProperty("partialUpdates").GetInt32() > 0, "protocol diagnostic failed");
            Check(data.GetProperty("streamingObserved").GetBoolean() == (mode == "live"), "finish-only output was mistaken for live streaming");
            Check((data.GetProperty("partialsBeforeFinish").GetInt32() > 0) == (mode == "live"), "partial timing was not distinguished");
            Check(data.GetProperty("readyMs").GetInt64() >= 0 && data.GetProperty("audioDurationMs").GetDouble() == 200 && data.GetProperty("finishWaitMs").GetInt64() >= 0, "missing diagnostic timing");
        }
    }),
    ("diagnostic-bad-arguments-report-json-and-fail", async () =>
    {
        int index = 0;
        foreach (string[] invalid in new[] { Array.Empty<string>(), new[] { "--smoke-input", "unused.wav", "--smoke-pacing", "not-a-number" }, new[] { "--smoke-input" }, new[] { "--unexpected", "value" } })
        {
            string output = Path.Combine(root, "bad-args-" + index++ + ".json");
            int code = await SmokeLocalAsr.RunCommandLineAsync(["--smoke-local-asr", "--SMOKE-OUTPUT", output, ..invalid], "unused-output.json");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Check(code != 0 && !result.RootElement.GetProperty("ok").GetBoolean() && !string.IsNullOrWhiteSpace(result.RootElement.GetProperty("error").GetString()), "invalid arguments looked successful or omitted JSON");
        }
    }),
    ("settings-backward-compatibility-and-local-validation", () =>
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(old.SpeechProvider == SpeechProviders.DashScope, "existing users changed provider");
        SpeechProviders.Validate(old);
        SpeechProviders.Validate(new AppSettings { SpeechProvider = "local", SpeechEndpoint = "unused", LocalSpeechEndpoint = endpoint });
        foreach (string invalid in new[] { "https://127.0.0.1", "ws://example.com/asr", "ws://192.168.1.2/asr", "ws://user:secret@localhost/asr", "bad" })
        {
            try { SpeechProviders.LocalEndpoint(invalid); throw new Exception("accepted unsafe/invalid endpoint: " + invalid); }
            catch (ArgumentException) { }
        }
        string prompt = LocalAsrSetupPrompt.Read();
        Check(prompt.Contains(LocalTranscriptionProvider.Protocol) && prompt.Contains("localSpeechEndpoint") && prompt.Contains("--smoke-local-asr"), "packaged prompt missing contract");
        return Task.CompletedTask;
    }),
    ("application-command-line-end-to-end", async () =>
    {
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string wav = Path.Combine(root, "cli sample.wav"), output = Path.Combine(root, "cli result.json");
        await File.WriteAllBytesAsync(wav, Wave(extraChunk: true));
        string dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../../dotnet.exe"));
        string? published = Environment.GetEnvironmentVariable("QUANSHUO_TEST_EXE");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string application = Path.Combine(repository, "src/QuanShuo.Native/bin", configuration, "net10.0-windows/QuanShuo.dll");
        var start = new ProcessStartInfo(published ?? dotnet) { UseShellExecute = false, CreateNoWindow = true };
        if (published is null) start.ArgumentList.Add(application);
        foreach (string argument in new[] { "--smoke-local-asr", "--smoke-input", wav, "--smoke-endpoint", endpoint, "--smoke-model", "test-config", "--smoke-output", output, "--smoke-pacing", "0" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(limit.Token);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Check(process.ExitCode == 0 && result.RootElement.GetProperty("ok").GetBoolean() && result.RootElement.GetProperty("text").GetString()!.Contains("收到6400字节"), "CLI failed");
        string invalidOutput = Path.Combine(root, "cli invalid arguments.json");
        start.ArgumentList.Clear();
        if (published is null) start.ArgumentList.Add(application);
        foreach (string argument in new[] { "--smoke-local-asr", "--SMOKE-OUTPUT", invalidOutput, "--smoke-pacing", "bad" }) start.ArgumentList.Add(argument);
        using var invalidProcess = Process.Start(start)!;
        await invalidProcess.WaitForExitAsync(limit.Token);
        using var invalidResult = JsonDocument.Parse(await File.ReadAllTextAsync(invalidOutput));
        Check(invalidProcess.ExitCode != 0 && !invalidResult.RootElement.GetProperty("ok").GetBoolean(), "CLI invalid arguments did not fail with JSON");
    }),
    ("wav-rejects-wrong-rate-and-truncation", () =>
    {
        byte[] wav = Wave();
        BitConverter.GetBytes(48000).CopyTo(wav, 24);
        foreach (byte[] invalid in new[] { wav, Wave()[..50] })
        {
            try { PcmWaveAudio.Read(invalid); throw new Exception("accepted malformed WAV"); }
            catch (InvalidDataException) { }
        }
        return Task.CompletedTask;
    }),
    ("caller-cancellation-is-not-swallowed", async () =>
    {
        await using var provider = Provider();
        using var cancelled = new CancellationTokenSource(30);
        try { await provider.StartAsync(Options("stall-ready"), cancelled.Token); throw new Exception("ignored cancellation"); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }),
};
tests.AddRange(RecordingPersistenceAudit.Cases(root));
tests.AddRange(LocalModelSwitchChecks.Cases(endpoint, root, switchStarts));
foreach (var (mode, expectedCode, expectedDetail, expectTimeout) in new[]
{
    ("wrong-version", "LocalProtocol", "协议版本不匹配", false),
    ("early-finished", "LocalProtocol", "提前结束", false),
    ("unknown", "LocalProtocol", "不支持的消息类型", false),
    ("oom", "LocalService", "显存不足", false),
    ("disconnect", "LocalProtocol", "确认完成前关闭", false),
    ("rewrite-final", "LocalProtocol", "修改了已经确认的句子", false),
    ("unfinished", "LocalProtocol", "未确认最后一句", false),
    ("stall-ready", "LocalConnection", "本地服务未就绪", true),
    ("stall-finish", "LocalConnection", "本地服务未完成识别", true),
})
{
    tests.Add(("reject-" + mode, async () =>
    {
        await using var provider = Provider(mode.StartsWith("stall", StringComparison.Ordinal));
        await MustFail(async () =>
        {
            await provider.StartAsync(Options(mode), CancellationToken.None);
            if (mode == "early-finished") await Task.Delay(30);
            await provider.SendAudioAsync(new byte[3200], CancellationToken.None);
            await provider.FinishAsync(CancellationToken.None);
        }, expectedCode, expectedDetail, expectTimeout);
    }));
}
try
{
    foreach (var test in tests)
    {
        try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch (Exception error) { failures++; Console.WriteLine($"FAIL {test.Name}: {error}"); }
    }
}
finally { await app.StopAsync(); await app.DisposeAsync(); }
Console.WriteLine($"Tests: {tests.Count}; failures: {failures}; synthetic data: {root}");
return failures == 0 ? 0 : 1;

LocalTranscriptionProvider Provider(bool shortTimeout = false) => new() { ReadyTimeout = TimeSpan.FromMilliseconds(shortTimeout ? 150 : 2000), FinishTimeout = TimeSpan.FromMilliseconds(shortTimeout ? 150 : 2000) };
TranscriptionOptions Options(string mode = "") => new() { ApiKey = "must-not-leak", Endpoint = endpoint + "?case=" + mode, Model = "test-config" };
RecordingCoordinator Coordinator(string mode, out RecordStore store)
{
    store = new RecordStore(Path.Combine(root, Guid.NewGuid().ToString("N")));
    return new RecordingCoordinator(store, new DpapiSecretStore(), new AppSettings
    {
        SpeechProvider = "local", LocalSpeechEndpoint = endpoint + "?case=" + mode, LocalSpeechModel = "test-config",
        DataDirectory = store.DataDirectory, DeleteAudioAfterSuccess = false,
    });
}
static Task<QuanShuoRecord> NewRecord(RecordStore store) => store.CreateTextAsync(new CaptureMetadata
{
    Timestamp = DateTimeOffset.Now, AppName = "LocalAsrTest", WindowTitle = "Synthetic test",
}, TranscriptionStatus.Incomplete);
static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
static async Task MustFail(Func<Task> run, string expectedCode, string expectedDetail, bool expectTimeout = false)
{
    try { await run(); }
    catch (TranscriptionServiceException error)
    {
        Check(error.Code == expectedCode, $"Expected {expectedCode}, got {error.Code}: {error.Detail}");
        Check(error.Detail.Contains(expectedDetail, StringComparison.Ordinal), $"Expected failure containing '{expectedDetail}', got: {error.Detail}");
        if (expectTimeout) Check(error.InnerException is TimeoutException, $"Expected timeout, got: {error.InnerException?.GetType().Name ?? "none"}");
        return;
    }
    throw new Exception("Expected a local service failure");
}
static async Task Send(WebSocket socket, object value, bool fragmented = false)
{
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    int split = fragmented ? bytes.Length / 2 : 0;
    if (split > 0) await socket.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, CancellationToken.None);
    await socket.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, true, CancellationToken.None);
}
static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(WebSocket socket)
{
    byte[] bytes = new byte[8192];
    using var stream = new MemoryStream();
    ValueWebSocketReceiveResult part;
    do
    {
        part = await socket.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
        stream.Write(bytes, 0, part.Count);
    } while (!part.EndOfMessage);
    return (part.MessageType, stream.ToArray());
}
static byte[] Wave(bool extraChunk = false)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8); writer.Write(36 + 6400 + (extraChunk ? 12 : 0)); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
    if (extraChunk) { writer.Write("JUNK"u8); writer.Write(3); writer.Write(new byte[4]); }
    writer.Write("data"u8); writer.Write(6400); writer.Write(new byte[6400]);
    return stream.ToArray();
}
