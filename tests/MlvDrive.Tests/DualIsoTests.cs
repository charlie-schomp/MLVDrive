// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.Testing;

namespace MlvDrive.Tests;

public class DualIsoTests
{
    const int Black = 512, White = 4050, W = 256, H = 128;
    const double Gain = 3.25;

    /// <summary>A smooth scene in dark-row units, reaching past where the bright rows clip.</summary>
    static double Scene(int x) => 8 + 2600.0 * x / (W - 1);

    static ushort Recorded(int x, int y, int phase)
    {
        bool bright = ((y - phase) & 3) < 2;
        double v = Black + Scene(x) * (bright ? Gain : 1);
        return (ushort)Math.Min(White, Math.Round(v));
    }

    static string WriteClip(TempDir t, int phase, uint mode = 1) => new SyntheticClip
    {
        Width = W, Height = H, DualMode = mode,
        PixelFunction = (_, x, y) => Recorded(x, y, phase),
    }.Write(t.Path, "M01-0001", new[] { 3 });

    static ushort[] Pixels(DngSequence seq, int frame)
    {
        var bytes = new byte[seq.FileSize];
        seq.Read(frame, 0, bytes);
        var px = new ushort[W * H];
        Buffer.BlockCopy(bytes, DngSequence.HeaderSize, px, 0, bytes.Length - DngSequence.HeaderSize);
        return px;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Pattern_and_gain_are_measured_from_the_frames(int phase)
    {
        using var t = new TempDir();
        using var clip = MlvClip.Open(WriteClip(t, phase));
        var info = new DngSequence(clip).DualIso;
        Assert.NotNull(info);
        Assert.Equal(phase, info!.Value.BrightPhase);
        Assert.InRange(info.Value.Gain, Gain * 0.97, Gain * 1.03);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void Merged_frame_is_the_scene_on_every_row_including_past_the_bright_rows_clip(int phase)
    {
        using var t = new TempDir();
        using var clip = MlvClip.Open(WriteClip(t, phase));
        var seq = new DngSequence(clip);
        var px = Pixels(seq, 1);
        int scale = DualIso.OutputScale(White);
        for (int y = 4; y < H - 4; y++)
            for (int x = 0; x < W; x++)
            {
                double got = px[y * W + x] / (double)scale - Black;
                Assert.InRange(got, Scene(x) * 0.97 - 2, Scene(x) * 1.03 + 2);
            }
    }

    [Fact]
    public void Header_levels_follow_the_merge_and_rows_no_longer_alternate()
    {
        using var t = new TempDir();
        using var clip = MlvClip.Open(WriteClip(t, 0));
        var seq = new DngSequence(clip);
        var bytes = new byte[seq.FileSize];
        seq.Read(0, 0, bytes);
        var tags = new TiffReader(bytes).Ifd0;
        int scale = DualIso.OutputScale(White);
        Assert.Equal((uint)(Black * scale), BitConverter.ToUInt32(tags[50714].Value));
        Assert.Equal((uint)(White * scale), BitConverter.ToUInt32(tags[50717].Value));

        var px = Pixels(seq, 0);
        double Mean(int phase)
        {
            double s = 0; int n = 0;
            for (int y = 8 + phase; y < H - 8; y += 4) for (int x = 0; x < W; x += 8) { s += px[y * W + x]; n++; }
            return s / n;
        }
        Assert.InRange(Mean(0) / Mean(2), 0.98, 1.02);
    }

    [Fact]
    public void Off_leaves_the_recorded_rows_alone()
    {
        using var t = new TempDir();
        using var clip = MlvClip.Open(WriteClip(t, 0));
        var seq = new DngSequence(clip, new DngOptions(MergeDualIso: false));
        Assert.Null(seq.DualIso);
        var px = Pixels(seq, 0);
        Assert.Equal(Recorded(100, 40, 0), px[40 * W + 100]);
        Assert.Equal(Recorded(100, 42, 0), px[42 * W + 100]);
    }

    [Fact]
    public void A_header_that_says_dual_iso_over_plain_rows_is_not_merged()
    {
        using var t = new TempDir();
        var path = new SyntheticClip
        {
            Width = W, Height = H, DualMode = 1,
            PixelFunction = (_, x, _) => (ushort)Math.Round(Black + Scene(x)),
        }.Write(t.Path, "M01-0001", new[] { 2 });
        using var clip = MlvClip.Open(path);
        Assert.Null(new DngSequence(clip).DualIso);
    }

    [Fact]
    public void Mode_zero_is_not_merged()
    {
        using var t = new TempDir();
        using var clip = MlvClip.Open(WriteClip(t, 0, mode: 0));
        Assert.Null(new DngSequence(clip).DualIso);
    }
}
