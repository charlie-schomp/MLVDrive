// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
namespace MlvDrive.Core.Dng;

/// <summary>What a Dual ISO clip measures: which rows are the bright pair, and how much brighter they are.</summary>
/// <param name="BrightPhase">Row (mod 4) where the bright pair starts: rows p and p + 1 are bright, p + 2 and p + 3 dark.</param>
/// <param name="Gain">Bright rows over dark rows, above black.</param>
public readonly record struct DualIsoInfo(int BrightPhase, double Gain);

/// <summary>
/// Dual ISO video: the sensor alternates two ISOs in pairs of rows. The two exposures of the
/// same colour are 2 rows apart (the CFA repeats every 2 rows, the exposures every 4), so each
/// pixel's missing exposure comes from the rows above and below it, then the two are blended:
/// the bright rows for clean shadows, the dark rows for the highlights only they hold.
/// Every row is smoothed vertically to keep them matched, which trades vertical detail for
/// freedom from scan lines. Output is on the dark rows'
/// scale, multiplied up to use the full 16 bits, so a frame opens at the low ISO's brightness
/// with its extra highlight range intact.
/// </summary>
public static class DualIso
{
    /// <summary>Weaker than this and it is not a Dual ISO frame, whatever the clip's header says.</summary>
    const double MinGain = 1.2;

    /// <summary>The pattern in one frame, or null if it has none.</summary>
    public static DualIsoInfo? Measure(ReadOnlySpan<ushort> pixels, int width, int height, int black, int white)
    {
        if (height < 24 || width < 8 || white <= black) return null;

        // which pair of rows is brighter, by the median-free mean of each row phase
        Span<double> sum = stackalloc double[4];
        Span<long> count = stackalloc long[4];
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * width, width);
            double s = 0;
            for (int x = 0; x < width; x += 2) s += row[x];
            sum[y & 3] += s;
            count[y & 3] += (width + 1) / 2;
        }
        int phase = 0;
        double best = double.MinValue;
        for (int p = 0; p < 4; p++)
        {
            double contrast = sum[p] / count[p] + sum[(p + 1) & 3] / count[(p + 1) & 3]
                            - sum[(p + 2) & 3] / count[(p + 2) & 3] - sum[(p + 3) & 3] / count[(p + 3) & 3];
            if (contrast > best) { best = contrast; phase = p; }
        }

        // the gain from same-colour pixels 2 rows apart, where neither is near black or clipped
        int range = white - black;
        int low = range / 64, highBright = range * 9 / 10;
        var ratios = new List<float>(1 << 16);
        int stepX = Math.Max(1, width / 512), stepY = Math.Max(4, height / 256) & ~3;
        for (int k = 0; k < 2; k++)
        {
            for (int y = ((phase + k) & 3) + 4; y < height - 4; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    int bright = pixels[y * width + x] - black;
                    if (bright < low || bright > highBright) continue;
                    int a = pixels[(y - 2) * width + x] - black, b = pixels[(y + 2) * width + x] - black;
                    if (a < low / 4 || b < low / 4) continue;
                    // only smooth places: an edge between the rows is not an exposure difference
                    if (Math.Abs(a - b) * 4 > a + b) continue;
                    ratios.Add(2f * bright / (a + b));
                }
            }
        }
        if (ratios.Count < 64) return null;
        ratios.Sort();
        double gain = ratios[ratios.Count / 2];
        return gain >= MinGain ? new DualIsoInfo(phase, gain) : null;
    }

    /// <summary>The largest whole multiplier that keeps the white level inside 16 bits.</summary>
    public static int OutputScale(int white) => Math.Max(1, 65535 / Math.Max(1, white));

    /// <summary>Blend one frame, in place. Black and white become black and white times <see cref="OutputScale"/>.</summary>
    public static void Merge(ushort[] pixels, int width, int height, int black, int white, DualIsoInfo info)
    {
        int scale = OutputScale(white);
        double gain = info.Gain;
        double range = white - black;
        // the bright rows are trusted up to here, then fade out before they clip
        double fadeFrom = range * 0.70, fadeTo = range * 0.92;

        var source = pixels.AsSpan(0, width * height).ToArray();
        Parallel.For(0, height, y =>
        {
            int Row(int r) => r < 0 || r >= height ? Math.Clamp(r < 0 ? y + 4 : y - 4, 0, height - 1) : r;
            var row = source.AsSpan(y * width, width);
            var above2 = source.AsSpan(Row(y - 2) * width, width);
            var below2 = source.AsSpan(Row(y + 2) * width, width);
            var above4 = source.AsSpan(Row(y - 4) * width, width);
            var below4 = source.AsSpan(Row(y + 4) * width, width);
            var output = pixels.AsSpan(y * width, width);
            bool isBright = ((y - info.BrightPhase) & 3) < 2;
            for (int x = 0; x < width; x++)
            {
                // the same kind of estimate on every row, so noise and detail match from row to row:
                // a pixel's own exposure smoothed with the rows 4 away, the other from the rows 2 away.
                // A pixel's own value alone would leave scan lines wherever the two exposures' noise differs.
                double own = (2.0 * row[x] + above4[x] + below4[x]) * 0.25 - black;
                double other = (above2[x] + below2[x]) * 0.5 - black;
                double bright = isBright ? own : other;
                double dark = isBright ? other : own;

                double keepBright = bright <= fadeFrom ? 1 : bright >= fadeTo ? 0 : (fadeTo - bright) / (fadeTo - fadeFrom);
                double value = keepBright * bright / gain + (1 - keepBright) * dark;
                double result = (value + black) * scale;
                output[x] = result <= 0 ? (ushort)0 : result >= 65535 ? (ushort)65535 : (ushort)(result + 0.5);
            }
        });
    }
}
