using System.Buffers.Binary;
using System.Text;
using MlvDrive.Core.Dng;

namespace MlvDrive.Tests;

/// <summary>Writes small synthetic MLV clips with known pixels, for tests.</summary>
public sealed class TestMlv
{
    public int Width = 64, Height = 32, Bpp = 12, Black = 512, White = 4050;
    public string Camera = "Canon EOS 6D Mark II";
    public ushort VideoClass = 1;
    public uint FpsNom = 23976, FpsDen = 1000;
    public uint WbMode = 9, Kelvin = 5600;
    public int FrameSpace = 16;                 // alignment padding before each frame's pixels
    public bool WithAudio;
    public int AudioBytesPerPacket = 4800;      // 25 ms of 48 kHz stereo 16-bit

    /// <summary>Deterministic pixel value for a frame and position.</summary>
    public ushort Pixel(int frame, int x, int y) =>
        (ushort)((Black + (x * 37 + y * 101 + frame * 13 + (x ^ y) * 7) % (White - Black)) & ((1 << Bpp) - 1));

    public ushort[] FramePixels(int frame)
    {
        var p = new ushort[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                p[y * Width + x] = Pixel(frame, x, y);
        return p;
    }

    /// <summary>
    /// Write the clip: <paramref name="framesPerFile"/> gives how many frames go in
    /// NAME.MLV, NAME.M00, ...; <paramref name="order"/> optionally reorders frame numbers.
    /// </summary>
    public string Write(string dir, string name, int[] framesPerFile, int[]? order = null)
    {
        Directory.CreateDirectory(dir);
        int total = framesPerFile.Sum();
        order ??= Enumerable.Range(0, total).ToArray();
        int next = 0;
        string main = Path.Combine(dir, name + ".MLV");
        for (int fi = 0; fi < framesPerFile.Length; fi++)
        {
            string path = fi == 0 ? main : Path.Combine(dir, $"{name}.M{fi - 1:00}");
            using var s = new MemoryStream();
            Block(s, "MLVI", Mlvi((ushort)fi, (ushort)framesPerFile.Length, (uint)total), withTimestamp: false);
            if (fi == 0)
            {
                Block(s, "RAWI", Rawi());
                Block(s, "IDNT", Idnt());
                Block(s, "EXPO", Expo());
                Block(s, "LENS", Lens());
                Block(s, "RTCI", Rtci());
                Block(s, "WBAL", Wbal());
                if (WithAudio) Block(s, "WAVI", Wavi());
            }
            for (int k = 0; k < framesPerFile[fi]; k++, next++)
            {
                int frameNumber = order[next];
                ulong ts = 1_000_000UL + (ulong)(frameNumber * 1_000_000L * FpsDen / FpsNom);
                Vidf(s, frameNumber, ts);
                if (WithAudio) Audf(s, frameNumber, ts);
            }
            File.WriteAllBytes(path, s.ToArray());
        }
        return main;
    }

    void Vidf(Stream s, int frameNumber, ulong ts)
    {
        var px = PixelUnpacker.Pack(FramePixels(frameNumber), Bpp);
        var body = new byte[16 + FrameSpace + px.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)frameNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)FrameSpace);
        px.CopyTo(body, 16 + FrameSpace);
        Block(s, "VIDF", body, ts);
    }

    /// <summary>Audio sample value for byte position in the clip's audio stream.</summary>
    public static byte AudioByte(long position) => (byte)(position * 7 + 3);

    void Audf(Stream s, int frameNumber, ulong ts)
    {
        // One packet per frame, stamped at the frame's time; payload continues a known byte pattern.
        var body = new byte[8 + AudioBytesPerPacket];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)frameNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0);
        long start = (long)((ts - 1_000_000UL) * 192000UL / 1_000_000UL);
        start -= start % 4;
        for (int i = 0; i < AudioBytesPerPacket; i++) body[8 + i] = AudioByte(start + i);
        Block(s, "AUDF", body, ts);
    }

    static void Block(Stream s, string type, byte[] body, ulong ts = 0, bool withTimestamp = true)
    {
        var h = new byte[16];
        Encoding.ASCII.GetBytes(type).CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(16 + body.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), withTimestamp ? ts : 0x0030762E32L);
        if (!withTimestamp) Encoding.ASCII.GetBytes("v2.0\0\0\0\0").CopyTo(h, 8);
        s.Write(h);
        s.Write(body);
    }

    byte[] Mlvi(ushort fileNum, ushort fileCount, uint frames)
    {
        var b = new byte[36];
        BinaryPrimitives.WriteUInt64LittleEndian(b, 0x1234_5678_9ABC_DEF0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), fileNum);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), fileCount);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), VideoClass);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), (ushort)(WithAudio ? 1 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), frames);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), FpsNom);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), FpsDen);
        return b;
    }

    byte[] Rawi()
    {
        var b = new byte[4 + 160];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)Height);
        void I(int index, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4 + index * 4), v);
        I(0, 1); I(2, 1208); I(3, 2128); I(4, 2128 * Bpp / 8); I(6, Bpp); I(7, Black); I(8, White);
        I(13, 28); I(14, 44); I(15, 1208); I(16, 2124);      // camera buffer active area (bigger than the frame)
        I(17, 0); I(18, 0); I(19, 0x02010100); I(20, 21);
        return b;
    }

    byte[] Idnt()
    {
        var b = new byte[68];
        Encoding.ASCII.GetBytes(Camera).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 0x80000406);
        Encoding.ASCII.GetBytes("123456789").CopyTo(b, 36);
        return b;
    }

    static byte[] Expo()
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 400);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 20833);
        return b;
    }

    static byte[] Lens()
    {
        var b = new byte[80];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 50);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 3000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 280);
        Encoding.ASCII.GetBytes("EF50mm f/1.8 STM").CopyTo(b, 16);
        return b;
    }

    static byte[] Rtci()
    {
        var b = new byte[28];
        ushort[] v = { 5, 51, 16, 8, 9, 126 };   // 16:51:05, 8 Oct 2026
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), v[i]);
        return b;
    }

    byte[] Wbal()
    {
        var b = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(b, WbMode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Kelvin);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1536);
        return b;
    }

    static byte[] Wavi()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 192000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), 16);
        return b;
    }
}

/// <summary>Minimal TIFF reader for checking generated DNG headers.</summary>
public sealed class TiffReader
{
    public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);
    public Dictionary<ushort, Entry> Ifd0 { get; } = new();
    public Dictionary<ushort, Entry> Exif { get; } = new();
    public List<ushort> Ifd0Order { get; } = new();

    public TiffReader(byte[] file)
    {
        if (file[0] != 'I' || file[1] != 'I' || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) != 42)
            throw new InvalidDataException("not a little-endian TIFF");
        ReadIfd(file, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)), Ifd0, Ifd0Order);
        if (Ifd0.TryGetValue(34665, out var exif))
            ReadIfd(file, BinaryPrimitives.ReadUInt32LittleEndian(exif.Value), Exif, new List<ushort>());
    }

    static readonly int[] TypeSize = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    static void ReadIfd(byte[] f, uint at, Dictionary<ushort, Entry> into, List<ushort> order)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan((int)at));
        for (int i = 0; i < n; i++)
        {
            int p = (int)at + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p + 2));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(p + 4));
            int size = TypeSize[type] * (int)count;
            byte[] value = size <= 4
                ? f.AsSpan(p + 8, size).ToArray()
                : f.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(p + 8)), size).ToArray();
            into[tag] = new Entry(tag, type, count, value);
            order.Add(tag);
        }
    }

    public uint Long(ushort tag) => Ifd0[tag].Type == 3
        ? BinaryPrimitives.ReadUInt16LittleEndian(Ifd0[tag].Value)
        : BinaryPrimitives.ReadUInt32LittleEndian(Ifd0[tag].Value);

    public string Ascii(ushort tag, bool exif = false) =>
        Encoding.ASCII.GetString((exif ? Exif : Ifd0)[tag].Value).TrimEnd('\0');

    public double[] Rationals(ushort tag)
    {
        var e = Ifd0[tag];
        var r = new double[e.Count];
        for (int i = 0; i < r.Length; i++)
        {
            long num = e.Type == 10 ? BinaryPrimitives.ReadInt32LittleEndian(e.Value.AsSpan(i * 8)) : BinaryPrimitives.ReadUInt32LittleEndian(e.Value.AsSpan(i * 8));
            long den = e.Type == 10 ? BinaryPrimitives.ReadInt32LittleEndian(e.Value.AsSpan(i * 8 + 4)) : BinaryPrimitives.ReadUInt32LittleEndian(e.Value.AsSpan(i * 8 + 4));
            r[i] = (double)num / den;
        }
        return r;
    }
}
