using System.Buffers.Binary;
using MlvDrive.Core.Audio;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.VirtualFs;
using MlvDrive.Core.Testing;

namespace MlvDrive.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mlvdrive-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class PixelUnpackerTests
{
    [Theory]
    [InlineData(10)] [InlineData(12)] [InlineData(14)] [InlineData(16)] [InlineData(11)]
    public void Unpack_any_range_matches_packed_pixels(int bpp)
    {
        var rng = new Random(bpp);
        var pixels = new ushort[1000];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)rng.Next(1 << bpp);
        var packed = PixelUnpacker.Pack(pixels, bpp);

        for (int trial = 0; trial < 200; trial++)
        {
            int first = rng.Next(pixels.Length);
            int count = rng.Next(1, pixels.Length - first + 1);
            long w0 = PixelUnpacker.FirstWord(first, bpp);
            var window = new byte[PixelUnpacker.WordSpan(first, count, bpp) * 2];
            int avail = (int)Math.Min(window.Length, packed.Length - w0 * 2);
            packed.AsSpan((int)(w0 * 2), avail).CopyTo(window);
            var output = new ushort[count];
            PixelUnpacker.Unpack(window, bpp, first, count, output);
            Assert.Equal(pixels.AsSpan(first, count).ToArray(), output);
        }
    }

    [Fact]
    public void Fourteen_bit_layout_matches_magic_lantern_pixblock()
    {
        // raw_pixblock: first 16-bit word holds pixel a in bits 15..2 and the top 2 bits of b.
        var packed = PixelUnpacker.Pack(new ushort[] { 0x3FFF, 0, 0, 0, 0, 0, 0, 0 }, 14);
        Assert.Equal(0xFFFC, BinaryPrimitives.ReadUInt16LittleEndian(packed));
    }
}

public class ClipTests
{
    [Fact]
    public void Indexes_frames_across_chunks_in_frame_order()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        var main = t.Write(tmp.Path, "M08-1651", new[] { 3, 2, 2 }, order: new[] { 0, 2, 1, 4, 3, 6, 5 });
        using var clip = MlvClip.Open(main);
        Assert.Null(clip.Unsupported);
        Assert.Equal(3, clip.FilePaths.Count);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6 }, clip.Frames.Select(f => f.FrameNumber).ToArray());
        Assert.Equal("Canon EOS 6D Mark II", clip.Idnt!.Value.CameraName);
        Assert.Equal(12, clip.Rawi!.Value.BitsPerPixel);
        Assert.Empty(clip.Warnings);
    }

    [Fact]
    public void Truncated_clip_keeps_complete_frames()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        var main = t.Write(tmp.Path, "CUT", new[] { 4 });
        var bytes = File.ReadAllBytes(main);
        File.WriteAllBytes(main, bytes[..^500]);                  // last frame cut short, as mid-copy
        using var clip = MlvClip.Open(main);
        Assert.Equal(3, clip.Frames.Count);
        Assert.Contains(clip.Warnings, w => w.Contains("incomplete"));
    }

    [Fact]
    public void Garbage_after_frames_is_ignored()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        var main = t.Write(tmp.Path, "JUNK", new[] { 2 });
        File.AppendAllText(main, new string('ÿ', 300));
        using var clip = MlvClip.Open(main);
        Assert.Equal(2, clip.Frames.Count);
        Assert.Contains(clip.Warnings, w => w.Contains("unreadable"));
    }

    [Fact]
    public void Not_an_mlv_is_rejected()
    {
        using var tmp = new TempDir();
        var p = Path.Combine(tmp.Path, "X.MLV");
        File.WriteAllBytes(p, new byte[100]);
        Assert.ThrowsAny<Exception>(() => MlvClip.Open(p));
    }

    [Fact]
    public void Lzma_clip_is_reported_unsupported()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip { VideoClass = 0x81 };
        using var clip = MlvClip.Open(t.Write(tmp.Path, "LZ", new[] { 1 }));
        Assert.Contains("LZMA", clip.Unsupported);
    }
}

public class DngTests
{
    static byte[] ReadAll(DngSequence seq, int frame, int chunk, bool oddStart = false)
    {
        var all = new byte[seq.FileSize];
        long pos = 0;
        if (oddStart)
        {
            // An odd first read, so every later read starts mid-pixel.
            pos = seq.Read(frame, 0, all.AsSpan(0, DngSequence.HeaderSize + 3));
        }
        while (pos < all.Length)
        {
            int n = seq.Read(frame, pos, all.AsSpan((int)pos, (int)Math.Min(chunk, all.Length - pos)));
            Assert.True(n > 0);
            pos += n;
        }
        return all;
    }

    [Theory]
    [InlineData(10)] [InlineData(12)] [InlineData(14)]
    public void Dng_pixels_equal_the_recorded_pixels(int bpp)
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip { Bpp = bpp, Black = 128 << (bpp - 10), White = (1 << bpp) - 46 };
        using var clip = MlvClip.Open(t.Write(tmp.Path, "C", new[] { 2, 1 }));
        var seq = new DngSequence(clip);
        Assert.Equal(DngSequence.HeaderSize + t.Width * t.Height * 2, seq.FileSize);

        foreach (var (chunk, odd) in new[] { (65536, false), (4096, false), (777, true), (1 << 20, false) })
        {
            var file = ReadAll(seq, 2, chunk, odd);
            var expected = t.FramePixels(2);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(DngSequence.HeaderSize + i * 2)));
        }
    }

    [Fact]
    public void Dng_header_describes_the_frame()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        using var clip = MlvClip.Open(t.Write(tmp.Path, "M08-1651", new[] { 3 }));
        var seq = new DngSequence(clip);
        var tiff = new TiffReader(seq.BuildHeader(1));

        Assert.Equal(tiff.Ifd0Order.OrderBy(x => x), tiff.Ifd0Order);   // TIFF requires sorted tags
        Assert.Equal((uint)t.Width, tiff.Long(256));
        Assert.Equal((uint)t.Height, tiff.Long(257));
        Assert.Equal(16u, tiff.Long(258));
        Assert.Equal(32803u, tiff.Long(262));
        Assert.Equal((uint)DngSequence.HeaderSize, tiff.Long(273));
        Assert.Equal((uint)(t.Width * t.Height * 2), tiff.Long(279));
        Assert.Equal((uint)t.Black, tiff.Long(50714));
        Assert.Equal((uint)t.White, tiff.Long(50717));
        Assert.Equal(new byte[] { 0, 1, 1, 2 }, tiff.Ifd0[33422].Value);
        Assert.Equal("Canon", tiff.Ascii(271));
        Assert.Equal("Canon EOS 6D Mark II", tiff.Ascii(272));
        Assert.Equal("M08-1651", tiff.Ascii(51081));
        // 6D Mark II: its own dual-illuminant set (Adobe Standard), not the 5D Mark III fallback
        Assert.Equal(0.7295, tiff.Rationals(50721)[0], 4);     // ColorMatrix1, standard A
        Assert.Equal(17u, tiff.Long(50778));
        Assert.Equal(0.6875, tiff.Rationals(50722)[0], 4);     // ColorMatrix2, D65
        Assert.Equal(21u, tiff.Long(50779));
        Assert.Equal(0.5806, tiff.Rationals(50964)[0], 4);     // ForwardMatrix1
        Assert.Equal(0.5333, tiff.Rationals(50965)[0], 4);     // ForwardMatrix2
        // recorded frame is a crop of the camera buffer: active area is the whole frame
        Assert.Equal(new uint[] { 0, 0, (uint)t.Height, (uint)t.Width },
            Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(tiff.Ifd0[50829].Value.AsSpan(i * 4))).ToArray());
        Assert.Equal(23.976, tiff.Rationals(51044)[0], 3);
        Assert.Equal(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, tiff.Ifd0[51043].Value);   // frame 1 -> 00:00:00:01
        Assert.Equal("EF50mm f/1.8 STM", tiff.Ascii(42036, exif: true));
        Assert.Equal("2026:10:08 16:51:05", tiff.Ascii(306));
    }

    [Fact]
    public void Daylight_white_balance_is_plausible_for_canon()
    {
        var p = CameraProfiles.Find("Canon EOS 6D Mark II")!;
        var m = WhiteBalance.KelvinToMultipliers(5500, p.WhiteBalanceMatrix);
        // Canon daylight raw multipliers are typically about R 2.0, B 1.5 relative to green.
        Assert.InRange(m[0], 1.6, 2.6);
        Assert.Equal(1.0, m[1]);
        Assert.InRange(m[2], 1.2, 1.9);
        // warmer light needs less red gain and more blue gain
        var tungsten = WhiteBalance.KelvinToMultipliers(3200, p.WhiteBalanceMatrix);
        Assert.True(tungsten[0] < m[0] && tungsten[2] > m[2]);
    }

    [Fact]
    public void Unknown_camera_uses_matrix_from_clip_then_fallback()
    {
        int[] clipMatrix = new int[18];
        int[] m = { 6875, -970, -932, -4691, 12459, 2501, -874, 1953, 5809 };
        for (int i = 0; i < 9; i++) { clipMatrix[2 * i] = m[i]; clipMatrix[2 * i + 1] = 10000; }
        var (p, src) = CameraProfiles.Resolve("Canon EOS Something", clipMatrix, 21);
        Assert.Equal(ProfileSource.FromClip, src);
        Assert.Equal(m, p.ColorMatrix1);
        var (f, src2) = CameraProfiles.Resolve("Canon EOS Something", new int[18], 0);
        Assert.Equal(ProfileSource.Fallback, src2);
        Assert.Equal("Canon EOS 5D Mark III", f.Name);
    }

    [Fact]
    public void Every_profile_matrix_is_a_real_colour_matrix()
    {
        foreach (var p in CameraProfiles.All)
        {
            foreach (var m in new[] { p.ColorMatrix1, p.ColorMatrix2 }.Where(x => x is not null))
            {
                Assert.Equal(9, m!.Length);
                Assert.True(m[0] > 0 && m[4] > 0 && m[8] > 0, p.Name);
                Assert.True(m.Count(v => v < 0) >= 2, p.Name + ": colour matrices have negative off-diagonal terms");
            }
            WhiteBalance.KelvinToMultipliers(5500, p.WhiteBalanceMatrix);
        }
    }
}

public class AudioTests
{
    [Fact]
    public void Embedded_audio_is_placed_by_timestamp_and_lasts_as_long_as_the_picture()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip { WithAudio = true };
        using var clip = MlvClip.Open(t.Write(tmp.Path, "A", new[] { 3, 2 }));
        var wav = EmbeddedWav.Create(clip)!;

        var bytes = new byte[wav.Size];
        Assert.Equal(bytes.Length, wav.Read(0, bytes));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        double seconds = dataSize / 192000.0;
        Assert.InRange(seconds, 5 / 23.976, 5 / 23.976 + 0.001);

        // each packet's bytes sit at its timestamp; between packets there is silence
        for (int f = 0; f < 5; f++)
        {
            long start = (long)(f * 1_000_000L * 1000 / 23976 * 192000L / 1_000_000L);
            start -= start % 4;
            Assert.Equal(SyntheticClip.AudioByte(start), bytes[44 + start]);
            Assert.Equal(SyntheticClip.AudioByte(start + 4799), bytes[44 + start + 4799]);
            if (start + 4800 < dataSize) Assert.Equal(0, bytes[44 + start + 4800]);
        }

        // reading in small pieces gives the same bytes
        var pieces = new byte[wav.Size];
        for (long pos = 0; pos < pieces.Length; pos += 333)
            wav.Read(pos, pieces.AsSpan((int)pos, (int)Math.Min(333, pieces.Length - pos)));
        Assert.Equal(bytes, pieces);
    }

    [Fact]
    public void Scratch_wav_is_found_in_dcim_audio()
    {
        using var tmp = new TempDir();
        var video = Path.Combine(tmp.Path, "DCIM", "100CANON");
        var audio = Path.Combine(tmp.Path, "DCIM", "AUDIO");
        Directory.CreateDirectory(audio);
        var main = new SyntheticClip().Write(video, "M08-1651", new[] { 1 });
        File.WriteAllBytes(Path.Combine(audio, "M08-1651.WAV"), new byte[] { 1, 2, 3 });
        Assert.Equal(Path.Combine(audio, "M08-1651.WAV"), PassThroughFile.FindScratchWav(main));
    }
}

public class TreeTests
{
    [Fact]
    public void Drive_tree_mirrors_folders_and_exposes_clip_folders()
    {
        using var tmp = new TempDir();
        var video = Path.Combine(tmp.Path, "DCIM", "100CANON");
        var t = new SyntheticClip();
        t.Write(video, "M08-1651", new[] { 2 });
        t.Write(video, "M08-1652", new[] { 1 });
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Empty"));
        var hidden = Path.Combine(tmp.Path, "$RECYCLE.BIN");
        t.Write(hidden, "OLD", new[] { 1 });
        Directory.CreateDirectory(Path.Combine(tmp.Path, "DCIM", "AUDIO"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "DCIM", "AUDIO", "M08-1651.WAV"), new byte[] { 1, 2, 3, 4 });

        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("SSD 2TB", tmp.Path) }, cache);

        Assert.Single(root.Children());
        Assert.Null(VirtualPath.Resolve(root, @"SSD 2TB\Empty"));
        Assert.Null(VirtualPath.Resolve(root, @"SSD 2TB\$RECYCLE.BIN"));
        var clipDir = Assert.IsAssignableFrom<VDirectory>(VirtualPath.Resolve(root, @"SSD 2TB\DCIM\100CANON\M08-1651"));
        var names = clipDir.Children().Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "M08-1651_000000.dng", "M08-1651_000001.dng", "M08-1651.wav" }, names);
        var dng = Assert.IsType<VFile>(VirtualPath.Resolve(root, @"ssd 2tb\dcim\100canon\m08-1651\M08-1651_000001.DNG"));
        Assert.Equal(DngSequence.HeaderSize + t.Width * t.Height * 2, dng.Size);
        var wav = Assert.IsType<VFile>(VirtualPath.Resolve(root, @"SSD 2TB\DCIM\100CANON\M08-1651\M08-1651.wav"));
        Assert.Equal(4, wav.Size);
    }

    [Fact]
    public void Clip_still_being_copied_fills_in()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        var main = t.Write(tmp.Path, "GROW", new[] { 4 });
        var full = File.ReadAllBytes(main);
        File.WriteAllBytes(main, full[..(full.Length / 2)]);

        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("D", tmp.Path) }, cache);
        var dir = (VDirectory)VirtualPath.Resolve(root, @"D\GROW")!;
        int partial = dir.Children().Count;

        File.WriteAllBytes(main, full);
        File.SetLastWriteTimeUtc(main, DateTime.UtcNow.AddSeconds(5));
        Assert.True(dir.Children().Count > partial);
        Assert.Equal(4, dir.Children().Count(c => c.Name.EndsWith(".dng")));
    }

    [Fact]
    public void Idle_clips_release_their_files_and_reopen_on_read()
    {
        using var tmp = new TempDir();
        var t = new SyntheticClip();
        var main = t.Write(tmp.Path, "EJECT", new[] { 2, 1 });
        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("D", tmp.Path) }, cache);
        var dng = (VFile)VirtualPath.Resolve(root, @"D\EJECT\EJECT_000002.dng")!;
        var before = new byte[dng.Size];
        dng.Read(0, before);

        cache.ReleaseIdle(TimeSpan.Zero);
        // Nothing holds the files now: an exclusive open (what ejecting needs) succeeds.
        foreach (var f in Directory.GetFiles(tmp.Path))
            using (File.Open(f, FileMode.Open, FileAccess.Read, FileShare.None)) { }

        var after = new byte[dng.Size];
        dng.Read(0, after);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Unsupported_clip_explains_itself()
    {
        using var tmp = new TempDir();
        new SyntheticClip { VideoClass = 0x81 }.Write(tmp.Path, "LZ", new[] { 1 });
        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("D", tmp.Path) }, cache);
        var note = Assert.IsType<VFile>(Assert.Single(((VDirectory)VirtualPath.Resolve(root, @"D\LZ")!).Children()));
        var text = new byte[note.Size];
        note.Read(0, text);
        Assert.Contains("LZMA", System.Text.Encoding.UTF8.GetString(text));
    }

    [Fact]
    public void Duplicate_drive_names_get_a_suffix()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        new SyntheticClip().Write(a.Path, "X", new[] { 1 });
        new SyntheticClip().Write(b.Path, "X", new[] { 1 });
        using var cache = new ClipCache();
        var root = SourceScanner.Build(new[] { new ClipSource("Card", a.Path), new ClipSource("Card", b.Path) }, cache);
        Assert.Equal(new[] { "Card", "Card (2)" }, root.Children().Select(c => c.Name).ToArray());
    }
}
