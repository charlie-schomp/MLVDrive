using System.Buffers.Binary;
using MlvDrive.Core.Audio;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.Testing;
using MlvDrive.Core.VirtualFs;

namespace MlvDrive.Tests;

/// <summary>
/// The 6D Mark II scratch WAV for dual-system sound: lined up with frame 1 from the clip's
/// "scratch_wav_start_us", and carrying a Tentacle's LTC as timecode in the WAV and the DNGs.
/// </summary>
public class ScratchSyncTests
{
    const int Rate = 48000;
    const double Spf2398 = 48000 * 1001 / 24000.0;              // 2002 samples a frame
    static readonly Timecode TenHours = new(Timecode.ToFrames(10, 0, 0, 0, 24, false), 24, false, 24000, 1001);

    static short[] Pattern(int n)
    {
        var a = new short[n];
        for (int i = 0; i < n; i++) a[i] = (short)(i * 31 % 20000 - 10000);
        return a;
    }

    /// <summary>A card with one clip (frame 1 at 1.0 s on the clip's clock) and its scratch WAV.</summary>
    static (string Mlv, string Wav) Card(string root, string name, long? startUs, Timecode? ltc, double ltcWordAt,
                                         uint fpsNom = 24000, uint fpsDen = 1001, bool ltcOnLeft = false)
    {
        var video = Path.Combine(root, "DCIM", "100CANON");
        var audio = Path.Combine(root, "DCIM", "AUDIO");
        Directory.CreateDirectory(audio);
        var mlv = new SyntheticClip { ScratchWavStartUs = startUs, FpsNom = fpsNom, FpsDen = fpsDen }.Write(video, name, new[] { 48 });
        int n = Rate * 8;
        var tc = ltc is { } s ? LtcSignal.Generate(n, ltcWordAt, s) : Pattern(n);
        var wav = Path.Combine(audio, name + ".WAV");
        LtcSignal.WriteStereoWav(wav, ltcOnLeft ? tc : Pattern(n), ltcOnLeft ? Pattern(n) : tc);
        return (mlv, wav);
    }

    static byte[] ReadAll(IVirtualFileContent c)
    {
        var b = new byte[c.Size];
        Assert.Equal(b.Length, c.Read(0, b));
        return b;
    }

    /// <summary>RIFF chunks of a WAV: id to (payload offset, size).</summary>
    static Dictionary<string, (int Offset, int Size)> Chunks(byte[] wav)
    {
        var d = new Dictionary<string, (int, int)>();
        for (int pos = 12; pos + 8 <= wav.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(pos + 4));
            d[id] = (pos + 8, size);
            if (id == "data") break;
            pos += 8 + size + (size & 1);
        }
        return d;
    }

    [Fact]
    public void Scratch_wav_starts_on_frame_one_and_carries_the_ltc()
    {
        using var tmp = new TempDir();
        // WAV started 1.5 s before the clip's time zero; frame 1 is at +1.0 s -> 2.5 s into the WAV
        var (mlv, wav) = Card(tmp.Path, "M26-1301", -1_500_000, TenHours, 1000);
        using var clip = MlvClip.Open(mlv);
        Assert.Equal(-1_500_000, clip.ScratchWavStartUs);

        var s = ScratchWav.Create(clip, wav)!;
        Assert.Equal(2.5, s.LeadSeconds, 6);
        Assert.Equal(1, s.LtcChannel);
        double expected = TenHours.Frames + (2.5 * Rate - 1000) / Spf2398;
        Assert.Equal((long)Math.Round(expected), s.StartTimecode!.Value.Frames);

        var bytes = ReadAll(s);
        var chunks = Chunks(bytes);
        // the clip's length, and its first sample is the source's sample at 2.5 s
        long samples = (long)Math.Ceiling(48000.0 * 1001 * 48 / 24000);
        Assert.Equal(samples * 4, chunks["data"].Size);
        var source = File.ReadAllBytes(wav);
        int src = 44 + 120000 * 4;
        Assert.Equal(source.AsSpan(src, 4000).ToArray(), bytes.AsSpan(chunks["data"].Offset, 4000).ToArray());

        // Broadcast WAV time reference: samples since midnight at frame 1, to the sample
        ulong timeRef = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(chunks["bext"].Offset + 338));
        Assert.InRange((double)timeRef, expected * Spf2398 - 1, expected * Spf2398 + 1);
        Assert.Contains("24000/1001", System.Text.Encoding.UTF8.GetString(bytes, chunks["iXML"].Offset, chunks["iXML"].Size));

        // RIFF size covers everything after it
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));

        // reading in small pieces gives the same bytes
        var pieces = new byte[s.Size];
        for (long pos = 0; pos < pieces.Length; pos += 777)
            s.Read(pos, pieces.AsSpan((int)pos, (int)Math.Min(777, pieces.Length - pos)));
        Assert.Equal(bytes, pieces);
    }

    [Fact]
    public void Clip_folder_shows_the_aligned_scratch_and_dngs_carry_the_ltc()
    {
        using var tmp = new TempDir();
        var (mlv, _) = Card(tmp.Path, "M26-1302", -1_500_000, TenHours, 1000, ltcOnLeft: true);
        using var cache = new ClipCache();
        var entry = cache.Get(mlv, "M26-1302");
        var wav = (VFile)entry.Lookup("M26-1302.wav")!;
        var head = new byte[200];
        wav.Read(0, head);
        Assert.Contains("bext", System.Text.Encoding.ASCII.GetString(head));

        var start = entry.Sequence!.StartTimecode!.Value;
        Assert.Equal(start.Frames, (long)Math.Round(TenHours.Frames + (2.5 * Rate - 1000) / Spf2398));
        foreach (int frame in new[] { 0, 5, 47 })
        {
            var dng = new TiffReader(entry.Sequence.BuildHeader(frame));
            var tc = dng.Ifd0[51043].Value;
            var (h, m, s, f) = start.Plus(frame).Fields;
            static int Bcd(byte b) => (b >> 4) * 10 + (b & 15);
            Assert.Equal((f, s, m, h), (Bcd((byte)(tc[0] & 0x3F)), Bcd(tc[1]), Bcd(tc[2]), Bcd(tc[3])));
        }
    }

    [Fact]
    public void Clip_without_a_start_time_keeps_its_scratch_as_recorded()
    {
        using var tmp = new TempDir();
        var (mlv, wavPath) = Card(tmp.Path, "M20-1446", null, TenHours, 1000);
        using var clip = MlvClip.Open(mlv);
        Assert.Null(clip.ScratchWavStartUs);
        var (audio, scratch, start) = ClipAudioSource.For(clip, mlv, new DngOptions());
        Assert.Null(scratch);
        Assert.Null(start);
        Assert.Equal(File.ReadAllBytes(wavPath), ReadAll(audio!));
        var dng = new TiffReader(new DngSequence(clip).BuildHeader(0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dng.Ifd0[51043].Value[..4]);
    }

    [Fact]
    public void Ltc_at_another_rate_stamps_the_wav_but_not_the_dngs()
    {
        using var tmp = new TempDir();
        var tc25 = new Timecode(Timecode.ToFrames(10, 0, 0, 0, 25, false), 25, false, 25, 1);
        var (mlv, _) = Card(tmp.Path, "M26-1303", -1_500_000, tc25, 1000);
        using var clip = MlvClip.Open(mlv);
        var (_, scratch, start) = ClipAudioSource.For(clip, mlv, new DngOptions());
        Assert.NotNull(scratch!.StartTimecode);
        Assert.Null(start);
    }

    [Fact]
    public void Camera_clock_drift_against_the_timecode_is_measured_over_the_take()
    {
        using var tmp = new TempDir();
        var video = Path.Combine(tmp.Path, "DCIM", "100CANON");
        var audio = Path.Combine(tmp.Path, "DCIM", "AUDIO");
        Directory.CreateDirectory(audio);
        const int frames = 960;                                                 // 40 s
        var mlv = new SyntheticClip { Width = 16, Height = 8, ScratchWavStartUs = -1_500_000, FpsNom = 24000, FpsDen = 1001 }
            .Write(video, "M26-1400", new[] { frames });
        int n = Rate * 44;
        var wav = Path.Combine(audio, "M26-1400.WAV");
        LtcSignal.WriteStereoWav(wav, Pattern(n), LtcSignal.Generate(n, 1000, TenHours, recorderPpm: 300));

        using var clip = MlvClip.Open(mlv);
        var s = ScratchWav.Create(clip, wav)!;
        Assert.InRange(s.ClockPpm!.Value, 297, 303);
        double seconds = frames * 1001 / 24000.0;
        Assert.InRange(s.DriftSeconds!.Value, seconds * 297e-6, seconds * 303e-6);

        // 12 ms is 0.29 of a frame: the clip folder says so
        using var cache = new ClipCache();
        Assert.Contains("ppm against the timecode", cache.Get(mlv, "M26-1400").Problem);
    }

    [Fact]
    public void Latency_calibration_moves_the_cut()
    {
        using var tmp = new TempDir();
        var (mlv, wav) = Card(tmp.Path, "M26-1304", -1_500_000, null, 0);
        using var clip = MlvClip.Open(mlv);
        var s = ScratchWav.Create(clip, wav, latencyMs: 20)!;
        Assert.Equal(2.52, s.LeadSeconds, 6);
        Assert.Null(s.StartTimecode);
        var bytes = ReadAll(s);
        var source = File.ReadAllBytes(wav);
        Assert.Equal(source.AsSpan(44 + 120960 * 4, 400).ToArray(), bytes.AsSpan(Chunks(bytes)["data"].Offset, 400).ToArray());
    }
}
