using WebRtcVad.NET;
using VoiceDetector = WebRtcVad.NET.WebRtcVad;

namespace QuanShuo.Native.Audio;

/// <summary>只观察本机 16 kHz / 16-bit / 单声道 PCM，不依赖识别结果或网络延迟。</summary>
internal sealed class SpeechInactivityMonitor : IDisposable
{
    private const int FrameBytes = 640; // 20 ms
    private const int IdleFrames = 60 * 50;
    private readonly byte[] frame = new byte[FrameBytes];
    private readonly Func<byte[], bool>? classify;
    private VoiceDetector vad = CreateVad();
    private int buffered;
    private int silentFrames;

    internal SpeechInactivityMonitor(Func<byte[], bool>? classify = null) => this.classify = classify;

    public bool Observe(ReadOnlySpan<byte> pcm)
    {
        while (!pcm.IsEmpty)
        {
            int take = Math.Min(FrameBytes - buffered, pcm.Length);
            pcm[..take].CopyTo(frame.AsSpan(buffered));
            buffered += take;
            pcm = pcm[take..];
            if (buffered != FrameBytes) continue;
            bool speech = classify?.Invoke(frame) ?? vad.HasSpeech(frame);
            silentFrames = speech ? 0 : Math.Min(IdleFrames, silentFrames + 1);
            buffered = 0;
        }
        // 先检查整个缓冲区，避免刚好在一分钟边界开口时，跳过末尾的语音。
        return silentFrames >= IdleFrames;
    }

    public void Reset()
    {
        buffered = silentFrames = 0;
        vad.Dispose();
        vad = CreateVad();
    }

    public void Dispose() => vad.Dispose();

    private static VoiceDetector CreateVad() => new()
    {
        SampleRate = SampleRate.Rate16kHz,
        FrameLength = FrameLength.Length20ms,
        OperatingMode = OperatingMode.LowBitrate,
    };
}
