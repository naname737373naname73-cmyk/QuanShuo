using QuanShuo.Core.Models;
using QuanShuo.Core.Transcription;

namespace QuanShuo.Core.Configuration;

public sealed record AppSettings
{
    public long SettingsRevision { get; init; }
    public string Hotkey { get; init; } = "Ctrl+Alt+G";
    public string SpeechHotkey { get; init; } = "Ctrl+Alt+H";
    public string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "QuanShuo");
    public string FreeformFillColor { get; init; } = "#FFFFFF";
    public bool DeleteAudioAfterSuccess { get; init; } = true;
    public bool AutoStart { get; init; } = true;
    public bool QuietMode { get; init; } = false;
    public bool AutoSpeakAfterSelection { get; init; } = false;
    public SelectionMode LastSelectionMode { get; init; } = SelectionMode.Rectangle;
    public string SpeechModel { get; init; } = SpeechModels.Default;
    public string SpeechProvider { get; init; } = SpeechProviders.DashScope;
    public string LocalSpeechEndpoint { get; init; } = "ws://127.0.0.1:8765/asr";
    public string LocalSpeechModel { get; init; } = "default";
    public string SpeechEndpoint { get; init; } = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
    public string SpeechLanguage { get; init; } = "zh";
    public string? MicrophoneDeviceId { get; init; }
    public string? VocabularyId { get; init; }
    public int? FixedDayPeriod { get; init; }
    public string Skin { get; init; } = "starlake";
    public bool FirstRunCompleted { get; init; }
}
