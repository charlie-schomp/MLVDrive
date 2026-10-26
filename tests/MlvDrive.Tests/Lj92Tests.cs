using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.Testing;
using MlvDrive.Core.VirtualFs;

namespace MlvDrive.Tests;

/// <summary>Lossless (LJ92) clips: the decoder, and clips that read exactly like uncompressed ones.</summary>
public class Lj92Tests
{
    /// <summary>Sensor-like samples: gradients, texture, hard edges and the extremes of the range.</summary>
    static ushort[] Image(int samples, int rowLength, int bits, int seed)
    {
        var rng = new Random(seed);
        int max = (1 << bits) - 1;
        var a = new ushort[samples];
        for (int i = 0; i < samples; i++)
        {
            int x = i % rowLength, y = i / rowLength;
            double v = max * (0.3 + 0.2 * Math.Sin(x * 0.07) * Math.Cos(y * 0.11)) + rng.Next(-40, 41);
            if ((x / 17 + y / 9) % 5 == 0) v = max * 0.9;                     // edges
            if (i % 97 == 0) v = 0;
            if (i % 89 == 0) v = max;
            a[i] = (ushort)Math.Clamp((int)v, 0, max);
        }
        return a;
    }

    static ushort[] RoundTrip(ushort[] image, int width, int height, int components, int bits, int predictor,
                              int restart = 0, bool perComponent = false, int pt = 0)
    {
        var jpeg = Lj92Writer.Encode(image, width, height, components, bits, predictor, restart, perComponent, pt);
        var info = Lj92.ReadInfo(jpeg);
        Assert.Equal((width, height, components, bits, predictor), (info.Width, info.Height, info.Components, info.Precision, info.Predictor));
        var output = new ushort[image.Length];
        Lj92.Decode(jpeg, output);
        return output;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Every_predictor_component_count_and_depth_round_trips(int predictor)
    {
        foreach (int components in new[] { 1, 2, 4 })
            foreach (int bits in new[] { 10, 12, 14, 16 })
            {
                int width = 37, height = 23;
                var image = Image(width * components * height, width * components, bits, predictor * 100 + components * 10 + bits);
                Assert.Equal(image, RoundTrip(image, width, height, components, bits, predictor));
            }
    }

    [Fact]
    public void Largest_difference_has_no_extra_bits()
    {
        // 0 and 32768 alternating at 16 bits: every difference is +-32768 (category 16)
        var image = new ushort[64 * 8];
        for (int i = 0; i < image.Length; i++) image[i] = (ushort)((i & 1) * 32768);
        Assert.Equal(image, RoundTrip(image, 64, 8, 1, 16, 1));
    }

    [Fact]
    public void Restarts_stuffing_separate_tables_and_point_transform()
    {
        int width = 40, height = 18;
        var image = Image(width * 2 * height, width * 2, 14, 7);
        var jpeg = Lj92Writer.Encode(image, width, height, 2, 14, 1);
        Assert.True(jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0x00 }) > 0, "fixture should exercise byte stuffing");

        Assert.Equal(image, RoundTrip(image, width, height, 2, 14, 6, restart: width));           // a restart every row
        Assert.Equal(image, RoundTrip(image, width, height, 2, 14, 4, restart: 13));              // mid-row restarts
        Assert.Equal(image, RoundTrip(image, width, height, 2, 14, 7, perComponent: true));

        var coarse = image.Select(v => (ushort)(v & ~3)).ToArray();                              // 2 bits dropped
        Assert.Equal(coarse, RoundTrip(coarse, width, height, 2, 14, 1, pt: 2));
    }

    [Theory]
    [InlineData(Lj92Layout.Camera)]
    [InlineData(Lj92Layout.MlvDump)]
    public void Magic_Lantern_layouts_decode_to_the_raw_frame(Lj92Layout layout)
    {
        int width = 96, height = 40;
        var pixels = Image(width * height, width, 14, 3);
        var jpeg = Lj92Writer.EncodeFrame(pixels, width, height, 14, layout);
        Assert.Equal((long)width * height, Lj92.ReadInfo(jpeg).Samples);
        var output = new ushort[width * height];
        Lj92.Decode(jpeg, output);
        Assert.Equal(pixels, output);
    }

    [Fact]
    public void Damaged_data_only_ever_raises_invalid_data()
    {
        var pixels = Image(64 * 32, 64, 14, 11);
        var good = Lj92Writer.EncodeFrame(pixels, 64, 32, 14, Lj92Layout.Camera);
        var rng = new Random(5);
        var output = new ushort[pixels.Length];
        for (int trial = 0; trial < 2000; trial++)
        {
            var bad = (byte[])good.Clone();
            if (trial % 3 == 0) bad = bad[..rng.Next(bad.Length)];                                // truncated
            else for (int k = 0; k < 1 + trial % 4; k++) bad[rng.Next(bad.Length)] ^= (byte)rng.Next(1, 256);
            try { Lj92.Decode(bad, output); }
            catch (InvalidDataException) { }
        }
        Assert.Throws<InvalidDataException>(() => Lj92.Decode(good, new ushort[10]));
    }

    static byte[] Dng(DngSequence seq, int frame, int chunk)
    {
        var all = new byte[seq.FileSize];
        for (long pos = 1; pos < all.Length; pos += chunk)                                       // odd offsets on purpose
            seq.Read(frame, pos, all.AsSpan((int)pos, (int)Math.Min(chunk, all.Length - pos)));
        seq.Read(frame, 0, all.AsSpan(0, 1));
        return all;
    }

    [Theory]
    [InlineData(Lj92Layout.Camera)]
    [InlineData(Lj92Layout.MlvDump)]
    public void Lossless_clip_gives_the_same_dngs_as_uncompressed(Lj92Layout layout)
    {
        using var tmp = new TempDir();
        var plain = new SyntheticClip { Width = 128, Height = 48, Bpp = 14, Black = 2047, White = 15000 };
        var lossless = new SyntheticClip { Width = 128, Height = 48, Bpp = 14, Black = 2047, White = 15000, Lossless = layout };
        using var a = MlvClip.Open(plain.Write(Path.Combine(tmp.Path, "a"), "P", new[] { 3, 2 }));
        using var b = MlvClip.Open(lossless.Write(Path.Combine(tmp.Path, "b"), "P", new[] { 3, 2 }));
        Assert.Null(b.Unsupported);
        Assert.True(b.Header.IsLj92);
        Assert.Equal(5, b.Frames.Count);
        var sa = new DngSequence(a);
        var sb = new DngSequence(b);
        for (int f = 0; f < 5; f++) Assert.Equal(Dng(sa, f, 65536), Dng(sb, f, 4093));
    }

    [Fact]
    public void Lossless_frames_read_in_parallel()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip { Width = 160, Height = 64, Bpp = 14, Lossless = Lj92Layout.Camera };
        using var clip = MlvClip.Open(t.Write(tmp.Path, "Q", new[] { 8 }));
        var seq = new DngSequence(clip);
        var expected = Enumerable.Range(0, 8).Select(f => Dng(seq, f, 65536)).ToArray();
        DecodedFrames.Forget(seq);
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            Assert.Equal(expected[i % 8], Dng(seq, i % 8, 3000 + i)));
    }

    [Fact]
    public void Clip_folder_lists_dngs_for_a_lossless_clip()
    {
        using var tmp = new TempDir();
        new SyntheticClip { Bpp = 14, Lossless = Lj92Layout.Camera }.Write(tmp.Path, "LJ", new[] { 2 });
        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("D", tmp.Path) }, cache);
        var names = ((VDirectory)VirtualPath.Resolve(root, @"D\LJ")!).Children().Select(c => c.Name).ToList();
        Assert.Equal(new[] { "LJ_000000.dng", "LJ_000001.dng" }, names);
    }

    [Fact]
    public void Frames_that_do_not_match_the_clip_size_are_refused()
    {
        using var tmp = new TempDir();
        // a camera-layout stream for a 64-wide frame inside a clip that says 66
        var t = new SyntheticClip { Width = 66, Height = 32, Bpp = 14, Lossless = Lj92Layout.Camera };
        t.PixelFunction = (_, _, _) => 3000;
        var path = t.Write(tmp.Path, "W", new[] { 1 });
        var bytes = File.ReadAllBytes(path);
        int sof = bytes.AsSpan().IndexOf(new byte[] { 0xFF, 0xC3 });
        bytes[sof + 8] = 32;                                                                     // JPEG width 33 -> 32
        File.WriteAllBytes(path, bytes);
        using var clip = MlvClip.Open(path);
        Assert.Contains("lossless frames hold 32x32x2", clip.Unsupported);
    }
}
