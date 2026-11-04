using System.Text.Json;

namespace QuanShuo.Core.Transcription;

/// <summary>服务报告的实际模型资料；资源数字是预算，不是实时显存遥测。</summary>
public sealed record LocalModelInfo(
    string Name, string? Runtime, string? Device, string? Precision,
    double? EstimatedVramMb, double? EstimatedRamMb, string? CatalogId = null)
{
    // 可选资料出错不应破坏旧服务的录音协议；无可靠名称时视为未报告。
    public static LocalModelInfo? FromReady(JsonElement ready)
    {
        if (!ready.TryGetProperty("modelInfo", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        string? name = Text(info, "name", 200);
        if (name is null || name.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
        return new(name, Text(info, "runtime"), Text(info, "device"), Text(info, "precision"),
            Memory(info, "estimatedVramMb"), Memory(info, "estimatedRamMb"), Text(info, "catalogId"));
    }

    private static string? Text(JsonElement info, string key, int max = 100)
    {
        if (!info.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        string text = value.GetString()!.Trim();
        return text.Length is > 0 && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }

    private static double? Memory(JsonElement info, string key) =>
        info.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double mb) && double.IsFinite(mb) && mb is >= 0 and <= 1048576 ? mb : null;
}
