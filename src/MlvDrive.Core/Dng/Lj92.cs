using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MlvDrive.Core.Dng;

/// <summary>
/// Lossless JPEG (ITU T.81 process 14, "LJ92"): Magic Lantern's lossless raw video. In the
/// camera it is Canon's CR2 encoder, set up so a frame is two full-width slices stacked top
/// and bottom, which leaves the decoded samples in plain raster order; mlv_dump's
/// re-compression (-c) codes the same raster as one component with two raw rows per JPEG
/// row. Either way the samples come out as the frame's pixels, row by row.
///
/// Any predictor 1-7, interleaved components with their own tables, restart intervals and
/// a point transform are handled; hierarchical, multi-scan and arithmetic-coded JPEG are
/// not (no camera writes them). Damaged data only ever raises InvalidDataException.
/// </summary>
public static class Lj92
{
    /// <summary>What a frame holds: <see cref="Width"/> x <see cref="Components"/> samples a row.</summary>
    public readonly record struct FrameInfo(int Width, int Height, int Components, int Precision, int Predictor,
                                            int PointTransform, int RestartInterval)
    {
        public long Samples => (long)Width * Components * Height;
    }

    /// <summary>The frame header of a lossless JPEG stream (reads only as far as the scan header).</summary>
    public static FrameInfo ReadInfo(ReadOnlySpan<byte> data) => Parse(data).Info;

    /// <summary>
    /// Decode a lossless JPEG stream into <paramref name="output"/>, in raster order. On damaged
    /// data it throws InvalidDataException with what came before the damage already written.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, Span<ushort> output)
    {
        var (info, tables, dataStart) = Parse(data);
        if (output.Length < info.Samples)
            throw Bad($"frame is {info.Width}x{info.Height}x{info.Components}, larger than the {output.Length}-sample buffer");
        var restarts = new List<int>();
        var entropy = Unstuff(data[dataStart..], restarts, out int length);
        DecodeScan(info, tables, entropy, length, restarts, output);
    }

    static InvalidDataException Bad(string why) => new($"lossless JPEG: {why}");

    static (FrameInfo Info, HuffmanTable[] Tables, int DataStart) Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw Bad("no start-of-image marker");
        var tables = new HuffmanTable?[4];
        int width = 0, height = 0, precision = 0, components = 0, restart = 0;
        Span<byte> ids = stackalloc byte[4];
        int pos = 2;
        while (true)
        {
            if (pos >= d.Length || d[pos] != 0xFF) throw Bad("expected a marker");
            while (pos < d.Length && d[pos] == 0xFF) pos++;               // fill bytes
            if (pos >= d.Length) throw Bad("ends inside a marker");
            int marker = d[pos++];
            if (marker == 0xD9) throw Bad("no scan before end-of-image");
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;          // markers with no segment
            if (pos + 2 > d.Length) throw Bad("ends inside a marker");
            int len = BinaryPrimitives.ReadUInt16BigEndian(d[pos..]);
            if (len < 2 || pos + len > d.Length) throw Bad("a segment runs past the end");
            var seg = d.Slice(pos + 2, len - 2);

            switch (marker)
            {
                case 0xC4:
                    ParseHuffman(seg, tables);
                    break;
                case 0xC3:
                    if (seg.Length < 6) throw Bad("short frame header");
                    precision = seg[0];
                    height = BinaryPrimitives.ReadUInt16BigEndian(seg[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(seg[3..]);
                    components = seg[5];
                    if (precision is < 2 or > 16) throw Bad($"{precision}-bit samples");
                    if (width == 0 || height == 0) throw Bad("no image size in the frame header");
                    if (components is < 1 or > 4 || seg.Length < 6 + 3 * components) throw Bad($"{components} components");
                    for (int i = 0; i < components; i++)
                    {
                        ids[i] = seg[6 + 3 * i];
                        if (seg[7 + 3 * i] != 0x11) throw Bad("subsampled components");
                    }
                    break;
                case >= 0xC0 and <= 0xCF and not 0xC8 and not 0xCC:
                    throw Bad($"not lossless (frame type FF{marker:X2})");
                case 0xDD:
                    if (seg.Length < 2) throw Bad("short restart interval");
                    restart = BinaryPrimitives.ReadUInt16BigEndian(seg);
                    break;
                case 0xDA:
                {
                    if (components == 0) throw Bad("scan before the frame header");
                    int ns = seg.Length > 0 ? seg[0] : 0;
                    if (ns != components) throw Bad("components in separate scans");
                    if (seg.Length < 4 + 2 * ns) throw Bad("short scan header");
                    var scanTables = new HuffmanTable[ns];
                    for (int j = 0; j < ns; j++)
                    {
                        if (seg[1 + 2 * j] != ids[j]) throw Bad("scan components out of frame order");
                        int td = seg[2 + 2 * j] >> 4;
                        scanTables[j] = (td < 4 ? tables[td] : null) ?? throw Bad($"no Huffman table {td}");
                    }
                    int predictor = seg[1 + 2 * ns];
                    int pt = seg[3 + 2 * ns] & 15;
                    if (predictor is < 1 or > 7) throw Bad($"predictor {predictor}");
                    if (pt >= precision) throw Bad($"point transform {pt}");
                    return (new FrameInfo(width, height, components, precision, predictor, pt, restart), scanTables, pos + len);
                }
            }
            pos += len;
        }
    }

    static void ParseHuffman(ReadOnlySpan<byte> seg, HuffmanTable?[] tables)
    {
        while (seg.Length > 0)
        {
            if (seg.Length < 17) throw Bad("short Huffman table");
            int tc = seg[0] >> 4, th = seg[0] & 15;
            int total = 0;
            for (int i = 1; i <= 16; i++) total += seg[i];
            if (seg.Length < 17 + total) throw Bad("short Huffman table");
            if (th > 3) throw Bad($"Huffman table {th}");
            if (tc == 0) tables[th] = new HuffmanTable(seg.Slice(1, 16), seg.Slice(17, total));
            seg = seg[(17 + total)..];
        }
    }

    /// <summary>
    /// Prefix code for the difference categories (SSSS 0-16), as one lookup on the next
    /// <see cref="MaxLength"/> bits: entry = code length &lt;&lt; 8 | SSSS, 0 where no code starts.
    /// </summary>
    sealed class HuffmanTable
    {
        public readonly ushort[] Lookup;
        public readonly int MaxLength;

        public HuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            MaxLength = 16;
            while (MaxLength > 0 && counts[MaxLength - 1] == 0) MaxLength--;
            if (MaxLength == 0) throw Bad("empty Huffman table");
            Lookup = new ushort[1 << MaxLength];
            int code = 0, k = 0;
            for (int len = 1; len <= MaxLength; len++)
            {
                for (int n = 0; n < counts[len - 1]; n++, k++, code++)
                {
                    int ssss = values[k];
                    if (ssss > 16) throw Bad($"difference category {ssss}");
                    if (code >= 1 << len) throw Bad("Huffman table has too many codes");
                    int shift = MaxLength - len;
                    Lookup.AsSpan(code << shift, 1 << shift).Fill((ushort)(len << 8 | ssss));
                }
                code <<= 1;
            }
        }
    }

    /// <summary>
    /// The entropy-coded bytes with marker stuffing removed (FF 00 is FF), cut at the end
    /// marker; restart markers are dropped and their positions recorded. Zero padding lets
    /// the reader fetch 8 bytes at a time.
    /// </summary>
    static byte[] Unstuff(ReadOnlySpan<byte> d, List<int> restarts, out int length)
    {
        var output = new byte[d.Length + Padding];
        int o = 0, i = 0;
        restarts.Add(0);
        while (i < d.Length)
        {
            int ff = d[i..].IndexOf((byte)0xFF);
            if (ff < 0) { d[i..].CopyTo(output.AsSpan(o)); o += d.Length - i; break; }
            d.Slice(i, ff).CopyTo(output.AsSpan(o));
            o += ff;
            int j = i + ff + 1;
            while (j < d.Length && d[j] == 0xFF) j++;                      // fill bytes
            if (j >= d.Length) break;
            int m = d[j];
            if (m == 0x00) output[o++] = 0xFF;
            else if (m is >= 0xD0 and <= 0xD7) restarts.Add(o);
            else break;                                                   // end of image or any other marker
            i = j + 1;
        }
        length = o;
        return output;
    }

    const int Padding = 32;

    /// <summary>
    /// MSB-first bit reader over unstuffed data. Past its end it reads zeros, and fails once a
    /// whole 64 bits of them would have been used (a truncated frame).
    /// </summary>
    struct BitReader
    {
        public byte[] Data;
        public int Pos, End, Bits;
        public ulong Buffer;

        public BitReader(byte[] data, int start, int end)
        {
            Data = data; Pos = start; End = end; Bits = 0; Buffer = 0;
        }

        /// <summary>Top the buffer up to at least 57 bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Fill(byte[] data, ref int pos, int end, ref int bits, ref ulong buffer)
        {
            if (pos > end + 16) throw Bad("data ends early");
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos, 8));
            if (pos + 8 > end) next &= pos >= end ? 0 : ~0UL << (8 * (pos + 8 - end));
            buffer |= next >> bits;
            int bytes = (63 - bits) >> 3;
            pos += bytes;
            bits += bytes << 3;
        }

        /// <summary>One difference: its Huffman-coded category, then that many bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Difference(byte[] data, ref int pos, int end, ref int bits, ref ulong buffer, ushort[] lookup, int shift)
        {
            if (bits < 32) Fill(data, ref pos, end, ref bits, ref buffer);
            int e = lookup[(int)(buffer >> shift)];
            if (e == 0) throw Bad("invalid Huffman code");
            int len = e >> 8, ssss = e & 0xFF;
            buffer <<= len;
            bits -= len;
            if (ssss == 0) return 0;
            if (ssss == 16) return 32768;
            int v = (int)(buffer >> (64 - ssss));
            buffer <<= ssss;
            bits -= ssss;
            return v < 1 << (ssss - 1) ? v - (1 << ssss) + 1 : v;
        }

        public int Difference(HuffmanTable t) => Difference(Data, ref Pos, End, ref Bits, ref Buffer, t.Lookup, 64 - t.MaxLength);
    }

    static void DecodeScan(FrameInfo info, HuffmanTable[] tables, byte[] data, int length, List<int> restarts, Span<ushort> output)
    {
        int w = info.Width, c = info.Components, pt = info.PointTransform, predictor = info.Predictor;
        int rowLength = w * c;
        var previous = new int[rowLength];
        var current = new int[rowLength];
        int initial = 1 << (info.Precision - pt - 1);
        int interval = info.RestartInterval;
        long mcusLeft = interval > 0 ? interval : long.MaxValue;
        int segment = 0;
        var reader = new BitReader(data, restarts[0], restarts.Count > 1 ? restarts[1] : length);
        bool oneTable = tables.All(t => t == tables[0]);

        // the first line, and the rest of the line after a restart, predict from the left
        // only; the first sample there starts from mid-scale
        bool firstLine = true;
        for (int y = 0; y < info.Height; y++)
        {
            var row = output.Slice(y * rowLength, rowLength);
            if (!firstLine && mcusLeft >= w && oneTable)
            {
                DecodeRow(ref reader, tables[0], predictor, c, previous, current, row, pt);
                mcusLeft -= w;
            }
            else
            {
                int lineStart = 0;
                for (int x = 0; x < w; x++)
                {
                    if (mcusLeft-- == 0)
                    {
                        if (++segment >= restarts.Count) throw Bad("missing restart marker");
                        reader = new BitReader(data, restarts[segment], segment + 1 < restarts.Count ? restarts[segment + 1] : length);
                        mcusLeft = interval - 1;
                        firstLine = true;
                        lineStart = x;
                    }
                    int i = x * c;
                    for (int k = 0; k < c; k++, i++)
                    {
                        int px = firstLine ? (x == lineStart ? initial : current[i - c])
                               : x == 0 ? previous[i]
                               : Predict(predictor, current[i - c], previous[i], previous[i - c]);
                        int v = (px + reader.Difference(tables[k])) & 0xFFFF;
                        current[i] = v;
                        row[i] = (ushort)(v << pt);
                    }
                }
            }
            (previous, current) = (current, previous);
            firstLine = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Predict(int predictor, int a, int b, int d) => predictor switch
    {
        1 => a,
        2 => b,
        3 => d,
        4 => a + b - d,
        5 => a + ((b - d) >> 1),
        6 => b + ((a - d) >> 1),
        _ => (a + b) >> 1,
    };

    /// <summary>A whole row with no restart in it and one table: the hot loop, state in locals.</summary>
    static void DecodeRow(ref BitReader reader, HuffmanTable table, int predictor, int c, int[] previous, int[] current, Span<ushort> row, int pt)
    {
        byte[] data = reader.Data;
        int pos = reader.Pos, end = reader.End, bits = reader.Bits;
        ulong buffer = reader.Buffer;
        ushort[] lookup = table.Lookup;
        int shift = 64 - table.MaxLength;
        int n = current.Length;

        for (int k = 0; k < c; k++)                                         // first column: from above
        {
            int v = (previous[k] + BitReader.Difference(data, ref pos, end, ref bits, ref buffer, lookup, shift)) & 0xFFFF;
            current[k] = v;
            row[k] = (ushort)(v << pt);
        }
        if (predictor == 1)
        {
            for (int i = c; i < n; i++)
            {
                int v = (current[i - c] + BitReader.Difference(data, ref pos, end, ref bits, ref buffer, lookup, shift)) & 0xFFFF;
                current[i] = v;
                row[i] = (ushort)(v << pt);
            }
        }
        else
        {
            for (int i = c; i < n; i++)
            {
                int px = Predict(predictor, current[i - c], previous[i], previous[i - c]);
                int v = (px + BitReader.Difference(data, ref pos, end, ref bits, ref buffer, lookup, shift)) & 0xFFFF;
                current[i] = v;
                row[i] = (ushort)(v << pt);
            }
        }
        reader.Pos = pos;
        reader.Bits = bits;
        reader.Buffer = buffer;
    }
}
