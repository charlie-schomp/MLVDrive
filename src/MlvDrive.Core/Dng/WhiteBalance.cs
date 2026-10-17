using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.Dng;

/// <summary>
/// As-shot white balance for the DNG AsShotNeutral tag, from the clip's WBAL block.
/// Kelvin to channel multipliers follows UFRaw (via MLVFS): daylight-locus chromaticity
/// for the temperature, through the camera's colour matrix, normalised to green.
/// </summary>
public static class WhiteBalance
{
    // Magic Lantern WBAL modes.
    const uint WbAuto = 0, WbSunny = 1, WbCloudy = 2, WbTungsten = 3, WbFluorescent = 4,
               WbFlash = 5, WbCustom = 6, WbShade = 8, WbKelvin = 9;

    /// <summary>AsShotNeutral as three (numerator, denominator) rationals: R, G, B.</summary>
    public static (int Num, int Den)[] AsShotNeutral(WbalHeader? wbal, CameraProfile profile)
    {
        if (wbal is { Mode: WbCustom } c && c.GainG > 0 && c.GainR > 0 && c.GainB > 0)
        {
            // Custom gains are 1/canon_gain, 1024 = 1.0: R/G and B/G are the neutral directly.
            return new[] { ((int)c.GainR, (int)c.GainG), (1, 1), ((int)c.GainB, (int)c.GainG) };
        }

        double kelvin = 5500;
        if (wbal is { } w)
        {
            kelvin = w.Mode switch
            {
                WbAuto or WbKelvin when w.Kelvin is >= 2000 and <= 25000 => w.Kelvin,
                WbShade => 7000,
                WbCloudy => 6000,
                WbTungsten => 3200,
                WbFluorescent => 4000,
                WbSunny or WbFlash => 5500,
                _ => 5500,
            };
        }

        var mul = KelvinToMultipliers(kelvin, profile.WhiteBalanceMatrix);
        // AsShotNeutral is the camera-space neutral: the reciprocal of the multipliers.
        return new[]
        {
            (1_000_000, Clamp(mul[0] * 1_000_000)),
            (1, 1),
            (1_000_000, Clamp(mul[2] * 1_000_000)),
        };
    }

    static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 1, int.MaxValue);

    static readonly double[,] XyzToRgb =
    {
        { 3.24071, -0.969258, 0.0556352 },
        { -1.53726, 1.87599, -0.203996 },
        { -0.498571, 0.0415557, 1.05707 },
    };

    static readonly double[,] XyzRgb =
    {
        { 0.412453, 0.357580, 0.180423 },
        { 0.212671, 0.715160, 0.072169 },
        { 0.019334, 0.119193, 0.950227 },
    };

    /// <summary>Channel multipliers (R, G, B; G = 1) that make a grey under this light neutral.</summary>
    public static double[] KelvinToMultipliers(double temperature, int[] colorMatrixD65, double green = 1.0)
    {
        var camXyz = new double[3, 3];
        for (int i = 0; i < 9; i++) camXyz[i / 3, i % 3] = colorMatrixD65[i] / 10000.0;

        // Camera response to sRGB primaries: cam_rgb = cam_xyz * xyz_rgb. UFRaw normalises
        // each row by its sum (pre_mul = 1/sum), inverts to rgb_cam, then pseudo-inverts
        // rgb_cam's transpose back; for a square matrix those steps cancel, leaving the
        // normalised cam_rgb, and 1/pre_mul undoes the normalisation. What remains:
        // multiplier[c] = 1 / (cam_rgb[c] . rgb_of_the_light).
        var camRgb = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double v = 0;
                for (int k = 0; k < 3; k++) v += camXyz[i, k] * XyzRgb[k, j];
                camRgb[i, j] = v;
            }

        var rgbWb = TemperatureToRgb(temperature);
        rgbWb[1] /= green;

        var mul = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double response = 0;
            for (int cc = 0; cc < 3; cc++) response += camRgb[c, cc] * rgbWb[cc];
            if (response <= 0) throw new ArgumentException("colour matrix gives no response to this light");
            mul[c] = 1 / response;
        }
        mul[0] /= mul[1];
        mul[2] /= mul[1];
        mul[1] = 1;
        return mul;
    }

    static double[] TemperatureToRgb(double t)
    {
        double xD = t <= 4000 ? 0.27475e9 / (t * t * t) - 0.98598e6 / (t * t) + 1.17444e3 / t + 0.145986
                  : t <= 7000 ? -4.6070e9 / (t * t * t) + 2.9678e6 / (t * t) + 0.09911e3 / t + 0.244063
                  : -2.0064e9 / (t * t * t) + 1.9018e6 / (t * t) + 0.24748e3 / t + 0.237040;
        double yD = -3 * xD * xD + 2.87 * xD - 0.275;
        double x = xD / yD, y = 1, z = (1 - xD - yD) / yD;
        var rgb = new double[3];
        double max = 0;
        for (int c = 0; c < 3; c++)
        {
            rgb[c] = x * XyzToRgb[0, c] + y * XyzToRgb[1, c] + z * XyzToRgb[2, c];
            max = Math.Max(max, rgb[c]);
        }
        for (int c = 0; c < 3; c++) rgb[c] /= max;
        return rgb;
    }

}
