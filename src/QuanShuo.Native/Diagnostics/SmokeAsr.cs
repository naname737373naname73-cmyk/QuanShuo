using System.Text.Json;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.Diagnostics;

/// <summary>
/// 用已保存的 API Key 转写一个 16kHz 单声道 WAV，把结果和原始错误写进文件。
/// 只发送指定的音频；结果里不含 Key。
/// pacing 是每 100ms 音频之间的等待毫秒数；gapSeconds 在发完一半音频后停止发送一段时间，模拟录音暂停。
/// noDirect 让直连立刻超时，走“连不上就改用系统代理”这条路。
/// </summary>
public static class SmokeAsr
{
    public static async Task<int> RunAsync(string input, string model, string outputPath, string? endpoint, int pacing, int gapSeconds, bool fakeKey = false, bool noDirect = false)
    {
        string apiKey = fakeKey ? "sk-quanshuo-smoke-invalid" : new DpapiSecretStore().LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await File.WriteAllTextAsync(outputPath, "{\"ok\":false,\"error\":\"no saved api key\"}");
            return 2;
        }

        var options = new TranscriptionOptions
        {
            ApiKey = apiKey,
            Model = model,
            Endpoint = endpoint ?? "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
            Language = "zh",
        };
        string text = string.Empty;
        string? error = null;
        string? friendly = null;
        int updates = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            byte[] wav = await File.ReadAllBytesAsync(input);
            if (SpeechModels.KindOf(model) == SpeechModelKind.WholeFile)
            {
                text = await DashScopeFileTranscriber.TranscribeAsync(wav, options, CancellationToken.None);
            }
            else
            {
                await using var provider = noDirect
                    ? new DashScopeTranscriptionProvider { DirectConnectTimeout = TimeSpan.FromMilliseconds(1) }
                    : new DashScopeTranscriptionProvider();
                provider.TranscriptReceived += (_, update) =>
                {
                    text = update.Text;
                    updates++;
                };
                await provider.StartAsync(options, CancellationToken.None);
                int dataStart = FindData(wav);
                const int chunk = 3200;
                int middle = dataStart + (wav.Length - dataStart) / 2;
                bool gapDone = gapSeconds == 0;
                for (int offset = dataStart; offset < wav.Length; offset += chunk)
                {
                    await provider.SendAudioAsync(wav.AsMemory(offset, Math.Min(chunk, wav.Length - offset)), CancellationToken.None);
                    if (pacing > 0) await Task.Delay(pacing);
                    if (!gapDone && offset >= middle)
                    {
                        gapDone = true;
                        await Task.Delay(TimeSpan.FromSeconds(gapSeconds));
                    }
                }

                await provider.FinishAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            var chain = new List<string>();
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                chain.Add(current.GetType().Name + ": " + current.Message);
            }

            error = string.Join(" <- ", chain).Replace(apiKey, "***", StringComparison.Ordinal);
            friendly = TranscriptionErrors.FriendlyMessage(exception);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            ok = error is null && text.Length > 0,
            model,
            pacing,
            gapSeconds,
            updates,
            text,
            error,
            friendly,
            elapsedMs = watch.ElapsedMilliseconds,
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return error is null ? 0 : 1;
    }

    /// <summary>
    /// 走真实的 RecordingCoordinator：测试连接、重新转写、暂停保活、录完再转写。
    /// 录音一开始就暂停，几乎不采集麦克风内容；重新转写用的是传入的 WAV。
    /// </summary>
    public static async Task<int> RunCoordinatorAsync(string input, string longInput, string outputPath, int pauseSeconds)
    {
        var results = new List<object>();
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "coordinator-" + DateTime.Now.Ticks);
        Directory.CreateDirectory(root);
        var secrets = new DpapiSecretStore();
        string[] models = ["qwen-audio-3.1-asr-flash-message", "qwen-audio-3.1-asr-flash", "qwen-audio-3.0-asr-flash", "qwen-audio-3.1-asr-flash-filetrans"];
        foreach (string model in models)
        {
            results.Add(new { step = "connection", model, result = await Attempt(() => Coordinator(root, secrets, model, out _).TestConnectionAsync()) });
            results.Add(new { step = "retry", model, result = await Attempt(() => Retry(root, secrets, model, input)) });
        }

        results.Add(new { step = "retry-over-5-minutes", model = models[2], result = await Attempt(() => Retry(root, secrets, models[2], longInput)) });
        results.Add(new { step = "pause-" + pauseSeconds + "s", model = models[0], result = await Attempt(() => Record(root, secrets, models[0], pauseSeconds)) });
        results.Add(new { step = "record-then-transcribe", model = models[2], result = await Attempt(() => Record(root, secrets, models[2], 1)) });
        var settings = await new AppSettingsStore().LoadAsync();
        results.Add(new { step = "saved-model-after-load", model = settings.SpeechModel, result = "" });
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    private static RecordingCoordinator Coordinator(string root, DpapiSecretStore secrets, string model, out RecordStore store)
    {
        store = new RecordStore(root);
        return new RecordingCoordinator(store, secrets, new AppSettings { DataDirectory = root, DeleteAudioAfterSuccess = false, SpeechModel = model });
    }

    private static Task<QuanShuoRecord> NewRecord(RecordStore store) => store.CreateAsync(
        new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "SmokeAsr", WindowTitle = "识别验证" },
        (path, _) =>
        {
            using var bitmap = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return Task.CompletedTask;
        },
        string.Empty,
        TranscriptionStatus.Recording);

    private static async Task<string> Retry(string root, DpapiSecretStore secrets, string model, string wav)
    {
        await Task.Delay(1100); // 记录文件名精确到秒
        RecordingCoordinator coordinator = Coordinator(root, secrets, model, out RecordStore store);
        QuanShuoRecord record = await NewRecord(store);
        string audio = store.GetAudioPath(record);
        File.Copy(wav, audio, true);
        QuanShuoRecord done = await coordinator.RetryAsync(record with { AudioPath = audio });
        return $"{done.Status} len={done.Text.Length} {done.Text[..Math.Min(32, done.Text.Length)]}";
    }

    private static async Task<string> Record(string root, DpapiSecretStore secrets, string model, int pauseSeconds)
    {
        await Task.Delay(1100);
        RecordingCoordinator coordinator = Coordinator(root, secrets, model, out RecordStore store);
        QuanShuoRecord record = await NewRecord(store);
        var failures = new List<string>();
        QuanShuoRecord? finished = null;
        coordinator.Failed += (_, message) => failures.Add(message);
        coordinator.Finished += (_, completed) => finished = completed;
        coordinator.TogglePause();
        await coordinator.StartAsync(record);
        coordinator.TogglePause();
        await Task.Delay(TimeSpan.FromSeconds(pauseSeconds));
        await coordinator.FinishAsync();
        return $"{finished?.Status} failures=[{string.Join(" | ", failures)}]";
    }

    private static async Task<string> Attempt(Func<Task<string>> action)
    {
        try
        {
            return "OK " + await action();
        }
        catch (Exception exception)
        {
            return "THROWN " + TranscriptionErrors.FriendlyMessage(exception) + " || " + exception.GetType().Name + ": " + exception.Message + " @ " + exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        }
    }

    private static int FindData(byte[] wav)
    {
        for (int index = 12; index + 8 < wav.Length;)
        {
            int size = BitConverter.ToInt32(wav, index + 4);
            if (wav[index] == 'd' && wav[index + 1] == 'a' && wav[index + 2] == 't' && wav[index + 3] == 'a') return index + 8;
            index += 8 + size + (size & 1);
        }

        return Math.Min(44, wav.Length);
    }
}
