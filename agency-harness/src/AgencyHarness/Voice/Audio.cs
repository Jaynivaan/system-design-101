using System.Buffers.Binary;

namespace AgencyHarness.Voice;

/// <summary>
/// Phone audio is 8 kHz G.711 μ-law (one byte per sample, 160 bytes per 20 ms frame).
/// Models want 16-bit PCM, usually at 16 kHz. These helpers convert between the two.
/// </summary>
public static class G711
{
    public static short Decode(byte mulaw)
    {
        int u = ~mulaw & 0xFF;
        int sign = u & 0x80;
        int exponent = (u >> 4) & 0x07;
        int mantissa = u & 0x0F;
        int sample = (((mantissa << 3) + 0x84) << exponent) - 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }

    public static byte Encode(short pcm)
    {
        const int Bias = 0x84, Clip = 32635;
        int s = pcm;
        int sign = 0;
        if (s < 0) { sign = 0x80; s = -s; }
        if (s > Clip) s = Clip;
        s += Bias;
        int exponent = 7;
        for (int mask = 0x4000; (s & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        int mantissa = (s >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short[] DecodeAll(ReadOnlySpan<byte> mulaw)
    {
        var pcm = new short[mulaw.Length];
        for (int i = 0; i < mulaw.Length; i++) pcm[i] = Decode(mulaw[i]);
        return pcm;
    }

    public static byte[] EncodeAll(ReadOnlySpan<short> pcm)
    {
        var mulaw = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) mulaw[i] = Encode(pcm[i]);
        return mulaw;
    }
}

public static class Pcm
{
    /// <summary>Linear-interpolation resampler. Fine for speech at telephone bandwidth.</summary>
    public static short[] Resample(ReadOnlySpan<short> input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input.ToArray();
        int outLen = (int)((long)input.Length * toRate / fromRate);
        var output = new short[outLen];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            double frac = pos - i0;
            output[i] = (short)Math.Round(input[i0] * (1 - frac) + input[i1] * frac);
        }
        return output;
    }

    /// <summary>Root-mean-square level in dBFS (0 = full scale, about -90 = silence).</summary>
    public static double LevelDb(ReadOnlySpan<short> frame)
    {
        if (frame.Length == 0) return -96;
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        double rms = Math.Sqrt(sum / frame.Length) / 32768.0;
        return rms <= 1e-6 ? -96 : 20 * Math.Log10(rms);
    }

    /// <summary>Split audio into 20 ms μ-law frames of 160 bytes (8 kHz), as Twilio expects.</summary>
    public static IEnumerable<byte[]> ToMulawFrames(short[] pcm8k)
    {
        const int frame = 160;
        for (int i = 0; i < pcm8k.Length; i += frame)
        {
            var chunk = new short[frame];   // pad the last frame with silence
            Array.Copy(pcm8k, i, chunk, 0, Math.Min(frame, pcm8k.Length - i));
            yield return G711.EncodeAll(chunk);
        }
    }
}

public record PcmAudio(short[] Samples, int SampleRate)
{
    public double Seconds => (double)Samples.Length / SampleRate;
}

/// <summary>Minimal WAV reader/writer: PCM 16-bit and float 32-bit, any channel count (mixed to mono).</summary>
public static class Wav
{
    public static byte[] Write(PcmAudio audio)
    {
        int dataLen = audio.Samples.Length * 2;
        var buf = new byte[44 + dataLen];
        var s = buf.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataLen);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);          // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);          // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], audio.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], audio.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataLen);
        for (int i = 0; i < audio.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s[(44 + i * 2)..], audio.Samples[i]);
        return buf;
    }

    public static PcmAudio Read(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV file.");
        int pos = 12, format = 0, channels = 1, rate = 0, bits = 16;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav[(pos + 4)..]);
            var body = wav.Slice(pos + 8, Math.Min(size < 0 ? wav.Length - pos - 8 : size, wav.Length - pos - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26) format = BinaryPrimitives.ReadInt16LittleEndian(body[24..]);   // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (rate == 0) throw new InvalidDataException("WAV data before fmt chunk.");
                return new PcmAudio(ToMono(body, format, channels, bits), rate);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }

    private static short[] ToMono(ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        int bytesPer = bits / 8;
        int frames = data.Length / (bytesPer * channels);
        var mono = new short[frames];
        for (int f = 0; f < frames; f++)
        {
            double acc = 0;
            for (int c = 0; c < channels; c++)
            {
                var sample = data.Slice((f * channels + c) * bytesPer, bytesPer);
                acc += (format, bits) switch
                {
                    (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample),
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(sample) * 32767.0,
                    (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(sample) / 65536.0,
                    _ => throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bits}-bit)."),
                };
            }
            mono[f] = (short)Math.Clamp(Math.Round(acc / channels), short.MinValue, short.MaxValue);
        }
        return mono;
    }
}
