using NAudio.Wave;

namespace QuanShuo.Native.Audio;

public sealed record MicrophoneDevice(int Index, string Name);

public sealed class AudioDataEventArgs(byte[] pcm, float level) : EventArgs
{
    public byte[] Pcm { get; } = pcm;
    public float Level { get; } = level;
}

public sealed class WavRecorder : IDisposable
{
    private readonly object sync = new();
    private WaveIn? waveIn;
    private PcmWaveWriter? writer;
    private volatile bool paused;

    public event EventHandler<AudioDataEventArgs>? AudioAvailable;

    /// <summary>暂停时每个缓冲区触发一次，参数是同样时长的静音。不写入 WAV。</summary>
    public event EventHandler<byte[]>? SilenceAvailable;

    public event EventHandler<string>? RecordingFailed;

    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    public static IReadOnlyList<MicrophoneDevice> GetDevices()
    {
        var devices = new List<MicrophoneDevice>();
        for (int index = 0; index < WaveIn.DeviceCount; index++)
        {
            devices.Add(new MicrophoneDevice(index, WaveIn.GetCapabilities(index).ProductName));
        }

        return devices;
    }

    public void Start(string path, int deviceNumber = 0)
    {
        lock (sync)
        {
            if (waveIn is not null) throw new InvalidOperationException("已经有一条录音正在进行。");
            writer = new PcmWaveWriter(path);
            try
            {
                waveIn = new WaveIn
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 4,
                };
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;
                waveIn.StartRecording();
            }
            catch
            {
                writer.Dispose();
                writer = null;
                waveIn?.Dispose();
                waveIn = null;
                throw;
            }
        }
    }

    public void Stop()
    {
        WaveIn? current;
        PcmWaveWriter? currentWriter;
        lock (sync)
        {
            current = waveIn;
            currentWriter = writer;
            waveIn = null;
            writer = null;
        }

        if (current is null)
        {
            currentWriter?.Dispose();
            return;
        }

        current.DataAvailable -= OnDataAvailable;
        current.RecordingStopped -= OnRecordingStopped;
        // WaveIn.Dispose 会等待音频回调线程结束；回调线程在 OnDataAvailable 里
        // 抢同一把锁，所以 NAudio 的停止和释放必须在锁外做，且不能无限等驱动。
        var teardown = Task.Run(() =>
        {
            try { current.StopRecording(); }
            catch { /* 设备已被拔出时 StopRecording 可能抛错，Dispose 兜底回收 */ }
            current.Dispose();
        });
        teardown.Wait(TimeSpan.FromSeconds(3));
        // 回调只会通过 writer 字段拿写入器，字段已置空，此处释放不会再有并发写入。
        currentWriter?.Dispose();
    }

    public void Dispose() => Stop();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        if (paused)
        {
            SilenceAvailable?.Invoke(this, new byte[e.BytesRecorded]);
            return;
        }

        lock (sync)
        {
            if (writer is null) return;
            var pcm = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
            writer.Write(pcm);
            double squareSum = 0;
            int sampleCount = pcm.Length / 2;
            for (int index = 0; index < sampleCount; index++)
            {
                short sample = BitConverter.ToInt16(pcm, index * 2);
                double normalized = sample / 32768.0;
                squareSum += normalized * normalized;
            }

            float level = sampleCount == 0 ? 0 : Math.Clamp((float)(Math.Sqrt(squareSum / sampleCount) * 7), 0, 1);
            AudioAvailable?.Invoke(this, new AudioDataEventArgs(pcm, level));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            RecordingFailed?.Invoke(this, "麦克风停止工作：" + e.Exception.Message + "。录音文件已保留，请检查设备后重试。");
        }
    }
}
