// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
namespace MlvDrive.Core.Testing;

/// <summary>How a lossless frame's pixels are laid out as a JPEG image.</summary>
public enum Lj92Layout
{
    /// <summary>The camera's: two components (a Bayer row's two colours), half-width JPEG rows, predictor 1.</summary>
    Camera,
    /// <summary>mlv_dump -c: one component, two raw rows per JPEG row, predictor 6.</summary>
    MlvDump,
}

/// <summary>Lossless JPEG (LJ92) encoder for tests: optimal Huffman tables, any predictor, restarts.</summary>
public static class Lj92Writer
{
    /// <summary>A frame of <paramref name="width"/> x <paramref name="height"/> raw pixels in one of Magic Lantern's layouts.</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<ushort> pixels, int width, int height, int bits, Lj92Layout layout) => layout switch
    {
        Lj92Layout.Camera => Encode(pixels, width / 2, height, 2, bits, 1),
        _ => Encode(pixels, width * 2, height / 2, 1, bits, 6),
    };

    /// <summary>
    /// Encode a raster of <paramref name="width"/> x <paramref name="components"/> samples a row.
    /// <paramref name="restartInterval"/> is in MCUs (one sample of each component).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<ushort> raster, int width, int height, int components, int precision, int predictor,
                                int restartInterval = 0, bool tablePerComponent = false, int pointTransform = 0)
    {
        int rowLength = width * components;
        if (raster.Length < rowLength * height) throw new ArgumentException("raster too small");

        // differences, predicted exactly as a decoder will
        var diffs = new int[rowLength * height];
        var previous = new int[rowLength];
        var current = new int[rowLength];
        int initial = 1 << (precision - pointTransform - 1);
        bool firstLine = true;
        int lineStart = 0, mcus = 0;
        var restartAt = new List<int>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (restartInterval > 0 && mcus > 0 && mcus % restartInterval == 0)
                {
                    restartAt.Add(y * rowLength + x * components);
                    firstLine = true;
                    lineStart = x;
                }
                mcus++;
                for (int k = 0, i = x * components; k < components; k++, i++)
                {
                    int px;
                    if (firstLine) px = x == lineStart ? initial : current[i - components];
                    else if (x == 0) px = previous[i];
                    else
                    {
                        int a = current[i - components], b = previous[i], d = previous[i - components];
                        px = predictor switch
                        {
                            1 => a, 2 => b, 3 => d, 4 => a + b - d,
                            5 => a + ((b - d) >> 1), 6 => b + ((a - d) >> 1), _ => (a + b) >> 1,
                        };
                    }
                    int v = raster[y * rowLength + i] >> pointTransform;
                    current[i] = v;
                    int diff = (v - px) & 0xFFFF;
                    diffs[y * rowLength + i] = diff > 32768 ? diff - 65536 : diff;
                }
            }
            (previous, current) = (current, previous);
            firstLine = false;
            lineStart = 0;
        }

        int tableCount = tablePerComponent ? components : 1;
        var freq = new long[tableCount, 17];
        for (int i = 0; i < diffs.Length; i++) freq[tablePerComponent ? i % components : 0, Category(diffs[i])]++;
        var tables = new (byte[] Bits, byte[] Values, int[] Code, int[] Size)[tableCount];
        for (int t = 0; t < tableCount; t++)
        {
            var f = new long[17];
            for (int s = 0; s <= 16; s++) f[s] = freq[t, s];
            tables[t] = BuildTable(f);
        }

        var o = new List<byte> { 0xFF, 0xD8 };
        for (int t = 0; t < tableCount; t++)
        {
            Segment(o, 0xC4, s =>
            {
                s.Add((byte)t);
                s.AddRange(tables[t].Bits);
                s.AddRange(tables[t].Values);
            });
        }
        Segment(o, 0xC3, s =>
        {
            s.Add((byte)precision);
            s.Add((byte)(height >> 8)); s.Add((byte)height);
            s.Add((byte)(width >> 8)); s.Add((byte)width);
            s.Add((byte)components);
            for (int k = 0; k < components; k++) { s.Add((byte)(k + 1)); s.Add(0x11); s.Add(0); }
        });
        if (restartInterval > 0) Segment(o, 0xDD, s => { s.Add((byte)(restartInterval >> 8)); s.Add((byte)restartInterval); });
        Segment(o, 0xDA, s =>
        {
            s.Add((byte)components);
            for (int k = 0; k < components; k++) { s.Add((byte)(k + 1)); s.Add((byte)((tablePerComponent ? k : 0) << 4)); }
            s.Add((byte)predictor); s.Add(0); s.Add((byte)pointTransform);
        });

        var bitsOut = new BitWriter(o);
        int next = 0, marker = 0;
        for (int i = 0; i < diffs.Length; i++)
        {
            if (next < restartAt.Count && restartAt[next] == i)
            {
                bitsOut.Flush();
                o.Add(0xFF); o.Add((byte)(0xD0 + marker++ % 8));
                next++;
            }
            var (_, _, code, size) = tables[tablePerComponent ? i % components : 0];
            int d = diffs[i], ssss = Category(d);
            bitsOut.Write(code[ssss], size[ssss]);
            if (ssss is > 0 and < 16) bitsOut.Write(d > 0 ? d : d + (1 << ssss) - 1, ssss);
        }
        bitsOut.Flush();
        o.Add(0xFF); o.Add(0xD9);
        return o.ToArray();
    }

    static int Category(int d) => d == 0 ? 0 : d == 32768 ? 16 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(d));

    static void Segment(List<byte> o, byte marker, Action<List<byte>> body)
    {
        var s = new List<byte>();
        body(s);
        o.Add(0xFF); o.Add(marker);
        o.Add((byte)((s.Count + 2) >> 8)); o.Add((byte)(s.Count + 2));
        o.AddRange(s);
    }

    /// <summary>Optimal code lengths limited to 16 bits (T.81 Annex K.2), then canonical codes (Annex C).</summary>
    static (byte[] Bits, byte[] Values, int[] Code, int[] Size) BuildTable(long[] symbolFreq)
    {
        var freq = new long[257];
        for (int s = 0; s <= 16; s++) freq[s] = symbolFreq[s];
        freq[256] = 1;                                   // reserved: keeps the all-ones code unused
        var codesize = new int[257];
        var others = Enumerable.Repeat(-1, 257).ToArray();
        while (true)
        {
            int v1 = -1, v2 = -1;
            for (int i = 0; i < 257; i++)
                if (freq[i] > 0 && (v1 < 0 || freq[i] <= freq[v1])) v1 = i;
            for (int i = 0; i < 257; i++)
                if (freq[i] > 0 && i != v1 && (v2 < 0 || freq[i] <= freq[v2])) v2 = i;
            if (v2 < 0) break;
            freq[v1] += freq[v2];
            freq[v2] = 0;
            codesize[v1]++;
            while (others[v1] >= 0) { v1 = others[v1]; codesize[v1]++; }
            others[v1] = v2;
            codesize[v2]++;
            while (others[v2] >= 0) { v2 = others[v2]; codesize[v2]++; }
        }
        var bits = new int[33];
        for (int i = 0; i < 257; i++) if (codesize[i] > 0) bits[codesize[i]]++;
        for (int i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2; bits[i - 1]++; bits[j + 1] += 2; bits[j]--;
            }
        }
        int top = 16;
        while (bits[top] == 0) top--;
        bits[top]--;                                     // drop the reserved symbol

        var values = new List<byte>();
        for (int len = 1; len <= 32; len++)
            for (int s = 0; s <= 16; s++)
                if (codesize[s] == len) values.Add((byte)s);
        var bitsOut = new byte[16];
        for (int len = 1; len <= 16; len++) bitsOut[len - 1] = (byte)bits[len];

        var code = new int[17];
        var size = new int[17];
        int c = 0, k = 0;
        for (int len = 1; len <= 16; len++, c <<= 1)
            for (int n = 0; n < bits[len]; n++, k++, c++)
            {
                code[values[k]] = c;
                size[values[k]] = len;
            }
        return (bitsOut, values.ToArray(), code, size);
    }

    sealed class BitWriter
    {
        readonly List<byte> _o;
        int _acc, _n;
        public BitWriter(List<byte> o) => _o = o;

        public void Write(int value, int count)
        {
            for (int b = count - 1; b >= 0; b--)
            {
                _acc = _acc << 1 | (value >> b & 1);
                if (++_n == 8) Emit();
            }
        }

        /// <summary>Pad the last byte with ones (as T.81 asks before a marker).</summary>
        public void Flush()
        {
            while (_n != 0) { _acc = _acc << 1 | 1; if (++_n == 8) Emit(); }
        }

        void Emit()
        {
            byte b = (byte)_acc;
            _o.Add(b);
            if (b == 0xFF) _o.Add(0x00);
            _acc = 0; _n = 0;
        }
    }
}
