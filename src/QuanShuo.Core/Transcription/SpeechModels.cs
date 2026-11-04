namespace QuanShuo.Core.Transcription;

public enum SpeechModelKind
{
    /// <summary>WebSocket 流式，边说边出字。</summary>
    Streaming,

    /// <summary>同步 HTTP，录完后把整段音频发过去。</summary>
    WholeFile,

    /// <summary>只接受公网文件地址，本地录音用不了。</summary>
    UrlOnly,
}

/// <summary>阿里云语音模型名和调用方式的对应关系。不同模型的协议不同，换模型不只是换名字。</summary>
public static class SpeechModels
{
    public const string Default = "qwen-audio-3.1-asr-flash-message";

    /// <summary>同步 HTTP 模型单次音频上限。</summary>
    public const int WholeFileLimitSeconds = 300;

    private static readonly string[] WholeFile = ["qwen-audio-3.0-asr-flash", "qwen-audio-3.1-asr-flash"];

    // 这个模型不走 run-task 协议，放在下拉框里时选了必报 ModelNotFound。
    private static readonly string[] Retired = ["qwen3-asr-flash-realtime"];

    public static SpeechModelKind KindOf(string model)
    {
        if (model.EndsWith("-filetrans", StringComparison.OrdinalIgnoreCase)) return SpeechModelKind.UrlOnly;
        return WholeFile.Contains(model, StringComparer.OrdinalIgnoreCase) ? SpeechModelKind.WholeFile : SpeechModelKind.Streaming;
    }

    public static bool IsQwenAudio(string model) => model.StartsWith("qwen-audio-", StringComparison.OrdinalIgnoreCase);

    /// <summary>把已保存的失效模型换成默认模型，其余原样返回。</summary>
    public static string Normalize(string? model) =>
        string.IsNullOrWhiteSpace(model) || Retired.Contains(model, StringComparer.OrdinalIgnoreCase) ? Default : model.Trim();
}
