using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using QuanShuo.Core.Models;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.Diagnostics;

public static class SmokeLocalAsr
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunCommandLineAsync(string[] args, string fallbackOutputPath)
    {
        // Find an explicitly supplied output even when another option is malformed.
        string outputPath = FindOutputPath(args) ?? fallbackOutputPath;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--smoke-input", "--smoke-endpoint", "--smoke-model", "--smoke-output", "--smoke-pacing",
            };
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (option.Equals("--smoke-local-asr", StringComparison.OrdinalIgnoreCase)) continue;
                if (!allowed.Contains(option)) throw new ArgumentException("未知的本地诊断参数：" + option);
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(option + " 缺少参数值。");
                if (!values.TryAdd(option, args[++index])) throw new ArgumentException(option + " 不能重复指定。");
            }

            if (!values.TryGetValue("--smoke-input", out string? input) || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("请用 --smoke-input 指定 16kHz、单声道、PCM16 WAV 文件。");
            string pacingValue = values.GetValueOrDefault("--smoke-pacing", "100");
            if (!int.TryParse(pacingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pacing) || pacing < 0)
                throw new ArgumentException("--smoke-pacing 必须是非负整数毫秒；100 为实时节奏，0 为快速重转写。");
            string endpoint = values.GetValueOrDefault("--smoke-endpoint", "ws://127.0.0.1:8765/asr");
            string model = values.GetValueOrDefault("--smoke-model", "default");
            return await RunAsync(input, endpoint, model, outputPath, pacing);
        }
        catch (Exception exception)
        {
            string error = "本地识别诊断未完成：" + exception.Message;
            try
            {
                await WriteResultAsync(outputPath, new
                {
                    ok = false, protocolOk = false, streamingObserved = (bool?)null,
                    protocol = LocalTranscriptionProvider.Protocol, text = string.Empty,
                    error, note = "参数或输出文件无效；本次不能算验收通过。",
                });
            }
            catch (Exception outputException)
            {
                Console.Error.WriteLine(error + " 诊断 JSON 无法写入：" + outputException.Message);
            }
            return 1;
        }
    }

    public static async Task<int> RunAsync(string input, string endpoint, string model, string outputPath, int pacing)
    {
        string text = string.Empty;
        string? error = null;
        int updates = 0, partialUpdates = 0, transcriptsBeforeFinish = 0, partialsBeforeFinish = 0;
        bool protocolOk = false, audioStarted = false, finishRequested = false;
        long? firstTextMs = null, readyMs = null, finishRequestedMs = null, finishWaitMs = null;
        double? audioDurationMs = null;
        LocalModelInfo? modelInfo = null;
        object metricsGate = new();
        var watch = Stopwatch.StartNew();
        try
        {
            if (pacing < 0) throw new ArgumentException("发送间隔不能为负数。");
            ReadOnlyMemory<byte> pcm = PcmWaveAudio.Read(await File.ReadAllBytesAsync(input));
            audioDurationMs = pcm.Length / 32.0;
            await using var provider = new LocalTranscriptionProvider
            {
                FinishTimeout = TimeSpan.FromSeconds(Math.Max(60, 20 + pcm.Length / 32000.0)),
            };
            provider.TranscriptReceived += (_, result) =>
            {
                lock (metricsGate)
                {
                    text = result.Text;
                    updates++;
                    if (!result.IsFinal) partialUpdates++;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        firstTextMs ??= watch.ElapsedMilliseconds;
                        if (audioStarted && !finishRequested)
                        {
                            transcriptsBeforeFinish++;
                            if (!result.IsFinal) partialsBeforeFinish++;
                        }
                    }
                }
            };
            await provider.StartAsync(new TranscriptionOptions { ApiKey = string.Empty, Endpoint = endpoint, Model = model }, CancellationToken.None);
            readyMs = watch.ElapsedMilliseconds;
            modelInfo = provider.ModelInfo;
            for (int offset = 0; offset < pcm.Length; offset += 3200)
            {
                lock (metricsGate) audioStarted = true;
                await provider.SendAudioAsync(pcm.Slice(offset, Math.Min(3200, pcm.Length - offset)), CancellationToken.None);
                if (pacing > 0) await Task.Delay(pacing);
            }
            lock (metricsGate)
            {
                finishRequested = true;
                finishRequestedMs = watch.ElapsedMilliseconds;
            }
            try
            {
                await provider.FinishAsync(CancellationToken.None);
                protocolOk = true;
            }
            finally { finishWaitMs = watch.ElapsedMilliseconds - finishRequestedMs.Value; }
        }
        catch (Exception exception)
        {
            error = exception is FileNotFoundException or DirectoryNotFoundException or InvalidDataException
                ? "测试 WAV 无法读取：" + exception.Message
                : TranscriptionErrors.FriendlyMessage(exception);
        }
        bool ok = protocolOk && error is null && !string.IsNullOrWhiteSpace(text);
        string pacingMode = pacing == 100 ? "realtime" : pacing == 0 ? "fast" : "custom";
        // Fast replay tests protocol/backpressure. It cannot establish live latency.
        bool? streamingObserved = pacing == 100 ? transcriptsBeforeFinish > 0 : null;
        await WriteResultAsync(outputPath, new
        {
            ok, protocolOk, streamingObserved, protocol = LocalTranscriptionProvider.Protocol,
            model, modelInfo, endpoint, text, updates, partialUpdates, transcriptsBeforeFinish, partialsBeforeFinish,
            pacing, pacingMode, readyMs, audioDurationMs, firstTextMs, finishRequestedMs, finishWaitMs,
            elapsedMs = watch.ElapsedMilliseconds, error,
            note = "ok 仅代表协议完成且文字非空，不代表实时出字或准确。streamingObserved 仅在 pacing=100 时判断是否在送音频期间收到非空结果；快速/自定义节奏为 null。首字时间包含连接和模型准备。静音与短音频不能单独证明流式能力。",
        });
        return ok ? 0 : 1;
    }

    private static string? FindOutputPath(string[] args)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--smoke-output", StringComparison.OrdinalIgnoreCase) &&
                !args[index + 1].StartsWith("--", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[index + 1]))
                return args[index + 1];
        }
        return null;
    }

    private static async Task WriteResultAsync(string outputPath, object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, JsonOptions));
    }
}
