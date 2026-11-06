// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;
using MlvDrive.Core.Audio;

namespace MlvDrive.Core.Testing;

/// <summary>Synthetic LTC and scratch WAVs for tests (what a Tentacle in the mic jack records).</summary>
public static class LtcSignal
{
    /// <summary>
    /// <paramref name="count"/> samples of LTC whose word for <paramref name="start"/> begins at
    /// sample <paramref name="firstWordAt"/> (earlier words fill in before it).
    /// <paramref name="recorderPpm"/> makes the recording's clock run fast (positive) against the generator's.
    /// </summary>
    public static short[] Generate(int count, double firstWordAt, Timecode start, int sampleRate = 48000, short amplitude = 9000,
                                   double recorderPpm = 0)
    {
        var output = new short[count];
        double samplesPerFrame = (double)sampleRate * start.RateDen / start.RateNum * (1 + recorderPpm * 1e-6);
        double cell = samplesPerFrame / 80;
        short level = amplitude;
        for (long k = -(long)(firstWordAt / samplesPerFrame) - 1; ; k++)
        {
            double w0 = firstWordAt + k * samplesPerFrame;
            if (w0 >= count) break;
            var bits = Bits(start.Plus(k));
            for (int i = 0; i < 80; i++)
            {
                double t0 = w0 + i * cell;
                level = (short)-level;                                   // every bit boundary
                Fill(output, t0, t0 + cell / 2, level);
                if (bits[i] == 1) level = (short)-level;                 // and mid-cell for a 1
                Fill(output, t0 + cell / 2, t0 + cell, level);
            }
        }
        return output;
    }

    static void Fill(short[] o, double from, double to, short v)
    {
        for (long x = Math.Max(0, (long)Math.Round(from)); x < Math.Min(o.Length, (long)Math.Round(to)); x++) o[x] = v;
    }

    static int[] Bits(Timecode tc)
    {
        var b = new int[80];
        if (tc.Frames < 0) return b;
        var (h, m, s, f) = tc.Fields;
        void Put(int lo, int n, int v) { for (int j = 0; j < n; j++) b[lo + j] = (v >> j) & 1; }
        Put(0, 4, f % 10); Put(8, 2, f / 10); b[10] = tc.DropFrame ? 1 : 0;
        Put(16, 4, s % 10); Put(24, 3, s / 10);
        Put(32, 4, m % 10); Put(40, 3, m / 10);
        Put(48, 4, h % 10); Put(56, 2, h / 10);
        int[] sync = { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1 };
        sync.CopyTo(b, 64);
        return b;
    }

    /// <summary>A 16-bit stereo 48 kHz WAV from two channels.</summary>
    public static void WriteStereoWav(string path, short[] left, short[] right, int sampleRate = 48000)
    {
        int n = Math.Min(left.Length, right.Length);
        var b = new byte[44 + n * 4];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(36 + n * 4));
        "WAVEfmt "u8.CopyTo(b.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)sampleRate * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 16);
        "data"u8.CopyTo(b.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)(n * 4));
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(44 + i * 4), left[i]);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(46 + i * 4), right[i]);
        }
        File.WriteAllBytes(path, b);
    }
}
