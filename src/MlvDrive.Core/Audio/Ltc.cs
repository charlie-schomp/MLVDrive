namespace MlvDrive.Core.Audio;

/// <summary>
/// A point on an SMPTE timecode timeline: a frame count since midnight at a counting base
/// (24, 25 or 30 frames per timecode second), drop-frame or not, running at Num/Den frames
/// per real second (24000/1001 for 23.976).
/// </summary>
public readonly record struct Timecode(long Frames, int Base, bool DropFrame, int RateNum, int RateDen)
{
    public double Fps => (double)RateNum / RateDen;

    /// <summary>The same timeline <paramref name="delta"/> frames later.</summary>
    public Timecode Plus(long delta) => this with { Frames = Frames + delta };

    /// <summary>Hours, minutes, seconds and frames as shown (drop-frame skips ;00 and ;01 each minute but every tenth).</summary>
    public (int H, int M, int S, int F) Fields
    {
        get
        {
            long n = Frames;
            if (DropFrame && Base == 30)
            {
                long d = n / 17982, r = n % 17982;
                n += 18 * d + (r > 1 ? 2 * ((r - 2) / 1798) : 0);
            }
            return ((int)(n / (Base * 3600L) % 24), (int)(n / (Base * 60L) % 60), (int)(n / Base % 60), (int)(n % Base));
        }
    }

    public static long ToFrames(int h, int m, int s, int f, int timebase, bool dropFrame)
    {
        if (dropFrame && timebase == 30)
        {
            int totalMinutes = 60 * h + m;
            return 108000L * h + 1800L * m + 30L * s + f - 2L * (totalMinutes - totalMinutes / 10);
        }
        return ((h * 60L + m) * 60 + s) * timebase + f;
    }

    public override string ToString()
    {
        var (h, m, s, f) = Fields;
        return $"{h:00}:{m:00}:{s:00}{(DropFrame ? ';' : ':')}{f:00}";
    }
}

/// <summary>
/// Linear timecode (SMPTE LTC) read from one channel of audio, as a Tentacle Sync or any
/// timecode generator puts it on a camera's mic input. Biphase mark: a transition at every
/// bit boundary and one more in the middle of a 1; 80 bits a frame, ending in the sync word
/// 0011 1111 1111 1101.
/// </summary>
public static class Ltc
{
    static readonly byte[] SyncWord = { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1 };

    /// <summary>One decoded frame word: where its first bit starts (in samples) and what it says.</summary>
    public readonly record struct Word(int Position, int H, int M, int S, int F, bool DropFrame);

    /// <summary>Every complete LTC word in <paramref name="samples"/>, in order.</summary>
    public static List<Word> Decode(ReadOnlySpan<short> samples, int sampleRate)
    {
        var words = new List<Word>();
        if (samples.Length < sampleRate / 10) return words;

        double mean = 0;
        foreach (var x in samples) mean += x;
        mean /= samples.Length;
        double peak = 1;
        foreach (var x in samples) peak = Math.Max(peak, Math.Abs(x - mean));
        double hyst = 0.15 * peak;

        // transitions, with hysteresis so noise between them is not one
        var edges = new List<int>();
        int state = -1;
        for (int i = 0; i < samples.Length; i++)
        {
            double v = samples[i] - mean;
            if (v > hyst && state != 1) { if (state != -1) edges.Add(i); state = 1; }
            else if (v < -hyst && state != 0) { if (state != -1) edges.Add(i); state = 0; }
        }
        if (edges.Count < 200) return words;

        var gaps = new int[edges.Count - 1];
        for (int i = 0; i < gaps.Length; i++) gaps[i] = edges[i + 1] - edges[i];
        var sorted = (int[])gaps.Clone();
        Array.Sort(sorted);
        double cell = sorted[(int)(sorted.Length * 0.9)];       // a whole bit cell (most 0s are one gap)

        var bits = new List<int>();          // 0, 1, or -1 for "lost here"
        var starts = new List<int>();
        for (int i = 0; i < gaps.Length;)
        {
            int g = gaps[i];
            if (g > 0.75 * cell)
            {
                bits.Add(0); starts.Add(edges[i]);
                cell = 0.9 * cell + 0.1 * g;
                i++;
            }
            else if (i + 1 < gaps.Length && gaps[i + 1] <= 0.75 * cell)
            {
                bits.Add(1); starts.Add(edges[i]);
                cell = 0.9 * cell + 0.1 * (g + gaps[i + 1]);
                i += 2;
            }
            else
            {
                bits.Add(-1); starts.Add(edges[i]);
                i++;
            }
        }

        for (int k = 64; k + 16 <= bits.Count;)
        {
            if (Matches(bits, k) && AllKnown(bits, k - 64, 64))
            {
                int Val(int lo, int n) { int v = 0; for (int j = 0; j < n; j++) v |= bits[k - 64 + lo + j] << j; return v; }
                int f = Val(0, 4) + 10 * Val(8, 2);
                bool df = bits[k - 64 + 10] == 1;
                int s = Val(16, 4) + 10 * Val(24, 3);
                int m = Val(32, 4) + 10 * Val(40, 3);
                int h = Val(48, 4) + 10 * Val(56, 2);
                if (f < 30 && s < 60 && m < 60 && h < 24) words.Add(new Word(starts[k - 64], h, m, s, f, df));
                k += 80;
            }
            else k++;
        }
        return words;
    }

    static bool Matches(List<int> bits, int at)
    {
        for (int j = 0; j < 16; j++) if (bits[at + j] != SyncWord[j]) return false;
        return true;
    }

    static bool AllKnown(List<int> bits, int from, int count)
    {
        for (int j = from; j < from + count; j++) if (bits[j] < 0) return false;
        return true;
    }

    /// <summary>
    /// The timecode at sample position <paramref name="at"/> of the decoded stretch, as a
    /// fractional frame count (the camera is not genlocked to the generator, so a point in
    /// the audio usually falls between two LTC frames), or null if there is no usable LTC.
    /// </summary>
    public static (Timecode Tc, double Frames)? At(ReadOnlySpan<short> samples, int sampleRate, double at)
    {
        var words = Decode(samples, sampleRate);
        if (words.Count < 4) return null;

        int timebase = words.Max(w => w.F) + 1;
        timebase = timebase <= 24 ? 24 : timebase == 25 ? 25 : 30;
        bool df = words.Count(w => w.DropFrame) > words.Count / 2;

        var spans = new List<int>();
        int reference = -1;
        for (int i = 0; i + 1 < words.Count; i++)
        {
            var a = words[i]; var b = words[i + 1];
            if (Timecode.ToFrames(b.H, b.M, b.S, b.F, timebase, df) - Timecode.ToFrames(a.H, a.M, a.S, a.F, timebase, df) == 1)
            {
                spans.Add(b.Position - a.Position);
                if (reference < 0) reference = i;
            }
        }
        if (spans.Count < 3 || reference < 0) return null;

        // whole-sample spans round 1601.6 to 1602 - an hour of 29.97 would drift a second;
        // snap to the exact rate they belong to
        spans.Sort();
        var (num, den) = ExactRate((double)sampleRate / spans[spans.Count / 2]);
        double samplesPerFrame = (double)sampleRate * den / num;

        var r = words[reference];
        long n0 = Timecode.ToFrames(r.H, r.M, r.S, r.F, timebase, df);
        double frames = n0 + (at - r.Position) / samplesPerFrame;
        return (new Timecode((long)Math.Round(frames), timebase, df, num, den), frames);
    }

    /// <summary>The standard rate a measured frame rate belongs to.</summary>
    public static (int Num, int Den) ExactRate(double fps)
    {
        foreach (var (n, d) in new[] { (24000, 1001), (30000, 1001), (60000, 1001) })
            if (Math.Abs(fps - (double)n / d) < 0.01) return (n, d);
        return ((int)Math.Round(fps), 1);
    }
}
