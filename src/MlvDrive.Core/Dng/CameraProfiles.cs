namespace MlvDrive.Core.Dng;

/// <summary>
/// DNG colour calibration for one camera, in DNG tag order: ColorMatrix1 under
/// Illuminant1, optionally ColorMatrix2 under Illuminant2 plus forward matrices.
/// Matrices are row-major 3x3, scaled by 10000.
/// </summary>
public sealed record CameraProfile(
    string Name,
    int[] ColorMatrix1,
    int Illuminant1,
    int[]? ColorMatrix2 = null,
    int Illuminant2 = 0,
    int[]? ForwardMatrix1 = null,
    int[]? ForwardMatrix2 = null,
    string[]? Aliases = null)
{
    /// <summary>The matrix white balance is computed from: the D65 one when there are two.</summary>
    public int[] WhiteBalanceMatrix => ColorMatrix2 ?? ColorMatrix1;
}

/// <summary>Where a clip's colour matrix came from (shown to the user when it is a guess).</summary>
public enum ProfileSource { KnownCamera, FromClip, Fallback }

public static class CameraProfiles
{
    // DNG LightSource values.
    public const int IlluminantStdA = 17;
    public const int IlluminantD65 = 21;

    /// <summary>
    /// Dual-illuminant sets (standard A + D65, with forward matrices) as published by
    /// MLVFS (credited there to Andy600, from Adobe DNG Converter), and D65-only sets for
    /// later bodies from LibRaw's colordata.cpp (Adobe's D65 ColorMatrix2). LibRaw's rows
    /// for the 5D Mark III and 6D equal MLVFS's D65 matrices, which confirms the illuminant.
    /// </summary>
    public static readonly IReadOnlyList<CameraProfile> All = new CameraProfile[]
    {
        Dual("Canon EOS 5D Mark III",
            stdA: new[] { 7234, -1413, -600, -3631, 11150, 2850, -382, 1335, 6437 },
            d65:  new[] { 6722, -635, -963, -4287, 12460, 2028, -908, 2162, 5668 },
            forwardStdA: new[] { 7868, 92, 1683, 2291, 8615, -906, 27, -4752, 12976 },
            forwardD65:  new[] { 7637, 805, 1201, 2649, 9179, -1828, 137, -2456, 10570 }),
        Dual("Canon EOS 5D Mark II",
            stdA: new[] { 5309, -229, -336, -6241, 13265, 3337, -817, 1215, 6664 },
            d65:  new[] { 4716, 603, -830, -7798, 15474, 2480, -1496, 1937, 6651 },
            forwardStdA: new[] { 8924, -1041, 1760, 4351, 6621, -972, 505, -1562, 9308 },
            forwardD65:  new[] { 8924, -1041, 1760, 4351, 6621, -972, 505, -1562, 9308 }),
        Dual("Canon EOS 7D",
            stdA: new[] { 11620, -6350, 5, -2558, 10146, 2813, 24, 858, 6926 },
            d65:  new[] { 6844, -996, -856, -3876, 11761, 2396, -593, 1772, 6198 },
            forwardStdA: new[] { 5445, 3536, 662, 1106, 10136, -1242, -374, -3559, 12184 },
            forwardD65:  new[] { 7415, 1533, 695, 2499, 9997, -2497, -22, -1933, 10207 }),
        Dual("Canon EOS 6D",
            stdA: new[] { 7546, -1435, -929, -3846, 11488, 2692, -332, 1209, 6370 },
            d65:  new[] { 7034, -804, -1014, -4420, 12564, 2058, -851, 1994, 5758 },
            forwardStdA: new[] { 7763, 65, 1815, 2364, 8351, -715, -59, -4228, 12538 },
            forwardD65:  new[] { 7464, 1044, 1135, 2648, 9173, -1820, 113, -2154, 10292 }),
        Dual("Canon EOS 70D",
            stdA: new[] { 7546, -1435, -929, -3846, 11488, 2692, -332, 1209, 6370 },
            d65:  new[] { 7034, -804, -1014, -4420, 12564, 2058, -851, 1994, 5758 },
            forwardStdA: new[] { 7763, 65, 1815, 2364, 8351, -715, -59, -4228, 12538 },
            forwardD65:  new[] { 7464, 1044, 1135, 2648, 9173, -1820, 113, -2154, 10292 }),
        Dual("Canon EOS 60D",
            stdA: new[] { 7428, -1897, -491, -3505, 10963, 2929, -337, 1242, 6413 },
            d65:  new[] { 6719, -994, -925, -4408, 12426, 2211, -887, 2129, 6051 },
            forwardStdA: new[] { 7550, 645, 1448, 2138, 8936, -1075, -5, -4306, 12562 },
            forwardD65:  new[] { 7286, 1385, 972, 2600, 9468, -2068, 93, -2268, 10426 }),
        Dual("Canon EOS 50D",
            stdA: new[] { 5852, -578, -41, -4691, 11696, 3427, -886, 2323, 6879 },
            d65:  new[] { 4920, 616, -593, -6493, 13964, 2784, -1774, 3178, 7005 },
            forwardStdA: new[] { 8716, -692, 1618, 3408, 8077, -1486, -13, -6583, 14847 },
            forwardD65:  new[] { 9485, -1150, 1308, 4313, 7807, -2120, 293, -2826, 10785 }),
        Dual("Canon EOS 550D",
            stdA: new[] { 7755, -2449, -349, -3106, 10222, 3362, -156, 986, 6409 },
            d65:  new[] { 6941, -1164, -857, -3825, 11597, 2534, -416, 1540, 6039 },
            forwardStdA: new[] { 7163, 1301, 1179, 1926, 9543, -1469, -278, -3830, 12359 },
            forwardD65:  new[] { 7239, 1838, 566, 2467, 10246, -2713, -112, -1754, 10117 }, aliases: new[] { "Canon EOS Rebel T2i", "Canon EOS Kiss X4" }),
        Dual("Canon EOS 600D",
            stdA: new[] { 7164, -1916, -431, -3361, 10600, 3200, -272, 1058, 6442 },
            d65:  new[] { 6461, -907, -882, -4300, 12184, 2378, -819, 1944, 5931 },
            forwardStdA: new[] { 7486, 835, 1322, 2099, 9147, -1245, -12, -3822, 12085 },
            forwardD65:  new[] { 7359, 1365, 918, 2610, 9687, -2297, 98, -2155, 10309 }, aliases: new[] { "Canon EOS Rebel T3i", "Canon EOS Kiss X5" }),
        Dual("Canon EOS 650D",
            stdA: new[] { 6985, -1611, -397, -3596, 10749, 3295, -349, 1136, 6512 },
            d65:  new[] { 6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148 },
            forwardStdA: new[] { 7747, 485, 1411, 2340, 8840, -1180, 105, -4147, 12293 },
            forwardD65:  new[] { 7397, 1199, 1047, 2650, 9355, -2005, 193, -2113, 10171 }, aliases: new[] { "Canon EOS Rebel T4i", "Canon EOS Kiss X6i" }),
        Dual("Canon EOS 700D",
            stdA: new[] { 6985, -1611, -397, -3596, 10749, 3295, -349, 1136, 6512 },
            d65:  new[] { 6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148 },
            forwardStdA: new[] { 7747, 485, 1411, 2340, 8840, -1180, 105, -4147, 12293 },
            forwardD65:  new[] { 7397, 1199, 1047, 2650, 9355, -2005, 193, -2113, 10171 }, aliases: new[] { "Canon EOS Rebel T5i", "Canon EOS Kiss X7i" }),
        Dual("Canon EOS 1100D",
            stdA: new[] { 6873, -1696, -529, -3659, 10795, 3313, -362, 1165, 7234 },
            d65:  new[] { 6444, -904, -893, -4563, 12308, 2535, -903, 2016, 6728 },
            forwardStdA: new[] { 7607, 647, 1389, 2337, 8876, -1213, 93, -3625, 11783 },
            forwardD65:  new[] { 7357, 1377, 909, 2729, 9630, -2359, 104, -1940, 10087 }, aliases: new[] { "Canon EOS Rebel T3", "Canon EOS Kiss X50" }),
        // MLVFS lists the 1100D forward matrix as this body's standard-A matrix
        // (all positive, not a colour matrix), so only its D65 matrix is kept.
        D65("Canon EOS M", new[] { 6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148 }),

        // D65 only (LibRaw colordata.cpp).
        D65("Canon EOS 6D Mark II", new[] { 6875, -970, -932, -4691, 12459, 2501, -874, 1953, 5809 }),
        D65("Canon EOS 5D Mark IV", new[] { 6446, -366, -864, -4436, 12204, 2513, -952, 2496, 6348 }),
        D65("Canon EOS 5DS", new[] { 6250, -711, -808, -5153, 12794, 2636, -1249, 2198, 5610 }, aliases: new[] { "Canon EOS 5DS R" }),
        D65("Canon EOS 7D Mark II", new[] { 7268, -1082, -969, -4186, 11839, 2663, -825, 2029, 5839 }),
        D65("Canon EOS 80D", new[] { 7457, -671, -937, -4849, 12495, 2643, -1213, 2354, 5492 }),
        D65("Canon EOS 90D", new[] { 11498, -3759, -1516, -5073, 12954, 2349, -892, 1867, 6118 }),
        D65("Canon EOS 77D", new[] { 7377, -742, -998, -4235, 11981, 2549, -673, 1918, 5538 }, aliases: new[] { "Canon EOS 9000D" }),
        D65("Canon EOS 200D", new[] { 7377, -742, -998, -4235, 11981, 2549, -673, 1918, 5538 }, aliases: new[] { "Canon EOS Rebel SL2", "Canon EOS Kiss X9" }),
        D65("Canon EOS 750D", new[] { 6362, -823, -847, -4426, 12109, 2616, -743, 1857, 5635 }, aliases: new[] { "Canon EOS Rebel T6i", "Canon EOS Kiss X8i" }),
        D65("Canon EOS 760D", new[] { 6362, -823, -847, -4426, 12109, 2616, -743, 1857, 5635 }, aliases: new[] { "Canon EOS Rebel T6s", "Canon EOS 8000D" }),
        D65("Canon EOS 850D", new[] { 9079, -1923, -1236, -4677, 12454, 2492, -922, 2319, 5565 }, aliases: new[] { "Canon EOS Rebel T8i", "Canon EOS Kiss X10i" }),
        D65("Canon EOS 100D", new[] { 6602, -841, -939, -4472, 12458, 2247, -975, 2039, 6148 }, aliases: new[] { "Canon EOS Rebel SL1", "Canon EOS Kiss X7" }),
        D65("Canon EOS 1200D", new[] { 6461, -907, -882, -4300, 12184, 2378, -819, 1944, 5931 }, aliases: new[] { "Canon EOS Rebel T5", "Canon EOS Kiss X70" }),
        D65("Canon EOS 1300D", new[] { 6939, -1016, -866, -4428, 12473, 2177, -1175, 2178, 6162 }, aliases: new[] { "Canon EOS Rebel T6", "Canon EOS Kiss X80" }),
        D65("Canon EOS M2", new[] { 6400, -480, -888, -5294, 13416, 2047, -1296, 2203, 6137 }),
        D65("Canon EOS M50", new[] { 8532, -701, -1167, -4095, 11879, 2508, -797, 2424, 7010 }, aliases: new[] { "Canon EOS Kiss M" }),
        D65("Canon EOS M6 Mark II", new[] { 11498, -3759, -1516, -5073, 12954, 2349, -892, 1867, 6118 }),
        D65("Canon EOS R", new[] { 8293, -1789, -1094, -5025, 12925, 2327, -1199, 2769, 6108 }),
        D65("Canon EOS RP", new[] { 8608, -2097, -1178, -5425, 13265, 2383, -1149, 2238, 5680 }),
        D65("Canon EOS R5", new[] { 9766, -2953, -1254, -4276, 12116, 2433, -437, 1336, 5131 }),
        D65("Canon EOS R6", new[] { 8293, -1611, -1132, -4759, 12711, 2275, -1013, 2415, 5509 }),
    };

    static CameraProfile Dual(string name, int[] stdA, int[] d65, int[] forwardStdA, int[] forwardD65, string[]? aliases = null) =>
        new(name, stdA, IlluminantStdA, d65, IlluminantD65, forwardStdA, forwardD65, aliases);

    static CameraProfile D65(string name, int[] d65, string[]? aliases = null) =>
        new(name, d65, IlluminantD65, Aliases: aliases);

    public static CameraProfile Fallback => All[0];

    /// <summary>Find the profile for a camera name as recorded in the clip's IDNT block.</summary>
    public static CameraProfile? Find(string? cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) return null;
        var name = cameraName.Trim();
        foreach (var p in All)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            if (p.Aliases is { } aliases)
                foreach (var a in aliases)
                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>
    /// The profile to use for a clip: the known camera, else the matrix Magic Lantern
    /// itself wrote into the clip (raw_info.color_matrix1), else the 5D Mark III's.
    /// </summary>
    public static (CameraProfile Profile, ProfileSource Source) Resolve(string? cameraName, int[]? clipMatrixRationals, int clipIlluminant)
    {
        if (Find(cameraName) is { } known) return (known, ProfileSource.KnownCamera);

        if (clipMatrixRationals is { Length: 18 } && TryFromRationals(clipMatrixRationals, out var m))
        {
            int illuminant = clipIlluminant is > 0 and < 256 ? clipIlluminant : IlluminantD65;
            return (new CameraProfile(cameraName ?? "Unknown camera", m, illuminant), ProfileSource.FromClip);
        }
        return (Fallback, ProfileSource.Fallback);
    }

    static bool TryFromRationals(int[] r, out int[] matrix)
    {
        matrix = new int[9];
        bool any = false;
        for (int i = 0; i < 9; i++)
        {
            int num = r[2 * i], den = r[2 * i + 1];
            if (den <= 0) return false;
            double v = (double)num / den;
            if (Math.Abs(v) > 10) return false;
            matrix[i] = (int)Math.Round(v * 10000);
            if (matrix[i] != 0) any = true;
        }
        // A real camera matrix has a positive diagonal.
        return any && matrix[0] > 0 && matrix[4] > 0 && matrix[8] > 0;
    }
}
