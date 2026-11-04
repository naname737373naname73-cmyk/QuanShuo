namespace QuanShuo.Core.Models;

public enum TranscriptionStatus
{
    NotRequested,
    Recording,
    Completed,
    Incomplete,
}

public enum SelectionMode
{
    Rectangle,
    Freeform,
}

public sealed record QuanShuoRecord
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string AppName { get; init; }
    public required string WindowTitle { get; init; }
    /// <summary>只说话、没有截图的记录为空字符串。</summary>
    public required string ImagePath { get; init; }
    public required string MarkdownPath { get; init; }
    public string? AudioPath { get; init; }
    public string Text { get; init; } = string.Empty;
    public TranscriptionStatus Status { get; init; } = TranscriptionStatus.NotRequested;

    public bool HasImage => ImagePath.Length > 0;
    public string RelativeDirectory => Path.GetFileName(Path.GetDirectoryName(MarkdownPath)) ?? string.Empty;
}

public sealed record CaptureMetadata
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string AppName { get; init; }
    public required string WindowTitle { get; init; }
}

public sealed record TranscriptionOptions
{
    public required string ApiKey { get; init; }
    public string Model { get; init; } = QuanShuo.Core.Transcription.SpeechModels.Default;
    public required string Endpoint { get; init; }
    public string Language { get; init; } = "zh";
    public string? VocabularyId { get; init; }
}
