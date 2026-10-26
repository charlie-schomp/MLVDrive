using System.Buffers.Binary;
using System.Text;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.Testing;

/// <summary>Writes small synthetic MLV clips with known pixels, for tests and for checking an editor imports them.</summary>
public sealed class SyntheticClip
{
    public int Width = 64, Height = 32, Bpp = 12, Black = 512, White = 4050;
    public string Camera = "Canon EOS 6D Mark II";
    public ushort VideoClass = 1;
    public uint FpsNom = 23976, FpsDen = 1000;
    public uint WbMode = 9, Kelvin = 5600;
    public int FrameSpace = 16;                 // alignment padding before each frame's pixels
    public bool WithAudio;
    public int AudioBytesPerPacket = 4800;      // 25 ms of 48 kHz stereo 16-bit
    /// <summary>The 6D Mark II port's INFO "scratch_wav_start_us=N" (v91 on); null writes none.</summary>
    public long? ScratchWavStartUs;
    /// <summary>Write lossless (LJ92) frames in this layout instead of packed pixels; null for uncompressed.</summary>
    public Lj92Layout? Lossless;

    /// <summary>Optional image: (frame, x, y) to a raw value. Default is a deterministic test pattern.</summary>
    public Func<int, int, int, ushort>? PixelFunction;

    /// <summary>Deterministic pixel value for a frame and position.</summary>
    public ushort Pixel(int frame, int x, int y) => PixelFunction is { } f ? f(frame, x, y) :
        (ushort)((Black + (x * 37 + y * 101 + frame * 13 + (x ^ y) * 7) % (White - Black)) & ((1 << Bpp) - 1));

    /// <summary>
    /// SMPTE-style colour bars on an RGGB sensor, with a white square moving across, so an
    /// editor's debayer shows recognisable colours and motion.
    /// </summary>
    public void UseColourBars()
    {
        // linear sensor levels (fraction of black..white) for white, yellow, cyan, green, magenta, red, blue
        // in camera RGB roughly balanced for daylight (green is the most sensitive channel)
        (double R, double G, double B)[] bars =
        {
            (0.36, 0.72, 0.48), (0.36, 0.72, 0.03), (0.02, 0.72, 0.48), (0.02, 0.72, 0.03),
            (0.36, 0.05, 0.48), (0.36, 0.05, 0.03), (0.02, 0.05, 0.48),
        };
        PixelFunction = (frame, x, y) =>
        {
            int bar = Math.Min(bars.Length - 1, x * bars.Length / Width);
            var c = bars[bar];
            int square = Height / 4, sx = frame * 8 % Math.Max(1, Width - square), sy = Height / 2 - square / 2;
            if (x >= sx && x < sx + square && y >= sy && y < sy + square) c = (0.36, 0.72, 0.48);
            if (y > Height * 3 / 4) c = (c.R * x / Width, c.G * x / Width, c.B * x / Width);   // ramp strip
            double level = (y & 1) == 0 ? ((x & 1) == 0 ? c.R : c.G) : ((x & 1) == 0 ? c.G : c.B);
            return (ushort)(Black + level * (White - Black));
        };
    }

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
                if (ScratchWavStartUs is { } us)
                {
                    var text = new byte[48];
                    Encoding.ASCII.GetBytes($"scratch_wav_start_us={us}").CopyTo(text, 0);
                    Block(s, "INFO", text);
                }
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
        var px = Lossless is { } layout
            ? Lj92Writer.EncodeFrame(FramePixels(frameNumber), Width, Height, Bpp, layout)
            : PixelUnpacker.Pack(FramePixels(frameNumber), Bpp);
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
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), (ushort)(VideoClass | (Lossless is null ? 0 : MlvFormat.VideoClassFlagLj92)));
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

