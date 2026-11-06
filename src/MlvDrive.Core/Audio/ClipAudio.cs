// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;
using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.Audio;

/// <summary>A read-only file whose bytes are produced on demand.</summary>
public interface IVirtualFileContent
{
    long Size { get; }
    /// <summary>Read from <paramref name="offset"/>; returns bytes written (0 at or past the end).</summary>
    int Read(long offset, Span<byte> destination);
}

/// <summary>
/// The WAV track for a clip: audio recorded inside the MLV (AUDF blocks) laid out by
/// timestamp on the video timeline. The layout is computed once when the clip is opened,
/// so a read only copies the packets that overlap it.
/// </summary>
public sealed class EmbeddedWav : IVirtualFileContent
{
    const int HeaderSize = 44;

    readonly MlvClip _clip;
    readonly byte[] _header;
    readonly long _dataSize;
    // Non-overlapping, sorted by destination: [Start, Start+Length) of the data chunk.
    readonly (long Start, int Length, int FileIndex, long Source)[] _segments;

    public long Size => HeaderSize + _dataSize;

    EmbeddedWav(MlvClip clip, byte[] header, long dataSize, (long, int, int, long)[] segments)
    {
        _clip = clip;
        _header = header;
        _dataSize = dataSize;
        _segments = segments;
    }

    /// <summary>The clip's embedded audio, or null when it has none.</summary>
    public static EmbeddedWav? Create(MlvClip clip, double? fpsOverride = null)
    {
        if (clip.Wavi is not { } w || clip.Audio.Count == 0 || clip.Frames.Count == 0) return null;
        if (w.BytesPerSecond == 0 || w.Channels == 0 || w.SamplingRate == 0) return null;
        int align = w.EffectiveBlockAlign;

        ulong firstVideo = clip.Frames.Min(f => f.Timestamp);
        var (num, den) = clip.FrameRate(fpsOverride);
        // Length matches the picture: frame count / fps, in whole sample frames.
        long dataSize = (long)Math.Ceiling((double)w.BytesPerSecond * den * clip.Frames.Count / num);
        dataSize = AlignUp(dataSize, align);

        var segments = new List<(long, int, int, long)>();
        long cursor = 0;
        foreach (var a in clip.Audio)
        {
            long start = a.Timestamp >= firstVideo
                ? (long)((a.Timestamp - firstVideo) * (decimal)w.BytesPerSecond / 1_000_000m)
                : -(long)((firstVideo - a.Timestamp) * (decimal)w.BytesPerSecond / 1_000_000m);
            start = start >= 0 ? start - start % align : -(-start - -start % align);
            long end = start + a.PayloadSize;
            long from = Math.Max(Math.Max(start, cursor), 0);
            long to = Math.Min(end, dataSize);
            if (to <= from) continue;
            segments.Add((from, (int)(to - from), a.FileIndex, a.PayloadOffset + (from - start)));
            cursor = to;
        }

        var header = new byte[HeaderSize];
        var h = header.AsSpan();
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(uint.MaxValue, 36 + dataSize));
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], w.Format == 0 ? (ushort)1 : w.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], w.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], w.SamplingRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], w.BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], w.BitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(uint.MaxValue, dataSize));

        return new EmbeddedWav(clip, header, dataSize, segments.ToArray());
    }

    static long AlignUp(long v, int a) => v % a == 0 ? v : v + a - v % a;

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Size) return 0;
        int length = (int)Math.Min(destination.Length, Size - offset);
        var dest = destination[..length];
        dest.Clear();
        int done = 0;
        if (offset < HeaderSize)
        {
            int n = (int)Math.Min(length, HeaderSize - offset);
            _header.AsSpan((int)offset, n).CopyTo(dest);
            done = n;
        }
        if (done == length) return length;

        long dataStart = offset + done - HeaderSize, dataEnd = offset + length - HeaderSize;
        int i = FirstSegmentEndingAfter(dataStart);
        for (; i < _segments.Length && _segments[i].Start < dataEnd; i++)
        {
            var s = _segments[i];
            long from = Math.Max(s.Start, dataStart), to = Math.Min(s.Start + s.Length, dataEnd);
            if (to <= from) continue;
            var target = dest.Slice((int)(from - dataStart) + done, (int)(to - from));
            _clip.ReadFile(s.FileIndex, s.Source + (from - s.Start), target);
        }
        return length;
    }

    int FirstSegmentEndingAfter(long position)
    {
        int lo = 0, hi = _segments.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_segments[mid].Start + _segments[mid].Length <= position) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

/// <summary>A real file on disk shown unchanged (the 6D Mark II's scratch WAV).</summary>
public sealed class PassThroughFile : IVirtualFileContent
{
    readonly string _path;
    public long Size { get; }

    public PassThroughFile(string path)
    {
        _path = path;
        Size = new FileInfo(path).Length;
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Size) return 0;
        using var h = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int total = 0;
        int want = (int)Math.Min(destination.Length, Size - offset);
        while (total < want)
        {
            int n = RandomAccess.Read(h, destination[total..want], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// A separately recorded WAV for a clip: NAME.WAV beside the clip, or in an AUDIO
    /// folder next to the clip's folder (the 6D Mark II port files them in DCIM/AUDIO).
    /// </summary>
    public static string? FindScratchWav(string mlvPath)
    {
        var dir = Path.GetDirectoryName(mlvPath);
        if (dir is null) return null;
        var stem = Path.GetFileNameWithoutExtension(mlvPath);
        var candidates = new List<string> { dir };
        if (Path.GetDirectoryName(dir) is { } parent) candidates.Add(Path.Combine(parent, "AUDIO"));
        foreach (var d in candidates)
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, stem + ".*"))
                if (string.Equals(Path.GetExtension(f), ".wav", StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }
}

/// <summary>Which audio a clip folder shows, and the timecode it gives the DNGs.</summary>
public static class ClipAudioSource
{
    /// <summary>
    /// Embedded audio if the clip has any; otherwise the 6D Mark II's scratch WAV, lined up
    /// with the first frame when the clip says when it started (v91 on) and passed through
    /// unchanged when it does not. The timecode is the scratch WAV's LTC, when it runs at the
    /// clip's frame rate (a mismatched generator would drift along the clip).
    /// </summary>
    public static (IVirtualFileContent? Audio, ScratchWav? Scratch, Timecode? Start) For(MlvClip clip, string mlvPath, Dng.DngOptions options)
    {
        IVirtualFileContent? audio = EmbeddedWav.Create(clip, options.FrameRateOverride);
        if (audio is not null) return (audio, null, null);
        if (PassThroughFile.FindScratchWav(mlvPath) is not { } wav) return (null, null, null);

        var scratch = ScratchWav.Create(clip, wav, options.FrameRateOverride, options.ScratchLatencyMs);
        if (scratch is null) return (new PassThroughFile(wav), null, null);

        Timecode? start = null;
        if (scratch.StartTimecode is { } tc)
        {
            var (num, den) = clip.FrameRate(options.FrameRateOverride);
            if (Math.Abs(tc.Fps - (double)num / den) < 0.001 * tc.Fps) start = tc;
        }
        return (scratch, scratch, start);
    }
}
