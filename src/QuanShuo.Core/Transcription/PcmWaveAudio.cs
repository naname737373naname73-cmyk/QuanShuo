using System.Buffers.Binary;

namespace QuanShuo.Core.Transcription;

public static class PcmWaveAudio
{
    /// <summary>跳过 RIFF 元数据，不把固定 44 字节之后的全部内容误当成 PCM。</summary>
    public static ReadOnlyMemory<byte> Read(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("请输入 16kHz、单声道、PCM16 格式的 WAV。");
        bool validFormat = false;
        ReadOnlyMemory<byte>? data = null;
        for (long offset = 12; offset + 8 <= wav.Length;)
        {
            int index = (int)offset;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(index + 4, 4));
            if (offset + 8 + size > wav.Length) throw new InvalidDataException("WAV 数据不完整。");
            if (wav.AsSpan(index, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException("WAV 格式信息不完整。");
                var format = wav.AsSpan(index + 8, 16);
                validFormat = BinaryPrimitives.ReadUInt16LittleEndian(format) == 1 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) == 16000 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(format[8..]) == 32000 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(format[12..]) == 2 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) == 16;
            }
            else if (wav.AsSpan(index, 4).SequenceEqual("data"u8))
            {
                if (data is not null || size % 2 != 0) throw new InvalidDataException("WAV 的 PCM 数据块格式不受支持。");
                data = wav.AsMemory(index + 8, checked((int)size));
            }
            offset += 8L + size + (size & 1);
        }
        if (!validFormat || data is null) throw new InvalidDataException("WAV 必须是 16kHz、单声道、PCM16（不压缩）格式。");
        return data.Value;
    }
}
