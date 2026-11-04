using QuanShuo.Core.Models;

namespace QuanShuo.Core.Transcription;

public sealed record TranscriptUpdate(string Text, bool IsFinal);

public interface ITranscriptionProvider : IAsyncDisposable
{
    event EventHandler<TranscriptUpdate>? TranscriptReceived;

    Task StartAsync(TranscriptionOptions options, CancellationToken cancellationToken);

    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, CancellationToken cancellationToken);

    Task FinishAsync(CancellationToken cancellationToken);
}
