using System.Globalization;
using MlvDrive.Core.Audio;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.Testing;

// mlvdrive: command-line companion to MLV Drive.
//   info CLIP.MLV                       what the clip contains and how it will be shown
//   export CLIP.MLV OUTDIR [--fps N] [--latency-ms N]
//                                       write the clip as a CinemaDNG folder (+ WAV) to disk
//   testclip OUTDIR [--bpp 12]          write a synthetic colour-bar clip for checking an editor
//   drift CLIP.MLV|FILE.WAV [--step 10] follow a scratch WAV's LTC through the take: the camera's
//                                       clock against the timecode generator's as it warms up

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();
    try
    {
        return args[0] switch
        {
            "info" when args.Length >= 2 => Info(args[1], Option(args, "--latency-ms") ?? 0),
            "export" when args.Length >= 3 => Export(args[1], args[2], Option(args, "--fps"), Option(args, "--latency-ms") ?? 0),
            "testclip" when args.Length >= 2 => TestClip(args[1], (int)(Option(args, "--bpp") ?? 12)),
            "drift" when args.Length >= 2 => Drift(args[1], Option(args, "--step") ?? 10),
            "tree" when args.Length >= 2 => Tree(args[1]),
            "bench" when args.Length >= 2 => Bench(args[1]),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("usage: mlvdrive info CLIP.MLV [--latency-ms N] | export CLIP.MLV OUTDIR [--fps N] [--latency-ms N] | testclip OUTDIR [--bpp 10|12|14] | drift CLIP.MLV|FILE.WAV [--step 10]");
    return 2;
}

static double? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], CultureInfo.InvariantCulture) : null;
}

static int Info(string path, double latencyMs)
{
    using var clip = MlvClip.Open(path);
    var r = clip.Rawi;
    var (num, den) = clip.FrameRate();
    Console.WriteLine($"{clip.BaseName}: {clip.FilePaths.Count} file(s), {clip.Frames.Count} frames at {(double)num / den:0.###} fps{(clip.Header.IsLj92 ? ", lossless (LJ92)" : "")}");
    Console.WriteLine($"  camera   {clip.Idnt?.CameraName ?? "?"}");
    if (r is { } ri)
        Console.WriteLine($"  frame    {ri.XRes}x{ri.YRes} {ri.BitsPerPixel}-bit, black {ri.BlackLevel}, white {ri.WhiteLevel}");
    if (clip.Expo is { } e) Console.WriteLine($"  exposure ISO {e.IsoValue}, 1/{(e.ShutterMicroseconds > 0 ? 1_000_000.0 / e.ShutterMicroseconds : 0):0}");
    if (clip.Lens is { } l) Console.WriteLine($"  lens     {l.LensName} {l.FocalLength}mm f/{l.ApertureX100 / 100.0:0.0}");
    var (audio, scratch, start) = ClipAudioSource.For(clip, path, new DngOptions(ScratchLatencyMs: latencyMs));
    Console.WriteLine($"  audio    {(audio is EmbeddedWav w ? $"embedded, {w.Size / 1e6:0.0} MB" : scratch is { } sc ? $"scratch WAV {PassThroughFile.FindScratchWav(path)}, aligned to frame 1 (WAV starts {sc.LeadSeconds:0.000} s earlier)" : audio is not null ? "scratch WAV " + PassThroughFile.FindScratchWav(path) + " (no start time in the clip: shown as recorded)" : "none")}");
    if (scratch?.StartTimecode is { } tc)
        Console.WriteLine($"  timecode {tc} at frame 1 from LTC on the {(scratch.LtcChannel == 0 ? "left" : scratch.LtcChannel == 1 ? "right" : $"#{scratch.LtcChannel + 1}")} channel, {tc.Fps:0.###} fps ({scratch.SubFrame:+0.00;-0.00} frame){(start is null ? " - not used for the DNGs (rate differs)" : "")}");
    if (scratch?.ClockPpm is { } ppm && scratch.DriftSeconds is { } drift)
        Console.WriteLine($"  clock    camera {ppm:+0.0;-0.0} ppm against the timecode: {drift * num / den:+0.00;-0.00} frames by the last frame ({drift * 1000:+0.0;-0.0} ms)");
    if (clip.Unsupported is { } why) Console.WriteLine($"  NOT SHOWN: {why}");
    else
    {
        var seq = new DngSequence(clip);
        Console.WriteLine($"  colour   {seq.Profile.Name} ({seq.ProfileSource})");
        Console.WriteLine($"  DNG      {seq.FileSize / 1e6:0.0} MB per frame");
    }
    foreach (var warning in clip.Warnings) Console.WriteLine($"  warning  {warning}");
    return clip.Unsupported is null ? 0 : 1;
}

static int Export(string path, string outDir, double? fps, double latencyMs)
{
    using var clip = MlvClip.Open(path);
    if (clip.Unsupported is { } why) throw new NotSupportedException(why);
    var options = new DngOptions(fps, latencyMs);
    var (audio, _, start) = ClipAudioSource.For(clip, path, options);
    var seq = new DngSequence(clip, options, start);
    var folder = Path.Combine(outDir, clip.BaseName);
    Directory.CreateDirectory(folder);
    var buffer = new byte[seq.FileSize];
    for (int i = 0; i < seq.FrameCount; i++)
    {
        seq.Read(i, 0, buffer);
        File.WriteAllBytes(Path.Combine(folder, $"{clip.BaseName}_{i:000000}.dng"), buffer);
        if (i % 24 == 0) Console.Write($"\r{i + 1}/{seq.FrameCount}");
    }
    if (audio is not null)
    {
        var bytes = new byte[audio.Size];
        audio.Read(0, bytes);
        File.WriteAllBytes(Path.Combine(folder, clip.BaseName + ".wav"), bytes);
    }
    Console.WriteLine($"\r{seq.FrameCount} frames -> {folder}{(audio is null ? "" : " (+ wav)")}");
    return 0;
}

static int Drift(string path, double step)
{
    string wav = path.EndsWith(".MLV", StringComparison.OrdinalIgnoreCase)
        ? PassThroughFile.FindScratchWav(path) ?? throw new FileNotFoundException("no scratch WAV for " + path)
        : path;
    if (ScratchWav.ReadChannels(wav) is not { } w) throw new InvalidDataException(wav + ": not a 16-bit PCM WAV");
    for (int c = 0; c < w.Channels.Length; c++)
    {
        var points = Ltc.Track(w.Channels[c], w.Rate, step);
        if (points.Count < 2) continue;
        var first = points[0];
        Console.WriteLine($"{Path.GetFileName(wav)}: LTC on the {(c == 0 ? "left" : c == 1 ? "right" : $"#{c + 1}")} channel at {first.Tc.Fps:0.###} fps, {points.Count} points");
        Console.WriteLine("  WAV time   timecode       camera ahead   ppm since last");
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double ahead = (p.RecorderSeconds - first.RecorderSeconds) - (p.TimecodeSeconds - first.TimecodeSeconds);
            string local = i == 0 ? "" : $"{(((p.RecorderSeconds - points[i - 1].RecorderSeconds) / (p.TimecodeSeconds - points[i - 1].TimecodeSeconds)) - 1) * 1e6,10:+0.0;-0.0}";
            Console.WriteLine($"  {p.RecorderSeconds,8:0.0} s  {p.Tc}  {ahead * 1000,9:+0.00;-0.00} ms  {local}");
        }
        var last = points[^1];
        double total = (last.RecorderSeconds - first.RecorderSeconds) - (last.TimecodeSeconds - first.TimecodeSeconds);
        Console.WriteLine($"camera clock {Ltc.ClockPpm(points):+0.0;-0.0} ppm against the timecode (best fit): {total * 1000:+0.0;-0.0} ms over {last.TimecodeSeconds - first.TimecodeSeconds:0} s, {total * first.Tc.Fps:+0.00;-0.00} frames");
        return 0;
    }
    Console.WriteLine($"{Path.GetFileName(wav)}: no LTC on any channel");
    return 1;
}

static int Bench(string path)
{
    using var clip = MlvClip.Open(path);
    var seq = new DngSequence(clip);
    var buffer = new byte[64 * 1024];
    int frames = Math.Min(seq.FrameCount, 96);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int f = 0; f < frames; f++)
        for (long pos = 0; pos < seq.FileSize; pos += buffer.Length)
            seq.Read(f, pos, buffer);
    double s = sw.Elapsed.TotalSeconds;
    long source = clip.Frames.Take(frames).Sum(f => (long)f.PayloadSize);
    Console.WriteLine($"{frames} frames in 64 KB reads: {frames / s:0.0} fps ({seq.FileSize * frames / s / 1e6:0} MB/s of DNG, {source / s / 1e6:0} MB/s from the clip{(clip.Header.IsLj92 ? ", lossless" : "")})");
    return 0;
}

static int Tree(string root)
{
    using var cache = new MlvDrive.Core.VirtualFs.ClipCache();
    var tree = MlvDrive.Core.VirtualFs.SourceScanner.Build(new[] { new MlvDrive.Core.VirtualFs.ClipSource(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } n ? n : "Drive", root) }, cache);
    void Print(MlvDrive.Core.VirtualFs.VNode node, string indent)
    {
        if (node is MlvDrive.Core.VirtualFs.ClipDirectory c)
        {
            var files = c.Children();
            Console.WriteLine($"{indent}{c.Name}\\  ({files.Count(f => f.Name.EndsWith(".dng"))} dng{(files.Any(f => f.Name.EndsWith(".wav")) ? " + wav" : "")}){(c.Problem is { } p ? "  ! " + p : "")}");
            return;
        }
        Console.WriteLine($"{indent}{node.Name}\\");
        if (node is MlvDrive.Core.VirtualFs.VDirectory d)
            foreach (var child in d.Children()) Print(child, indent + "  ");
    }
    foreach (var top in tree.Children()) Print(top, "");
    return 0;
}

static int TestClip(string outDir, int bpp)
{
    var synthetic = new SyntheticClip
    {
        Width = 1920, Height = 804, Bpp = bpp,
        Black = 2047 >> (14 - bpp), White = 16200 >> (14 - bpp),
        WithAudio = true,
    };
    synthetic.UseColourBars();
    var path = synthetic.Write(outDir, $"T{bpp:00}-BARS", new[] { 24, 24 });
    Console.WriteLine($"wrote {path} (+ .M00): 48 frames 1920x804 {bpp}-bit, colour bars, embedded audio");
    return 0;
}
