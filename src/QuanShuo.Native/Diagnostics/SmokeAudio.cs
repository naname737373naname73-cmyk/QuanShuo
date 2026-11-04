using System.Drawing.Imaging;
using QuanShuo.Core.Configuration;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Native.Audio;
using QuanShuo.Native.Configuration;
using QuanShuo.Native.Transcription;

namespace QuanShuo.Native.Diagnostics;

public static class SmokeAudio
{
    public static async Task<int> RunAsync(string outputRoot)
    {
        string runDirectory = Path.Combine(outputRoot, "audio data", DateTime.Now.Ticks.ToString());
        Directory.CreateDirectory(runDirectory);
        var secrets = new DpapiSecretStore(Path.Combine(runDirectory, "isolated-key.dpapi"));
        const string fakeKey = "sk-smoke-not-a-real-key";
        await secrets.SaveApiKeyAsync(fakeKey);
        string encrypted = await File.ReadAllTextAsync(Path.Combine(runDirectory, "isolated-key.dpapi"));
        bool roundtrip = secrets.LoadApiKey() == fakeKey;
        bool plaintextAbsent = !encrypted.Contains(fakeKey, StringComparison.Ordinal);
        await secrets.SaveApiKeyAsync(string.Empty);

        var store = new RecordStore(runDirectory);
        var metadata = new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "SmokeAudio", WindowTitle = "本地录音验证" };
        QuanShuoRecord record = await store.CreateAsync(
            metadata,
            (path, _) =>
            {
                using var bitmap = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
                bitmap.Save(path, ImageFormat.Png);
                return Task.CompletedTask;
            },
            string.Empty,
            TranscriptionStatus.Recording);
        var settings = new AppSettings { DataDirectory = runDirectory, DeleteAudioAfterSuccess = true };
        await using var coordinator = new RecordingCoordinator(store, secrets, settings);
        int deviceCount = WavRecorder.GetDevices().Count;
        await coordinator.StartAsync(record);
        await Task.Delay(600);
        await coordinator.FinishAsync();

        string wavPath = store.GetAudioPath(record);
        bool wavExists = File.Exists(wavPath);
        int declaredDataLength = -1;
        long wavLength = wavExists ? new FileInfo(wavPath).Length : 0;
        if (wavExists)
        {
            await using var stream = File.OpenRead(wavPath);
            using var reader = new BinaryReader(stream);
            stream.Position = 40;
            declaredDataLength = reader.ReadInt32();
        }

        QuanShuoRecord persisted = MarkdownRecordFile.Parse(record.MarkdownPath, await File.ReadAllTextAsync(record.MarkdownPath));
        bool valid = roundtrip && plaintextAbsent && wavExists && wavLength > 44 && declaredDataLength == wavLength - 44 &&
                     persisted.Status == TranscriptionStatus.Incomplete;
        string result = string.Join(Environment.NewLine,
            $"valid={valid}",
            $"dpapiRoundtrip={roundtrip}",
            $"plaintextAbsent={plaintextAbsent}",
            $"microphoneDevices={deviceCount}",
            $"status={persisted.Status}",
            $"wavPreserved={wavExists}",
            $"wavBytes={wavLength}",
            $"headerDataBytes={declaredDataLength}",
            $"wav={wavPath}");
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "audio-result.txt"), result);
        return valid ? 0 : 1;
    }
}
