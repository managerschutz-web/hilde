using System.Buffers.Binary;

namespace Hilde.Core;

/// <summary>Minimaler WAV-Leser/-Schreiber für 16-kHz-Mono-PCM (16 Bit) – ohne Fremdbibliothek.</summary>
public static class WavIO
{
    public const int SampleRate = 16000;

    public static void WriteMono16k(string path, ReadOnlySpan<float> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        int dataBytes = samples.Length * 2;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);       // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);       // Mono
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        fs.Write(header);

        var buffer = new byte[8192];
        int pos = 0;
        foreach (var s in samples)
        {
            var v = (short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(pos), v);
            pos += 2;
            if (pos == buffer.Length) { fs.Write(buffer, 0, pos); pos = 0; }
        }
        if (pos > 0) fs.Write(buffer, 0, pos);
    }

    /// <summary>Liest eine 16-kHz-Mono-16-Bit-WAV. Robust gegen unvollständige Header (Absturz).</summary>
    public static float[] ReadMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (dataOffset, dataLength, channels, rate, bits) = ParseHeader(bytes);
        if (channels != 1 || rate != SampleRate || bits != 16)
            throw new InvalidDataException($"Erwartet 16 kHz Mono 16 Bit, gefunden {rate} Hz/{channels} Kanäle/{bits} Bit.");
        int count = dataLength / 2;
        var result = new float[count];
        for (int i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2)) / 32768f;
        return result;
    }

    /// <summary>
    /// Liefert Offset/Länge des data-Chunks. Ist die Länge 0 oder zu groß (Datei nach Absturz nicht
    /// finalisiert), wird bis zum Dateiende gelesen.
    /// </summary>
    public static (int DataOffset, int DataLength, int Channels, int SampleRate, int Bits) ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Keine gültige WAV-Datei.");
        int pos = 12, channels = 0, rate = 0, bits = 0;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 4)..]);
            int body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes[(body + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                int available = bytes.Length - body;
                int len = size <= 0 || size > available ? available : size;
                int blockAlign = Math.Max(1, channels * Math.Max(1, bits / 8));
                len -= len % blockAlign;
                return (body, len, channels, rate, bits);
            }
            if (size < 0) break;
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("WAV ohne data-Chunk.");
    }

    /// <summary>1 = PCM, 3 = IEEE-Float (WAVE_FORMAT_EXTENSIBLE wird aufgelöst).</summary>
    public static int ParseFormatTag(ReadOnlySpan<byte> bytes)
    {
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(pos + 4)..]);
            int body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[body..]);
                if (tag == 0xFFFE && size >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(body + 24)..]);
                return tag;
            }
            if (size < 0) break;
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException("WAV ohne fmt-Chunk.");
    }
}
