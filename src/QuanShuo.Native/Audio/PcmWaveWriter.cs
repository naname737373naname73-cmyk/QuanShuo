namespace QuanShuo.Native.Audio;

public sealed class PcmWaveWriter : IDisposable
{
    private readonly FileStream stream;
    private readonly BinaryWriter writer;
    private int dataLength;
    private DateTime lastFlush = DateTime.UtcNow;
    private bool disposed;

    public PcmWaveWriter(string path)
    {
        stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(0);
        stream.Flush(true);
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (disposed) throw new ObjectDisposedException(nameof(PcmWaveWriter));
        long end = stream.Length;
        stream.Position = end;
        writer.Write(pcm);
        dataLength += pcm.Length;
        UpdateHeader();
        if ((DateTime.UtcNow - lastFlush) >= TimeSpan.FromSeconds(1))
        {
            stream.Flush(true);
            lastFlush = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UpdateHeader();
        stream.Flush(true);
        writer.Dispose();
        stream.Dispose();
    }

    private void UpdateHeader()
    {
        long end = 44L + dataLength;
        stream.Position = 4;
        writer.Write(36 + dataLength);
        stream.Position = 40;
        writer.Write(dataLength);
        stream.Position = end;
    }
}
