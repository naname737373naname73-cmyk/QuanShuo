using QuanShuo.Core.Configuration;

namespace QuanShuo.Core.Transcription;

public static class LocalAsrCatalog
{
    public static IReadOnlyDictionary<string, string> Models { get; } = new Dictionary<string, string>
    {
        ["paraformer-streaming-int8"] = "Paraformer Streaming · INT8",
        ["qwen3-asr-0.6b"] = "Qwen3-ASR · 0.6B",
        ["qwen3-asr-1.7b"] = "Qwen3-ASR · 1.7B",
    };

    public static string Name(string id) => Models.TryGetValue(id, out var name) ? name
        : throw new ArgumentException("没有这个本地模型选项。");
}

/// <summary>Agent 验收后登记的连接资料，不包含可执行命令，也不授权下载。</summary>
public sealed record LocalAsrProfile
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ExpectedModelName { get; init; } = string.Empty;

    public void Validate(string expectedId)
    {
        LocalAsrCatalog.Name(expectedId);
        if (SchemaVersion != 1 || Id != expectedId) throw new ArgumentException("模型登记版本或 ID 不匹配。");
        SpeechProviders.LocalEndpoint(Endpoint);
        if (!ValidText(Model) || !ValidText(ExpectedModelName) || ExpectedModelName.Equals("default", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("登记资料缺少有效的模型配置 ID 或实际模型名称。");
        if (Endpoint != Endpoint.Trim() || Model != Model.Trim() || ExpectedModelName != ExpectedModelName.Trim())
            throw new ArgumentException("登记资料中的地址和模型名称不能带首尾空白。");
    }

    public AppSettings Apply(AppSettings current) => current with
    {
        SpeechProvider = SpeechProviders.Local, LocalSpeechEndpoint = Endpoint, LocalSpeechModel = Model,
    };

    public bool Matches(LocalModelInfo? info) => info is not null && info.CatalogId == Id && info.Name == ExpectedModelName;

    private static bool ValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
}
