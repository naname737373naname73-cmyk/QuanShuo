using System.Reflection;
using NAudio.Wave;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Native.Audio;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Transcription;

// Feed PCM directly; never open a microphone, load a key, or contact a service.
internal static class IdlePauseChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly byte[] QuietFrame = new byte[640];

    internal static void Run()
    {
        using (var monitor = new SpeechInactivityMonitor(_ => false))
        {
            for (int i = 0; i < 2999; i++) Check(!monitor.Observe(QuietFrame), "paused before sixty seconds");
            Check(monitor.Observe(QuietFrame), "did not pause at sixty seconds");
            monitor.Reset();
            Check(!monitor.Observe(QuietFrame), "resume retained elapsed silence");
        }
        using (var monitor = new SpeechInactivityMonitor(frame => frame[0] == 1))
        {
            var speech = new byte[640]; speech[0] = 1;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                Check(!monitor.Observe(new byte[640 * 2999]), "speech did not reset idle interval");
                Check(!monitor.Observe(speech), "speech triggered idle pause");
            }
            Check(!monitor.Observe(new byte[640 * 2999]), "paused before boundary speech");
            var boundary = new byte[1280]; boundary[640] = 1;
            Check(!monitor.Observe(boundary), "ignored speech later in the same callback");
        }
        using (var monitor = new SpeechInactivityMonitor(_ => false))
        {
            Check(!monitor.Observe(new byte[60 * 32000 - 1]), "counted a partial PCM frame");
            Check(monitor.Observe(new byte[1]), "fragmented frames lost elapsed time");
            monitor.Reset();
            monitor.Observe(new byte[639]);
            monitor.Reset();
            Check(!monitor.Observe(new byte[60 * 32000 - 1]), "reset retained buffered audio");
        }
        Console.WriteLine("PASS idle timing: 60-second boundary, speech resets, fragmented PCM, fresh resume interval");
        RealSpeechPreventsPause();
        RecorderPauseAndResume();
        QueuedPauseNotifications();
    }

    private static void RealSpeechPreventsPause()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "16k-speech.raw"));
        Check(original.Length > 3200, "missing speech fixture");
        foreach (int divisor in new[] { 1, 10 })
        {
            byte[] speech = (byte[])original.Clone();
            for (int offset = 0; offset < speech.Length - 1; offset += 2)
            {
                short sample = (short)(BitConverter.ToInt16(speech, offset) / divisor);
                speech[offset] = (byte)sample; speech[offset + 1] = (byte)(sample >> 8);
            }
            using var monitor = new SpeechInactivityMonitor();
            for (int pass = 0; pass < 100; pass++)
                for (int offset = 0; offset < speech.Length; offset += 3200)
                    Check(!monitor.Observe(speech.AsSpan(offset, Math.Min(3200, speech.Length - offset))), "ongoing speech auto-paused without transcripts");
            Check(!monitor.Observe(new byte[59 * 32000]), "paused less than a minute after speech");
            Check(monitor.Observe(new byte[2 * 32000]), "silence after speech never paused");
        }
        Console.WriteLine("PASS actual speech fixture: normal and reduced volume keep recording; later silence pauses");
    }

    private static void RecorderPauseAndResume()
    {
        string directory = Path.Combine("artifacts", "idle-pause-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "silence.wav");
        using var recorder = new WavRecorder();
        Set(recorder, "writer", new PcmWaveWriter(path));
        var changes = new List<RecordingPauseState>();
        int audioFrames = 0, keepaliveFrames = 0;
        recorder.PauseChanged += (_, state) => changes.Add(state);
        recorder.AudioAvailable += (_, _) => audioFrames++;
        recorder.SilenceAvailable += (_, bytes) =>
        {
            Check(bytes.All(value => value == 0), "paused microphone data reached the provider");
            keepaliveFrames++;
        };
        byte[] buffer = new byte[3200];
        for (int i = 0; i < 599; i++) Feed(recorder, buffer);
        Check(!recorder.Paused && changes.Count == 0, "actual VAD paused early on silence");
        Feed(recorder, buffer);
        Check(recorder.PauseState == new RecordingPauseState(true, true) && changes.Count == 1, "actual VAD did not automatically pause");
        long length = new FileInfo(path).Length;
        byte[] noisy = Enumerable.Repeat((byte)40, 3200).ToArray();
        for (int i = 0; i < 20; i++) Feed(recorder, noisy);
        Check(audioFrames == 600 && keepaliveFrames == 20 && changes.Count == 1, "pause repeated or microphone audio escaped");
        Check(new FileInfo(path).Length == length, "paused audio was written into WAV");
        recorder.Paused = false;
        Check(recorder.PauseState == default && changes.Count == 2, "manual resume failed");
        for (int i = 0; i < 599; i++) Feed(recorder, buffer);
        Check(!recorder.Paused, "resume did not grant a full new minute");
        Feed(recorder, buffer);
        Check(recorder.Paused && changes.Count == 3, "second silent minute did not pause");
        recorder.Paused = false;
        recorder.Paused = true;
        Check(!recorder.PauseState.Automatic, "manual pause retained automatic reason");
        recorder.Stop();
        Feed(recorder, buffer);
        Check(audioFrames == 1200 && keepaliveFrames == 20, "stopped recorder emitted audio");
        Check(new FileInfo(path).Length == 44 + 1200L * 3200, "auto pause truncated prior WAV or included paused audio");
        Console.WriteLine("PASS recorder: real VAD, single automatic pause, WAV preserved, zero-only keepalive, resume, stop");
    }

    private static void QueuedPauseNotifications()
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedContext();
        SynchronizationContext.SetSynchronizationContext(context);
        string directory = Path.Combine("artifacts", "idle-pause-tests", Guid.NewGuid().ToString("N"));
        var coordinator = new RecordingCoordinator(new RecordStore(directory), new DpapiSecretStore(Path.Combine(directory, "unused.key")), new AppSettings());
        SynchronizationContext.SetSynchronizationContext(previous);
        using var recorder = new WavRecorder();
        Set(coordinator, "recorder", recorder);
        Set(coordinator, "current", new QuanShuoRecord
        {
            Id = "idle-test", Timestamp = DateTimeOffset.Now, AppName = "audit", WindowTitle = "audit",
            ImagePath = string.Empty, MarkdownPath = string.Empty,
        });
        recorder.PauseChanged += (sender, state) => typeof(RecordingCoordinator).GetMethod("OnRecorderPauseChanged", Hidden)!.Invoke(coordinator, [sender, state]);
        var delivered = new List<RecordingPauseState>();
        coordinator.PauseChanged += (_, state) => delivered.Add(state);
        recorder.Paused = true;
        coordinator.TogglePause();
        context.Drain();
        Check(delivered.Count == 1 && !delivered[0].Paused, "queued pause overrode a more recent resume");
        recorder.Paused = true;
        Set(coordinator, "recorder", null);
        Set(coordinator, "current", null);
        context.Drain();
        Check(delivered.Count == 1, "old recording callback updated the next window");
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS coordinator: stale notifications rejected after resume and finish");
    }

    private static void Feed(WavRecorder recorder, byte[] bytes) =>
        typeof(WavRecorder).GetMethod("OnDataAvailable", Hidden)!.Invoke(recorder, [null, new WaveInEventArgs(bytes, bytes.Length)]);

    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Hidden)!.SetValue(instance, value);
    private static void Check(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue(() => callback(state));
        public void Drain() { while (callbacks.TryDequeue(out var callback)) callback(); }
    }
}
