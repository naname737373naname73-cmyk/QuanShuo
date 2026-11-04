using System.Text.Json;
using System.Text.Json.Serialization;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Native.Configuration;

public sealed class AppSettingsStore
{
    private readonly string path;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public AppSettingsStore()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuanShuo");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, jsonOptions) ?? new AppSettings();
            return settings with { SpeechModel = SpeechModels.Normalize(settings.SpeechModel) };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("设置文件无法读取。请关闭圈说后检查 settings.json 的格式；API Key 不在此文件中。", exception);
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(settings, jsonOptions);
        return AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
    }
}
