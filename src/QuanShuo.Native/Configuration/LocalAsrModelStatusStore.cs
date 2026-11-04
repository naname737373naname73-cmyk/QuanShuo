using System.Text.Json;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Configuration;

public sealed record LocalAsrModelStatus(
    string Endpoint, string Model, bool Connected, DateTimeOffset CheckedAt, LocalModelInfo? ModelInfo);

/// <summary>单独保存上次测试资料，不回写设置，避免覆盖用户正在编辑的配置。</summary>
public sealed class LocalAsrModelStatusStore
{
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalAsrModelStatusStore(string? directory = null) => path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuanShuo"), "local-asr-model.json");

    public async Task<LocalAsrModelStatus?> ReadAsync()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            return JsonSerializer.Deserialize<LocalAsrModelStatus>(await File.ReadAllTextAsync(path), JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task<bool> SaveAsync(LocalAsrModelStatus status)
    {
        try
        {
            await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(status, JsonOptions));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
