using System.Buffers.Binary;
using System.Text;
using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.Audio;

/// <summary>
/// The 6D Mark II port's scratch WAV lined up with its clip: it starts on the clip's first
/// frame and lasts as long as the picture, so an editor can lay it under the clip with no
/// offset. Nothing is copied - reads map straight into the WAV on the card.
///
/// The port's recorder starts 0.4..1.6 s ahead of the first frame, by an amount that
/// changes every take; from v91 each clip says when (an INFO block
/// "scratch_wav_start_us=N", relative to the clip's time zero, which is also the base of
/// every frame's timestamp). When one channel carries LTC (a Tentacle Sync in the mic
/// jack), the WAV also carries the timecode of the first frame - a Broadcast WAV time
/// reference plus iXML - and <see cref="StartTimecode"/> gives it to the DNGs.
/// </summary>
public sealed class ScratchWav : IVirtualFileContent
{
    readonly string _path;
    readonly byte[] _header;
    readonly long _srcData, _srcDataSize;     // the source WAV's data chunk
    readonly long _first;                     // byte of the source data that is our first byte
    readonly long _dataSize;

    public long Size => _header.Length + _dataSize;
    /// <summary>Seconds of the source WAV before the first frame.</summary>
    public double LeadSeconds { get; }
    /// <summary>Timecode of the first frame, from LTC on one of the channels; null if none.</summary>
    public Timecode? StartTimecode { get; }
    /// <summary>The channel the LTC was on (0 = left), or -1.</summary>
    public int LtcChannel { get; } = -1;
    /// <summary>Where the first frame falls between LTC frames (-0.5 .. 0.5).</summary>
    public double SubFrame { get; }

    ScratchWav(string path, byte[] header, long srcData, long srcDataSize, long first, long dataSize,
               double lead, Timecode? tc, int ltcChannel, double subFrame)
    {
        _path = path; _header = header; _srcData = srcData; _srcDataSize = srcDataSize;
        _first = first; _dataSize = dataSize;
        LeadSeconds = lead; StartTimecode = tc; LtcChannel = ltcChannel; SubFrame = subFrame;
    }

    /// <summary>
    /// The aligned scratch WAV for a clip, or null when the clip does not say when its WAV
    /// started (recorded before v91) or the WAV is not plain PCM.
    /// <paramref name="latencyMs"/> is the calibration: how much later the WAV's first sample
    /// comes than the moment the camera stamped.
    /// </summary>
    public static ScratchWav? Create(MlvClip clip, string wavPath, double? fpsOverride = null, double latencyMs = 0)
    {
        if (clip.ScratchWavStartUs is not { } startUs || clip.Frames.Count == 0) return null;
        if (ReadFormat(wavPath) is not { } fmt) return null;
        var (format, channels, rate, blockAlign, bits, dataOffset, dataSize) = fmt;
        if (format != 1 || channels == 0 || rate == 0 || blockAlign == 0) return null;

        double lead = ((long)clip.Frames[0].Timestamp - startUs) / 1e6 + latencyMs / 1000.0;
        long startSample = (long)Math.Round(lead * rate);
        var (num, den) = clip.FrameRate(fpsOverride);
        long samples = (long)Math.Ceiling((double)rate * den * clip.Frames.Count / num);
        long size = samples * blockAlign;

        Timecode? tc = null;
        int ltcChannel = -1;
        double sub = 0;
        if (bits == 16)
        {
            // look a couple of seconds either side of the first frame, every channel
            long look0 = Math.Max(0, startSample - 2L * rate);
            var pcm = ReadPcm(wavPath, dataOffset, dataSize, look0, 5L * rate, channels);
            for (int c = 0; c < channels && tc is null; c++)
            {
                var ch = new short[pcm.Length / channels];
                for (int i = 0; i < ch.Length; i++) ch[i] = pcm[i * channels + c];
                if (Ltc.At(ch, (int)rate, startSample - look0) is { } hit)
                {
                    tc = hit.Tc;
                    sub = hit.Frames - hit.Tc.Frames;
                    ltcChannel = c;
                }
            }
        }

        var header = BuildHeader(channels, rate, blockAlign, bits, size, tc, clip.BaseName, sub);
        return new ScratchWav(wavPath, header, dataOffset, dataSize, startSample * blockAlign, size, lead, tc, ltcChannel, sub);
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Size) return 0;
        int length = (int)Math.Min(destination.Length, Size - offset);
        var dest = destination[..length];
        int done = 0;
        if (offset < _header.Length)
        {
            int n = (int)Math.Min(length, _header.Length - offset);
            _header.AsSpan((int)offset, n).CopyTo(dest);
            done = n;
        }
        if (done == length) return length;

        // our data byte d is source data byte _first + d; outside the source is silence
        long d0 = offset + done - _header.Length;
        long s0 = _first + d0, s1 = s0 + (length - done);
        var rest = dest[done..];
        rest.Clear();
        long from = Math.Max(s0, 0), to = Math.Min(s1, _srcDataSize);
        if (to > from)
        {
            using var h = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var target = rest.Slice((int)(from - s0), (int)(to - from));
            int total = 0;
            while (total < target.Length)
            {
                int n = RandomAccess.Read(h, target[total..], _srcData + from + total);
                if (n <= 0) break;
                total += n;
            }
        }
        return length;
    }

    /// <summary>fmt fields and the data chunk of a RIFF/WAVE file, or null.</summary>
    static (ushort Format, ushort Channels, uint Rate, ushort BlockAlign, ushort Bits, long DataOffset, long DataSize)? ReadFormat(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(h);
        Span<byte> b = stackalloc byte[24];
        if (RandomAccess.Read(h, b[..12], 0) < 12 || !b[..4].SequenceEqual("RIFF"u8) || !b[8..12].SequenceEqual("WAVE"u8)) return null;
        (ushort, ushort, uint, ushort, ushort)? fmt = null;
        for (long pos = 12; pos + 8 <= length;)
        {
            if (RandomAccess.Read(h, b[..8], pos) < 8) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[4..8]);
            if (b[..4].SequenceEqual("fmt "u8) && size >= 16 && RandomAccess.Read(h, b[8..24], pos + 8) == 16)
                fmt = (BinaryPrimitives.ReadUInt16LittleEndian(b[8..]), BinaryPrimitives.ReadUInt16LittleEndian(b[10..]),
                       BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), BinaryPrimitives.ReadUInt16LittleEndian(b[20..]),
                       BinaryPrimitives.ReadUInt16LittleEndian(b[22..]));
            else if (b[..4].SequenceEqual("data"u8) && fmt is { } f)
                return (f.Item1, f.Item2, f.Item3, f.Item4, f.Item5, pos + 8, Math.Min(size, length - pos - 8));
            pos += 8 + size + (size & 1);
        }
        return null;
    }

    static short[] ReadPcm(string path, long dataOffset, long dataSize, long firstSample, long count, int channels)
    {
        long from = firstSample * 2 * channels, bytes = Math.Max(0, Math.Min(count * 2 * channels, dataSize - from));
        var buf = new byte[bytes];
        using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            RandomAccess.Read(h, buf, dataOffset + from);
        var pcm = new short[bytes / 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(i * 2));
        return pcm;
    }

    static byte[] BuildHeader(int channels, uint rate, int blockAlign, int bits, long dataSize, Timecode? tc,
                              string clipName, double sub)
    {
        using var s = new MemoryStream();
        void Chunk(string id, byte[] payload)
        {
            s.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> n = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(n, (uint)payload.Length);
            s.Write(n);
            s.Write(payload);
            if ((payload.Length & 1) != 0) s.WriteByte(0);
        }

        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), rate * (uint)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);
        Chunk("fmt ", fmt);

        if (tc is { } t)
        {
            // samples since midnight at the first frame (Broadcast WAV time reference)
            double frames = t.Frames + sub;
            ulong timeRef = (ulong)Math.Round(frames * rate * t.RateDen / t.RateNum);
            var bext = new byte[602];
            var description = Encoding.ASCII.GetBytes($"6D2 scratch for {clipName}, aligned to frame 1");
            description.AsSpan(0, Math.Min(256, description.Length)).CopyTo(bext);
            Encoding.ASCII.GetBytes("MLV Drive").CopyTo(bext, 256);
            BinaryPrimitives.WriteUInt32LittleEndian(bext.AsSpan(338), (uint)timeRef);
            BinaryPrimitives.WriteUInt32LittleEndian(bext.AsSpan(342), (uint)(timeRef >> 32));
            BinaryPrimitives.WriteUInt16LittleEndian(bext.AsSpan(346), 1);
            Chunk("bext", bext);
            string ixml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><BWFXML><IXML_VERSION>1.5</IXML_VERSION>" +
                $"<SPEED><TIMECODE_RATE>{t.RateNum}/{t.RateDen}</TIMECODE_RATE><TIMECODE_FLAG>{(t.DropFrame ? "DF" : "NDF")}</TIMECODE_FLAG>" +
                $"<FILE_SAMPLE_RATE>{rate}</FILE_SAMPLE_RATE>" +
                $"<TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_HI>{timeRef >> 32}</TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_HI>" +
                $"<TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_LO>{timeRef & 0xFFFFFFFF}</TIMESTAMP_SAMPLES_SINCE_MIDNIGHT_LO>" +
                $"<TIMESTAMP_SAMPLE_RATE>{rate}</TIMESTAMP_SAMPLE_RATE></SPEED></BWFXML>";
            Chunk("iXML", Encoding.UTF8.GetBytes(ixml));
        }

        // data chunk header only; the samples follow it
        s.Write("data"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, dataSize));
        s.Write(size);
        var chunks = s.ToArray();

        // RIFF size: "WAVE" + every chunk header and payload + the samples
        var header = new byte[12 + chunks.Length];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)Math.Min(uint.MaxValue, 4 + chunks.Length + dataSize));
        "WAVE"u8.CopyTo(header.AsSpan(8));
        chunks.CopyTo(header, 12);
        return header;
    }
}
