using System.Text.Json;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Configuration;

public sealed record LocalAsrProfileEntry(string Id, LocalAsrProfile? Profile, string? Error);

public sealed class LocalAsrProfileStore
{
    private readonly string directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LocalAsrProfileStore(string? directory = null) => this.directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuanShuo", "local-asr-profiles");

    public async Task<LocalAsrProfileEntry[]> ReadAsync()
    {
        var entries = await Task.WhenAll(LocalAsrCatalog.Models.Keys.Select(ReadOneAsync));
        var duplicates = entries.Where(e => e.Profile is not null).GroupBy(e => (e.Profile!.Endpoint, e.Profile.Model))
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(e => e.Id)).ToHashSet();
        return entries.Select(e => duplicates.Contains(e.Id)
            ? e with { Profile = null, Error = "多个模型登记了相同地址和配置 ID，请让安装 agent 修正。" } : e).ToArray();
    }

    public async Task<LocalAsrProfile> ResolveAsync(string id, LocalAsrProfile expected)
    {
        LocalAsrCatalog.Name(id);
        var entry = (await ReadAsync()).Single(e => e.Id == id);
        if (entry.Profile is null) throw new InvalidOperationException(entry.Error ?? "此模型尚未接入，请先让 agent 安装并登记。");
        if (entry.Profile != expected) throw new InvalidOperationException("模型登记资料已变化，请刷新列表后再切换。");
        return entry.Profile;
    }

    private async Task<LocalAsrProfileEntry> ReadOneAsync(string id)
    {
        string path = Path.Combine(directory, id + ".json");
        try
        {
            if (!File.Exists(path)) return new(id, null, null);
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("模型登记文件过大。");
            var profile = JsonSerializer.Deserialize<LocalAsrProfile>(await File.ReadAllTextAsync(path), JsonOptions)
                ?? throw new InvalidDataException("模型登记文件为空。");
            profile.Validate(id);
            return new(id, profile, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(id, null, "登记资料无法使用，请让安装 agent 检查该模型的 JSON 文件、地址和模型 ID。");
        }
    }
}
