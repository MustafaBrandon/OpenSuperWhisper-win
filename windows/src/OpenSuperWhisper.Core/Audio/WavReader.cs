using System.Buffers.Binary;

namespace OpenSuperWhisper.Core.Audio;

/// <summary>
/// Minimal RIFF/WAVE reader producing the 16 kHz mono float samples whisper wants.
/// </summary>
/// <remarks>
/// Deliberately narrow: it handles 16-bit and 32-bit-float PCM, which covers our own
/// captures (16 kHz mono s16, per the mac app's recorder settings) and the jfk.wav
/// fixture. Arbitrary containers - mp3, m4a, wma - are Media Foundation's job in the
/// full audio pipeline (module 02); this exists so M1 can transcribe without pulling
/// that in first.
/// </remarks>
public static class WavReader
{
    public const int WhisperSampleRate = 16000;

    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Reads a WAV file as mono float samples at its native rate.</summary>
    /// <returns>The samples and the sample rate they are at.</returns>
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes);
    }

    public static (float[] Samples, int SampleRate) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        ushort format = 0, channels = 0, bitsPerSample = 0;
        var sampleRate = 0;
        ReadOnlySpan<byte> data = default;
        var haveFormat = false;

        // Walk the chunk list rather than assuming fmt is immediately followed by
        // data - real files interleave LIST, fact and other chunks.
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var chunkId = bytes.Slice(pos, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos + 4, 4));
            var body = pos + 8;

            if (body + chunkSize > (uint)bytes.Length)
            {
                // Truncated final chunk: take what is actually there rather than throwing,
                // so a recording cut short by a crash is still transcribable.
                chunkSize = (uint)(bytes.Length - body);
            }

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));

                if (format == FormatExtensible && chunkSize >= 40)
                {
                    // WAVEFORMATEXTENSIBLE: the real format is the first two bytes of the GUID.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
                }

                haveFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = bytes.Slice(body, (int)chunkSize);
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte.
            pos = body + (int)chunkSize + ((chunkSize & 1) == 1 ? 1 : 0);
        }

        if (!haveFormat) throw new InvalidDataException("WAV file has no fmt chunk.");
        if (data.IsEmpty) throw new InvalidDataException("WAV file has no data chunk.");
        if (channels == 0) throw new InvalidDataException("WAV file reports zero channels.");

        var samples = format switch
        {
            FormatPcm when bitsPerSample == 16 => DecodePcm16(data, channels),
            FormatFloat when bitsPerSample == 32 => DecodeFloat32(data, channels),
            _ => throw new NotSupportedException(
                $"Unsupported WAV format {format} at {bitsPerSample} bits. " +
                "Only 16-bit PCM and 32-bit float are handled here."),
        };

        return (samples, sampleRate);
    }

    private static float[] DecodePcm16(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (2 * channels);
        var result = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            // Average the channels. The mac app mixes only channels above an RMS floor
            // to avoid diluting a single live mic on a multi-channel interface; that
            // belongs in the full pipeline, not in a fixture reader.
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = (frame * channels + ch) * 2;
                sum += BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)) / 32768f;
            }
            result[frame] = sum / channels;
        }

        return result;
    }

    private static float[] DecodeFloat32(ReadOnlySpan<byte> data, int channels)
    {
        var frames = data.Length / (4 * channels);
        var result = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = (frame * channels + ch) * 4;
                sum += BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)));
            }
            result[frame] = sum / channels;
        }

        return result;
    }
}
